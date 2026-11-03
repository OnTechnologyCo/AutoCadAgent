using System;
using System.Collections.Generic;
using System.IO;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // Phase 3 도구 모음.
    //
    // 편집(trim/extend/break/fillet/chamfer/join), 메타데이터 조회(레이어/블록/스타일 목록),
    // 외부 참조(xref attach/detach), 2D 헬릭스(나선), 그룹(group create/dissolve).
    //
    // 모든 즉시 처리 도구는 활성 도면 + LockDocument + 트랜잭션 패턴.

    // ───────── 편집 ─────────

    public class DrawTrimTool : ChatTool
    {
        public override string Name => "draw.trim";
        public override string Description => "Curve 양단을 절단점에서 잘라낸다. handle 단일 + 절단점(x,y) — 가까운 끝쪽을 자른다.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
            new ToolParam { Name = "x",      Type = "number", Required = true, Description = "절단 위치 X" },
            new ToolParam { Name = "y",      Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var c = tr.GetObject(id, OpenMode.ForWrite) as Curve;
                if (c == null) { tr.Abort(); return ToolResult.Fail("Curve 가 아님"); }
                try
                {
                    var p = c.GetClosestPointTo(new Point3d(x, y, 0), false);
                    var param = c.GetParameterAtPoint(p);
                    var startParam = c.StartParam;
                    var endParam = c.EndParam;
                    // 가까운 끝쪽을 자름 — start↔p 간격이 짧으면 시작쪽, 아니면 끝쪽
                    var splitPts = new DoubleCollection { param };
                    var pieces = c.GetSplitCurves(splitPts);
                    if (pieces == null || pieces.Count != 2) { tr.Abort(); return ToolResult.Fail("split 실패"); }
                    // 두 조각 중 더 긴 것을 남기고 원본 교체
                    Curve longer = (pieces[0] as Curve);
                    if (pieces[1] is Curve c2 && c2.GetDistanceAtParameter(c2.EndParam) > longer.GetDistanceAtParameter(longer.EndParam))
                        longer = c2;
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    longer.Layer = c.Layer;
                    longer.ColorIndex = c.ColorIndex;
                    ms.AppendEntity(longer);
                    tr.AddNewlyCreatedDBObject(longer, true);
                    var newHandle = longer.Handle.Value.ToString("X");
                    c.Erase();
                    tr.Commit();
                    return ToolResult.Ok($"trim → 새 핸들 {newHandle}");
                }
                catch (Exception ex) { tr.Abort(); return ToolResult.Fail($"trim 실패: {ex.Message}"); }
            }
        }
    }

    public class DrawExtendTool : ChatTool
    {
        public override string Name => "draw.extend";
        public override string Description => "Line/Arc 의 한쪽 끝을 (x,y) 까지 연장. handle 단일.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
            new ToolParam { Name = "x",      Type = "number", Required = true },
            new ToolParam { Name = "y",      Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(id, OpenMode.ForWrite);
                var target = new Point3d(x, y, 0);
                if (ent is Line ln)
                {
                    // 가까운 쪽 끝을 target 으로 옮김
                    if (ln.StartPoint.DistanceTo(target) < ln.EndPoint.DistanceTo(target))
                        ln.StartPoint = target;
                    else
                        ln.EndPoint = target;
                    tr.Commit();
                    return ToolResult.Ok($"extend(line) → ({x:0.##},{y:0.##})");
                }
                else
                {
                    tr.Abort();
                    return ToolResult.Fail("extend 는 Line 만 지원 (Arc/Polyline 은 trim+addvertex 권장)");
                }
            }
        }
    }

    public class DrawBreakTool : ChatTool
    {
        public override string Name => "draw.break";
        public override string Description => "Curve 를 두 점 (x1,y1)~(x2,y2) 사이를 잘라내고 두 조각으로 분리.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
            new ToolParam { Name = "x1",     Type = "number", Required = true },
            new ToolParam { Name = "y1",     Type = "number", Required = true },
            new ToolParam { Name = "x2",     Type = "number", Required = true },
            new ToolParam { Name = "y2",     Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var c = tr.GetObject(id, OpenMode.ForWrite) as Curve;
                if (c == null) { tr.Abort(); return ToolResult.Fail("Curve 아님"); }
                try
                {
                    var p1 = c.GetClosestPointTo(new Point3d(x1, y1, 0), false);
                    var p2 = c.GetClosestPointTo(new Point3d(x2, y2, 0), false);
                    double t1 = c.GetParameterAtPoint(p1);
                    double t2 = c.GetParameterAtPoint(p2);
                    if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }
                    var pieces = c.GetSplitCurves(new DoubleCollection { t1, t2 });
                    if (pieces == null || pieces.Count < 2) { tr.Abort(); return ToolResult.Fail("split 실패"); }
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var newHandles = new List<string>();
                    // pieces[0] = 시작~t1, pieces[1] = t1~t2(잘라냄), pieces[2] = t2~끝
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        if (i == 1) continue;  // 가운데 조각 버림
                        if (!(pieces[i] is Curve seg)) continue;
                        seg.Layer = c.Layer;
                        seg.ColorIndex = c.ColorIndex;
                        ms.AppendEntity(seg);
                        tr.AddNewlyCreatedDBObject(seg, true);
                        newHandles.Add(seg.Handle.Value.ToString("X"));
                    }
                    c.Erase();
                    tr.Commit();
                    return ToolResult.Ok($"break → 핸들 {string.Join(",", newHandles)}");
                }
                catch (Exception ex) { tr.Abort(); return ToolResult.Fail($"break 실패: {ex.Message}"); }
            }
        }
    }

    public class DrawFilletTool : ChatTool
    {
        public override string Name => "draw.fillet";
        public override string Description => "두 Line 의 교차각을 반지름 r 호로 둥글림. handles 두 개 + r.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true, Description = "Line 두 개의 hex" },
            new ToolParam { Name = "r",       Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "r", out var r) || r <= 0) return ToolResult.Fail("r 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null || ids.Count != 2) return ToolResult.Fail("handles 는 정확히 2개 필요");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var l1 = tr.GetObject(ids[0], OpenMode.ForWrite) as Line;
                var l2 = tr.GetObject(ids[1], OpenMode.ForWrite) as Line;
                if (l1 == null || l2 == null) { tr.Abort(); return ToolResult.Fail("두 Line 만 지원"); }

                // 교차점 계산 (무한 라인으로 가정)
                var p1 = l1.StartPoint;
                var p2 = l1.EndPoint;
                var p3 = l2.StartPoint;
                var p4 = l2.EndPoint;
                double a1 = p2.Y - p1.Y, b1 = p1.X - p2.X, c1 = a1 * p1.X + b1 * p1.Y;
                double a2 = p4.Y - p3.Y, b2 = p3.X - p4.X, c2 = a2 * p3.X + b2 * p3.Y;
                double det = a1 * b2 - a2 * b1;
                if (Math.Abs(det) < 1e-9) { tr.Abort(); return ToolResult.Fail("두 line 평행"); }
                double ix = (b2 * c1 - b1 * c2) / det;
                double iy = (a1 * c2 - a2 * c1) / det;
                var inter = new Point3d(ix, iy, 0);

                // 각 라인의 교차점→방향(반대 끝점쪽)으로 r 만큼 떨어진 점이 호 시작/끝
                Vector3d v1 = ((p1.DistanceTo(inter) > p2.DistanceTo(inter)) ? p1 - inter : p2 - inter).GetNormal();
                Vector3d v2 = ((p3.DistanceTo(inter) > p4.DistanceTo(inter)) ? p3 - inter : p4 - inter).GetNormal();
                var arcStart = inter + v1 * r;
                var arcEnd = inter + v2 * r;
                // 호 중심 = 두 방향 사이 이등분선 위 + r/sin(θ/2)
                double cosTh = v1.DotProduct(v2);
                cosTh = Math.Max(-1, Math.Min(1, cosTh));
                double th = Math.Acos(cosTh);
                if (th < 1e-6 || Math.Abs(th - Math.PI) < 1e-6) { tr.Abort(); return ToolResult.Fail("두 line 정렬됨"); }
                var bisector = (v1 + v2).GetNormal();
                double centerDist = r / Math.Sin(th / 2);
                var center = inter + bisector * centerDist;

                // 라인을 호 시작/끝까지 줄임
                if (l1.StartPoint.DistanceTo(inter) < l1.EndPoint.DistanceTo(inter)) l1.StartPoint = arcStart;
                else l1.EndPoint = arcStart;
                if (l2.StartPoint.DistanceTo(inter) < l2.EndPoint.DistanceTo(inter)) l2.StartPoint = arcEnd;
                else l2.EndPoint = arcEnd;

                // 호 생성
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var vs = (arcStart - center);
                var ve = (arcEnd - center);
                double ang1 = Math.Atan2(vs.Y, vs.X);
                double ang2 = Math.Atan2(ve.Y, ve.X);
                var arc = new Arc(center, r, ang1, ang2);
                arc.Layer = l1.Layer;
                ms.AppendEntity(arc);
                tr.AddNewlyCreatedDBObject(arc, true);
                var ah = arc.Handle.Value.ToString("X");
                tr.Commit();
                return ToolResult.Ok($"fillet r={r:0.###} → 호 {ah}");
            }
        }
    }

    public class DrawChamferTool : ChatTool
    {
        public override string Name => "draw.chamfer";
        public override string Description => "두 Line 의 교차각을 d1, d2 거리만큼 모따기. handles 두 개 + d1, d2.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "d1",      Type = "number", Required = true },
            new ToolParam { Name = "d2",      Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "d1", out var d1) || d1 <= 0) return ToolResult.Fail("d1 누락");
            if (!DrawHelpers.TryGetDouble(args, "d2", out var d2) || d2 <= 0) return ToolResult.Fail("d2 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null || ids.Count != 2) return ToolResult.Fail("handles 는 정확히 2개 필요");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var l1 = tr.GetObject(ids[0], OpenMode.ForWrite) as Line;
                var l2 = tr.GetObject(ids[1], OpenMode.ForWrite) as Line;
                if (l1 == null || l2 == null) { tr.Abort(); return ToolResult.Fail("두 Line 만 지원"); }

                var p1 = l1.StartPoint; var p2 = l1.EndPoint;
                var p3 = l2.StartPoint; var p4 = l2.EndPoint;
                double a1 = p2.Y - p1.Y, b1 = p1.X - p2.X, c1 = a1 * p1.X + b1 * p1.Y;
                double a2 = p4.Y - p3.Y, b2 = p3.X - p4.X, c2 = a2 * p3.X + b2 * p3.Y;
                double det = a1 * b2 - a2 * b1;
                if (Math.Abs(det) < 1e-9) { tr.Abort(); return ToolResult.Fail("두 line 평행"); }
                var inter = new Point3d((b2 * c1 - b1 * c2) / det, (a1 * c2 - a2 * c1) / det, 0);

                Vector3d v1 = ((p1.DistanceTo(inter) > p2.DistanceTo(inter)) ? p1 - inter : p2 - inter).GetNormal();
                Vector3d v2 = ((p3.DistanceTo(inter) > p4.DistanceTo(inter)) ? p3 - inter : p4 - inter).GetNormal();
                var ch1 = inter + v1 * d1;
                var ch2 = inter + v2 * d2;

                if (l1.StartPoint.DistanceTo(inter) < l1.EndPoint.DistanceTo(inter)) l1.StartPoint = ch1;
                else l1.EndPoint = ch1;
                if (l2.StartPoint.DistanceTo(inter) < l2.EndPoint.DistanceTo(inter)) l2.StartPoint = ch2;
                else l2.EndPoint = ch2;

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var seg = new Line(ch1, ch2) { Layer = l1.Layer };
                ms.AppendEntity(seg);
                tr.AddNewlyCreatedDBObject(seg, true);
                var sh = seg.Handle.Value.ToString("X");
                tr.Commit();
                return ToolResult.Ok($"chamfer d1={d1:0.###} d2={d2:0.###} → 모따기 라인 {sh}");
            }
        }
    }

    public class DrawJoinTool : ChatTool
    {
        public override string Name => "draw.join";
        public override string Description => "여러 Curve 를 하나의 Polyline 으로 결합 (끝점이 일치하거나 매우 가까울 때).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array", Required = true, Description = "Curve hex 배열 (2개 이상)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null || ids.Count < 2) return ToolResult.Fail("handles 는 2개 이상 필요");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var first = tr.GetObject(ids[0], OpenMode.ForWrite) as Curve;
                if (first == null) { tr.Abort(); return ToolResult.Fail("첫 핸들이 Curve 아님"); }
                int joined = 0;
                for (int i = 1; i < ids.Count; i++)
                {
                    var nxt = tr.GetObject(ids[i], OpenMode.ForWrite) as Curve;
                    if (nxt == null) continue;
                    try { first.JoinEntity(nxt); nxt.Erase(); joined++; }
                    catch { /* 끝점 미일치 등 — 건너뜀 */ }
                }
                tr.Commit();
                return ToolResult.Ok($"join: {joined}개 결합 → {first.Handle.Value:X}");
            }
        }
    }

    // ───────── 메타데이터 조회 ─────────

    public class LayerListTool : ChatTool
    {
        public override string Name => "layer.list";
        public override string Description => "도면의 모든 레이어 목록 (이름 / 색상 / 동결 / 잠금 상태).";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    lines.Add($"{rec.Name} color={rec.Color.ColorIndex} frozen={rec.IsFrozen} locked={rec.IsLocked} off={rec.IsOff}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"레이어 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }

    public class BlockListTool : ChatTool
    {
        public override string Name => "block.list";
        public override string Description => "도면에 정의된 블록 목록 (이름, 익명/외부참조 표시).";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var rec = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (rec.IsLayout) continue;  // ModelSpace, PaperSpace 제외
                    var marks = new List<string>();
                    if (rec.IsAnonymous) marks.Add("anon");
                    if (rec.IsFromExternalReference) marks.Add("xref");
                    if (rec.HasAttributeDefinitions) marks.Add("attdef");
                    lines.Add($"{rec.Name}{(marks.Count > 0 ? " [" + string.Join(",", marks) + "]" : "")}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"블록 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }

    public class TextStyleListTool : ChatTool
    {
        public override string Name => "textstyle.list";
        public override string Description => "도면의 모든 텍스트 스타일 목록 (이름, 폰트, 높이).";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ts = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in ts)
                {
                    var rec = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    lines.Add($"{rec.Name} font={rec.FileName} h={rec.TextSize} wf={rec.XScale}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"텍스트 스타일 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }

    public class DimStyleListTool : ChatTool
    {
        public override string Name => "dimstyle.list";
        public override string Description => "도면의 모든 치수 스타일 목록.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dst = (DimStyleTable)tr.GetObject(doc.Database.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dst)
                {
                    var rec = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    lines.Add($"{rec.Name} scale={rec.Dimscale} arrow={rec.Dimasz} h={rec.Dimtxt}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"치수 스타일 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }

    public class LinetypeListTool : ChatTool
    {
        public override string Name => "linetype.list";
        public override string Description => "도면의 모든 선종류(Linetype) 목록.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LinetypeTable)tr.GetObject(doc.Database.LinetypeTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var rec = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    lines.Add($"{rec.Name}: {rec.AsciiDescription}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"선종류 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }

    // ───────── Xref ─────────

    public class XrefAttachTool : ChatTool
    {
        public override string Name => "xref.attach";
        public override string Description => "DWG 파일을 외부참조(Xref)로 첨부. (x,y) 위치 + scale + rotation(도) 옵션.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "path",     Type = "string", Required = true, Description = "DWG 파일 절대경로" },
            new ToolParam { Name = "x",        Type = "number", Required = true },
            new ToolParam { Name = "y",        Type = "number", Required = true },
            new ToolParam { Name = "scale",    Type = "number", Required = false, Description = "기본 1" },
            new ToolParam { Name = "rotation", Type = "number", Required = false, Description = "회전(도). 기본 0" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var path = DrawHelpers.GetString(args, "path");
            if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path 누락");
            if (!File.Exists(path)) return ToolResult.Fail($"파일 없음: {path}");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            DrawHelpers.TryGetDouble(args, "scale", out var sc); if (sc <= 0) sc = 1;
            DrawHelpers.TryGetDouble(args, "rotation", out var rotDeg);

            var bn = Path.GetFileNameWithoutExtension(path);
            string newHandle;
            using (doc.LockDocument())
            {
                ObjectId xrefId;
                try { xrefId = doc.Database.AttachXref(path, bn); }
                catch (Exception ex) { return ToolResult.Fail($"AttachXref 실패: {ex.Message}"); }
                if (xrefId.IsNull) return ToolResult.Fail("Xref 첨부 실패");

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    using (var bref = new BlockReference(new Point3d(x, y, 0), xrefId))
                    {
                        bref.ScaleFactors = new Scale3d(sc);
                        bref.Rotation = DrawHelpers.Deg2Rad(rotDeg);
                        ms.AppendEntity(bref);
                        tr.AddNewlyCreatedDBObject(bref, true);
                        newHandle = bref.Handle.Value.ToString("X");
                    }
                    tr.Commit();
                }
            }
            return ToolResult.Ok($"xref.attach '{bn}' @ ({x:0.##},{y:0.##}) → {newHandle}");
        }
    }

    public class XrefDetachTool : ChatTool
    {
        public override string Name => "xref.detach";
        public override string Description => "Xref 분리. block_name 으로 지정.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "block_name", Type = "string", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var bn = DrawHelpers.GetString(args, "block_name");
            if (string.IsNullOrWhiteSpace(bn)) return ToolResult.Fail("block_name 누락");
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(bn)) { tr.Abort(); return ToolResult.Fail($"블록 '{bn}' 없음"); }
                try
                {
                    doc.Database.DetachXref(bt[bn]);
                    tr.Commit();
                    return ToolResult.Ok($"xref.detach '{bn}'");
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    return ToolResult.Fail($"detach 실패 (xref 가 아니거나 사용 중): {ex.Message}");
                }
            }
        }
    }

    // ───────── 2D 헬릭스(나선) ─────────

    public class DrawHelix2DTool : ChatTool
    {
        public override string Name => "draw.helix_2d";
        public override string Description => "2D 나선 — 폴리라인으로 근사. 중심(cx,cy) + 시작 반지름 r0 + 끝 반지름 r1 + 회전수 turns.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",              Type = "number", Required = true },
            new ToolParam { Name = "cy",              Type = "number", Required = true },
            new ToolParam { Name = "r0",              Type = "number", Required = true, Description = "시작 반지름" },
            new ToolParam { Name = "r1",              Type = "number", Required = true, Description = "끝 반지름" },
            new ToolParam { Name = "turns",           Type = "number", Required = true, Description = "회전수 (소수 가능)" },
            new ToolParam { Name = "segments_per_turn", Type = "number", Required = false, Description = "회전당 세그먼트 수 (기본 36)" },
            new ToolParam { Name = "start_angle",     Type = "number", Required = false, Description = "시작각(도). 기본 0" },
            new ToolParam { Name = "layer",           Type = "string", Required = false },
            new ToolParam { Name = "color",           Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "r0", out var r0) || r0 < 0) return ToolResult.Fail("r0 누락");
            if (!DrawHelpers.TryGetDouble(args, "r1", out var r1) || r1 < 0) return ToolResult.Fail("r1 누락");
            if (!DrawHelpers.TryGetDouble(args, "turns", out var turns) || turns <= 0) return ToolResult.Fail("turns 누락");
            if (!DrawHelpers.TryGetInt(args, "segments_per_turn", out var spt) || spt < 6) spt = 36;
            DrawHelpers.TryGetDouble(args, "start_angle", out var sa);

            int total = (int)Math.Ceiling(spt * turns);
            var pts = new List<Point2d>();
            for (int i = 0; i <= total; i++)
            {
                double t = (double)i / total;
                double r = r0 + (r1 - r0) * t;
                double ang = DrawHelpers.Deg2Rad(sa + 360.0 * turns * t);
                pts.Add(new Point2d(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
            var s = new DrawSession.PolylineShape { Points = pts, Closed = false };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"helix_2d r0={r0:0.##}→r1={r1:0.##} turns={turns} ({total}점) 추가. 누적 {DrawSession.Count}개");
        }
    }

    // ───────── 그룹 ─────────

    public class GroupCreateTool : ChatTool
    {
        public override string Name => "group.create";
        public override string Description => "여러 엔티티를 그룹으로 묶어 단일 단위처럼 선택/이동. 동명 그룹 있으면 실패.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",        Type = "string", Required = true },
            new ToolParam { Name = "handles",     Type = "array",  Required = true },
            new ToolParam { Name = "description", Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);
            var desc = DrawHelpers.GetString(args, "description", "");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var gd = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                if (gd.Contains(name)) { tr.Abort(); return ToolResult.Fail($"그룹 '{name}' 이미 존재"); }
                var grp = new Group(desc, true);
                gd.SetAt(name, grp);
                tr.AddNewlyCreatedDBObject(grp, true);
                var idColl = new ObjectIdCollection();
                foreach (var id in ids) idColl.Add(id);
                grp.Append(idColl);
                tr.Commit();
            }
            return ToolResult.Ok($"group.create '{name}' ({ids.Count}개)");
        }
    }

    public class GroupDissolveTool : ChatTool
    {
        public override string Name => "group.dissolve";
        public override string Description => "그룹 해체 (구성 엔티티는 유지).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name", Type = "string", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var gd = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                if (!gd.Contains(name)) { tr.Abort(); return ToolResult.Fail($"그룹 '{name}' 없음"); }
                var gid = gd.GetAt(name);
                var grp = (Group)tr.GetObject(gid, OpenMode.ForWrite);
                grp.Erase();
                tr.Commit();
            }
            return ToolResult.Ok($"group.dissolve '{name}'");
        }
    }

    public class GroupListTool : ChatTool
    {
        public override string Name => "group.list";
        public override string Description => "도면의 모든 그룹 목록.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var gd = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry kv in gd)
                {
                    var grp = tr.GetObject(kv.Value, OpenMode.ForRead) as Group;
                    if (grp == null) continue;
                    lines.Add($"{kv.Key} ({grp.NumEntities}개) {grp.Description}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"그룹 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }
}


