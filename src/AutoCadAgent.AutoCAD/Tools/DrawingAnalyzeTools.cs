using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // 범용 DWG 도면 분석과 수정 도구.
    // 모든 도구는 ModelSpace 직접 읽기 (DXF 변환 X — 토큰 폭발 방지).

    internal static class _AnalyzeHelp
    {
        public static Document Doc()
        {
            return Application.DocumentManager.MdiActiveDocument;
        }

        public static string Pt(Point3d p) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F1},{1:F1})", p.X, p.Y);

        public static IEnumerable<ObjectId> IterModelSpace(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var ids = new List<ObjectId>();
                foreach (ObjectId id in ms) ids.Add(id);
                tr.Commit();
                return ids;
            }
        }

        // 핸들 문자열 (16진) → ObjectId
        public static bool TryHandleToId(Database db, string hex, out ObjectId id)
        {
            id = ObjectId.Null;
            try
            {
                var h = new Handle(Convert.ToInt64(hex, 16));
                id = db.GetObjectId(false, h, 0);
                return !id.IsNull && id.IsValid && !id.IsErased;
            }
            catch { return false; }
        }
    }

    // ───────── 조회 ─────────

    public class DrawingSummaryFullTool : ChatTool
    {
        public override string Name => "drawing.summary_full";
        public override string Description =>
            "현재 도면 상세 요약 — 엔티티 수 / 타입별 / 레이어별 / 색상 / 도면 extents (좌하/우상). 임의 도면 분석의 첫 단계.";
        public override IList<ToolParam> Parameters => new ToolParam[0];

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc();
            if (doc == null) return ToolResult.Fail("AutoCAD 활성 도면 없음.");
            var db = doc.Database;
            var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var byLayer = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var byColor = new SortedDictionary<int, int>();
            int total = 0, unavailableBounds = 0;
            Extents3d? actualBounds = null;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    total++;
                    try { var bounds = ent.GeometricExtents; if (actualBounds == null) actualBounds = bounds; else { var union = actualBounds.Value; union.AddExtents(bounds); actualBounds = union; } }
                    catch { unavailableBounds++; }
                    var t = ent.GetType().Name;
                    byType.TryGetValue(t, out int cv); byType[t] = cv + 1;
                    var layer = ent.Layer ?? "0";
                    byLayer.TryGetValue(layer, out int cl); byLayer[layer] = cl + 1;
                    var ci = ent.ColorIndex;
                    byColor.TryGetValue(ci, out int cc); byColor[ci] = cc + 1;
                }
                tr.Commit();
            }
            var sb = new StringBuilder();
            sb.Append("총 엔티티: ").Append(total).AppendLine();
            sb.Append("extents: ");
            try
            {
                var min = db.Extmin; var max = db.Extmax;
                sb.Append(_AnalyzeHelp.Pt(min)).Append(" ~ ").Append(_AnalyzeHelp.Pt(max)).AppendLine();
            }
            catch { sb.AppendLine("(미계산)"); }
            sb.Append("타입별:"); foreach (var kv in byType) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value).Append(","); sb.AppendLine();
            sb.Append("레이어별:"); foreach (var kv in byLayer) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value).Append(","); sb.AppendLine();
            sb.Append("색상별:"); foreach (var kv in byColor) sb.Append(" idx").Append(kv.Key).Append("=").Append(kv.Value).Append(",");

            return ToolResult.Ok(sb.ToString().TrimEnd(','), new { count = total, byType, byLayer, byColor = byColor.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value), coordinateSystem = "WCS", bounds = actualBounds.HasValue ? GeometrySnapshot.Bounds(actualBounds.Value) : null, unavailableBounds });
        }
    }

    public class DrawingListTextsTool : ChatTool
    {
        public override string Name => "drawing.list_texts";
        public override string Description =>
            "도면의 모든 MText / DBText 객체 — 핸들/위치/내용/레이어. 임의 도면에서 의미 추론에 가장 유용.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "max", Type = "number", Description = "최대 출력 (기본 200)" },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            int max = 200; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    string text = null; Point3d pos = Point3d.Origin;
                    if (ent is MText mt) { text = mt.Contents; pos = mt.Location; }
                    else if (ent is DBText dt) { text = dt.TextString; pos = dt.Position; }
                    if (text == null) continue;
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(_AnalyzeHelp.Pt(pos))
                      .Append(" L=").Append(ent.Layer).Append(" : ").Append(text).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("텍스트 객체 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListDimensionsTool : ChatTool
    {
        public override string Name => "drawing.list_dimensions";
        public override string Description => "도면의 모든 치수 (Dimension) — 핸들/위치/측정값/레이어.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "max", Type = "number", Description = "최대 출력 (기본 200)" },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            int max = 200; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Dimension;
                    if (ent == null) continue;
                    var measure = "?";
                    try { measure = ent.Measurement.ToString("F2", CultureInfo.InvariantCulture); } catch { }
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(_AnalyzeHelp.Pt(ent.TextPosition))
                      .Append(" L=").Append(ent.Layer).Append(" m=").Append(measure)
                      .Append(" txt=").Append(ent.DimensionText ?? "").AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("치수 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListBlocksTool : ChatTool
    {
        public override string Name => "drawing.list_blocks";
        public override string Description => "도면의 모든 블록 참조 (BlockReference) — 블록 이름별 카운트와 첫 위치.";
        public override IList<ToolParam> Parameters => new ToolParam[0];

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var byName = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var firstPos = new Dictionary<string, Point3d>();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    var name = br.Name ?? "?";
                    byName.TryGetValue(name, out int c); byName[name] = c + 1;
                    if (!firstPos.ContainsKey(name)) firstPos[name] = br.Position;
                }
                tr.Commit();
            }
            if (byName.Count == 0) return ToolResult.Ok("블록 참조 없음.");
            var sb = new StringBuilder();
            foreach (var kv in byName)
                sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("개  첫 위치=").Append(_AnalyzeHelp.Pt(firstPos[kv.Key])).AppendLine();
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListInRegionTool : ChatTool
    {
        public override string Name => "drawing.list_in_region";
        public override string Description =>
            "지정 영역 (xMin,yMin) ~ (xMax,yMax) 안의 엔티티 목록. 도면 일부만 보고 싶을 때.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="xMin", Type="number", Required=true },
            new ToolParam{ Name="yMin", Type="number", Required=true },
            new ToolParam{ Name="xMax", Type="number", Required=true },
            new ToolParam{ Name="yMax", Type="number", Required=true },
            new ToolParam{ Name="max",  Type="number", Description="최대 출력 (기본 200)" },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var xMin = _ToolHelp.AsDouble(args, "xMin"); var yMin = _ToolHelp.AsDouble(args, "yMin");
            var xMax = _ToolHelp.AsDouble(args, "xMax"); var yMax = _ToolHelp.AsDouble(args, "yMax");
            if (!xMin.HasValue || !yMin.HasValue || !xMax.HasValue || !yMax.HasValue)
                return ToolResult.Fail("xMin/yMin/xMax/yMax 가 모두 필요합니다.");
            int max = 200; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    Extents3d ext;
                    try { ext = ent.GeometricExtents; } catch { continue; }
                    if (ext.MaxPoint.X < xMin.Value || ext.MinPoint.X > xMax.Value) continue;
                    if (ext.MaxPoint.Y < yMin.Value || ext.MinPoint.Y > yMax.Value) continue;
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(ent.GetType().Name)
                      .Append(" ").Append(_AnalyzeHelp.Pt(ext.MinPoint)).Append("~").Append(_AnalyzeHelp.Pt(ext.MaxPoint))
                      .Append(" L=").Append(ent.Layer).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("영역 안 엔티티 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListSelectedTool : ChatTool
    {
        public override string Name => "drawing.list_selected";
        public override string Description =>
            "AutoCAD 에서 명령 호출 전에 미리 선택해둔 엔티티 (PickFirstSet) 목록. 사용자가 '이거' 라고 가리킬 때 가장 정확.";
        public override IList<ToolParam> Parameters => new ToolParam[0];

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ed = doc.Editor;
            PromptSelectionResult res;
            try { res = ed.SelectImplied(); }
            catch (Exception ex) { return ToolResult.Fail("선택 조회 실패: " + ex.Message); }
            if (res.Status != PromptStatus.OK || res.Value == null)
                return ToolResult.Ok("미리 선택된 엔티티 없음. AutoCAD 에서 객체를 클릭 선택한 뒤 다시 호출하세요.");
            var ids = res.Value.GetObjectIds();
            var sb = new StringBuilder();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    Point3d pos = Point3d.Origin;
                    try { pos = ent.GeometricExtents.MinPoint; } catch { }
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(ent.GetType().Name)
                      .Append(" ").Append(_AnalyzeHelp.Pt(pos)).Append(" L=").Append(ent.Layer).AppendLine();
                }
                tr.Commit();
            }
            if (sb.Length == 0) return ToolResult.Ok("선택은 있으나 표시할 정보 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingEntityNeighborsTool : ChatTool
    {
        public override string Name => "drawing.entity_neighbors";
        public override string Description =>
            "한 엔티티(handle) 의 bounding box 주변 radius 안에 있는 다른 엔티티들 — 컨텍스트 파악.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handle", Type="string", Required=true },
            new ToolParam{ Name="radius", Type="number", Required=true, Description="반경(도면 단위, 보통 mm)" },
            new ToolParam{ Name="max",    Type="number", Description="최대 출력 (기본 100)" },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var hex = _ToolHelp.AsString(args, "handle");
            var radius = _ToolHelp.AsDouble(args, "radius");
            if (string.IsNullOrEmpty(hex) || !radius.HasValue) return ToolResult.Fail("handle / radius 필요.");
            int max = 100; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            if (!_AnalyzeHelp.TryHandleToId(doc.Database, hex, out var srcId))
                return ToolResult.Fail("유효하지 않은 핸들: " + hex);

            Extents3d srcExt;
            using (var trS = doc.Database.TransactionManager.StartTransaction())
            {
                var sEnt = trS.GetObject(srcId, OpenMode.ForRead) as Entity;
                if (sEnt == null) { trS.Commit(); return ToolResult.Fail("엔티티 아님."); }
                try { srcExt = sEnt.GeometricExtents; } catch (Exception ex) { trS.Commit(); return ToolResult.Fail("extents 실패: " + ex.Message); }
                trS.Commit();
            }
            var cx = (srcExt.MinPoint.X + srcExt.MaxPoint.X) / 2;
            var cy = (srcExt.MinPoint.Y + srcExt.MaxPoint.Y) / 2;
            double r = radius.Value;

            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (id == srcId || !id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    Extents3d ex2;
                    try { ex2 = ent.GeometricExtents; } catch { continue; }
                    var ex2cx = (ex2.MinPoint.X + ex2.MaxPoint.X) / 2;
                    var ex2cy = (ex2.MinPoint.Y + ex2.MaxPoint.Y) / 2;
                    var dx = ex2cx - cx; var dy = ex2cy - cy;
                    if (dx * dx + dy * dy > r * r) continue;
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(ent.GetType().Name)
                      .Append(" ").Append(_AnalyzeHelp.Pt(new Point3d(ex2cx, ex2cy, 0)))
                      .Append(" L=").Append(ent.Layer).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("주변 엔티티 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    // ───────── 수정 ─────────

    internal static class _ModifyHelp
    {
        public static List<ObjectId> ResolveHandles(Database db, IDictionary<string, object> args, out string err)
            => EditToolsExt.ResolveHandles(args, "handles", db, out err);
    }

    public class DrawingChangeColorTool : ChatTool
    {
        public override string Name => "drawing.change_color";
        public override string Description =>
            "지정한 핸들들의 ColorIndex 변경. AutoCAD ACI 색상 (1=빨강, 2=노랑, 3=초록, 4=청록, 5=파랑, 6=자홍, 7=흰/검, 256=BYLAYER 등).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handles", Type="array",  Required=true, Description="핸들 배열 또는 콤마 구분 문자열" },
            new ToolParam{ Name="color",   Type="number", Required=true, Description="ColorIndex (1~256)" },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ids = _ModifyHelp.ResolveHandles(doc.Database, args, out var err); if (err != null) return ToolResult.Fail(err);
            var color = _ToolHelp.AsDouble(args, "color"); if (!color.HasValue) return ToolResult.Fail("color 필요.");
            int ci = (int)color.Value;
            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.ColorIndex = ci;
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok("색상 변경: " + n + "개 → ColorIndex " + ci);
        }
    }

    public class DrawingChangeLayerTool : ChatTool
    {
        public override string Name => "drawing.change_layer";
        public override string Description => "지정한 핸들들의 Layer 변경. 레이어가 없으면 실패하므로 미리 생성된 레이어만 가능.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handles", Type="array",  Required=true },
            new ToolParam{ Name="layer",   Type="string", Required=true },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ids = _ModifyHelp.ResolveHandles(doc.Database, args, out var err); if (err != null) return ToolResult.Fail(err);
            var layer = _ToolHelp.AsString(args, "layer"); if (string.IsNullOrEmpty(layer)) return ToolResult.Fail("layer 필요.");
            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layer)) { tr.Commit(); return ToolResult.Fail("레이어 없음: " + layer); }
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.Layer = layer;
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok("레이어 변경: " + n + "개 → " + layer);
        }
    }

    public class DrawingDeleteEntitiesTool : ChatTool
    {
        public override string Name => "drawing.delete_entities";
        public override string Description => "지정한 핸들들 삭제. 사용 전 사용자 확인을 권장.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handles", Type="array", Required=true },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ids = _ModifyHelp.ResolveHandles(doc.Database, args, out var err); if (err != null) return ToolResult.Fail(err);
            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.Erase(); n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok("삭제: " + n + "개");
        }
    }

    // ───────── N 단계: 추론 친화 조회 + 미리보기 ─────────

    public class DrawingListPolylinesTool : ChatTool
    {
        public override string Name => "drawing.list_polylines";
        public override string Description =>
            "Polyline / Polyline2d / Polyline3d 필터링. closed (닫힘 여부), 꼭지점 수 범위, 면적 범위, 영역 (xMin/yMin/xMax/yMax) 으로 좁힘. " +
            "예: 사각형 = closed=true & minVertices=4 & maxVertices=4. 원 비슷한 것 = vertices 가 8 이상. 큰 외곽선 = areaMin 큰 값.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="closed",       Type="boolean", Description="true=닫힘만, false=열림만, 미지정=상관없음" },
            new ToolParam{ Name="minVertices",  Type="number" },
            new ToolParam{ Name="maxVertices",  Type="number" },
            new ToolParam{ Name="areaMin",      Type="number" },
            new ToolParam{ Name="areaMax",      Type="number" },
            new ToolParam{ Name="xMin",         Type="number" },
            new ToolParam{ Name="yMin",         Type="number" },
            new ToolParam{ Name="xMax",         Type="number" },
            new ToolParam{ Name="yMax",         Type="number" },
            new ToolParam{ Name="layer",        Type="string", Description="특정 레이어로 좁힐 때" },
            new ToolParam{ Name="max",          Type="number", Description="최대 출력 (기본 100)" },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            int max = 100; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            bool? closed = null;
            if (args != null && args.TryGetValue("closed", out var cv) && cv is bool cb) closed = cb;
            int? minV = null, maxV = null;
            var d = _ToolHelp.AsDouble(args, "minVertices"); if (d.HasValue) minV = (int)d.Value;
            d = _ToolHelp.AsDouble(args, "maxVertices");     if (d.HasValue) maxV = (int)d.Value;
            var areaMin = _ToolHelp.AsDouble(args, "areaMin");
            var areaMax = _ToolHelp.AsDouble(args, "areaMax");
            var xMin = _ToolHelp.AsDouble(args, "xMin"); var yMin = _ToolHelp.AsDouble(args, "yMin");
            var xMax = _ToolHelp.AsDouble(args, "xMax"); var yMax = _ToolHelp.AsDouble(args, "yMax");
            var layer = _ToolHelp.AsString(args, "layer");

            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!(ent is Polyline) && !(ent is Polyline2d) && !(ent is Polyline3d)) continue;
                    if (!string.IsNullOrEmpty(layer) && !string.Equals(ent.Layer, layer, StringComparison.Ordinal)) continue;

                    bool isClosed = false; int verts = 0; double area = 0;
                    if (ent is Polyline pl) { isClosed = pl.Closed; verts = pl.NumberOfVertices; try { area = pl.Area; } catch { } }
                    else if (ent is Polyline2d p2) { isClosed = p2.Closed; foreach (ObjectId vId in p2) verts++; }
                    else if (ent is Polyline3d p3) { isClosed = p3.Closed; foreach (ObjectId vId in p3) verts++; }
                    if (closed.HasValue && closed.Value != isClosed) continue;
                    if (minV.HasValue && verts < minV.Value) continue;
                    if (maxV.HasValue && verts > maxV.Value) continue;
                    if (areaMin.HasValue && Math.Abs(area) < areaMin.Value) continue;
                    if (areaMax.HasValue && Math.Abs(area) > areaMax.Value) continue;

                    Extents3d ex2;
                    try { ex2 = ent.GeometricExtents; } catch { continue; }
                    if (xMin.HasValue && ex2.MaxPoint.X < xMin.Value) continue;
                    if (xMax.HasValue && ex2.MinPoint.X > xMax.Value) continue;
                    if (yMin.HasValue && ex2.MaxPoint.Y < yMin.Value) continue;
                    if (yMax.HasValue && ex2.MinPoint.Y > yMax.Value) continue;

                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(ent.GetType().Name)
                      .Append(" closed=").Append(isClosed).Append(" verts=").Append(verts);
                    if (area != 0) sb.Append(" area=").Append(area.ToString("F1", CultureInfo.InvariantCulture));
                    sb.Append(" ").Append(_AnalyzeHelp.Pt(ex2.MinPoint)).Append("~").Append(_AnalyzeHelp.Pt(ex2.MaxPoint))
                      .Append(" L=").Append(ent.Layer).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("매칭 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListByLayerTool : ChatTool
    {
        public override string Name => "drawing.list_by_layer";
        public override string Description => "특정 레이어의 모든 엔티티 — 사용자가 도면에서 보는 그룹과 일치하는 경우 가장 정확.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="layer", Type="string", Required=true },
            new ToolParam{ Name="max",   Type="number", Description="최대 출력 (기본 200)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var layer = _ToolHelp.AsString(args, "layer"); if (string.IsNullOrEmpty(layer)) return ToolResult.Fail("layer 필요.");
            int max = 200; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!string.Equals(ent.Layer, layer, StringComparison.Ordinal)) continue;
                    Extents3d ex2; try { ex2 = ent.GeometricExtents; } catch { ex2 = new Extents3d(Point3d.Origin, Point3d.Origin); }
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(ent.GetType().Name)
                      .Append(" ").Append(_AnalyzeHelp.Pt(ex2.MinPoint)).Append("~").Append(_AnalyzeHelp.Pt(ex2.MaxPoint))
                      .Append(" ci=").Append(ent.ColorIndex).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("레이어에 엔티티 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListByTypeTool : ChatTool
    {
        public override string Name => "drawing.list_by_type";
        public override string Description => "특정 타입 (Line / Circle / Polyline / MText / DBText / Hatch / Dimension / RotatedDimension / DiametricDimension / BlockReference 등) 의 엔티티.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="type", Type="string", Required=true, Description="대소문자 무시 (예: Polyline, Circle, MText)" },
            new ToolParam{ Name="max",  Type="number" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var type = _ToolHelp.AsString(args, "type"); if (string.IsNullOrEmpty(type)) return ToolResult.Fail("type 필요.");
            int max = 200; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!string.Equals(ent.GetType().Name, type, StringComparison.OrdinalIgnoreCase)) continue;
                    Extents3d ex2; try { ex2 = ent.GeometricExtents; } catch { ex2 = new Extents3d(Point3d.Origin, Point3d.Origin); }
                    sb.Append("h=").Append(ent.Handle).Append(" ")
                      .Append(_AnalyzeHelp.Pt(ex2.MinPoint)).Append("~").Append(_AnalyzeHelp.Pt(ex2.MaxPoint))
                      .Append(" L=").Append(ent.Layer).Append(" ci=").Append(ent.ColorIndex).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("매칭 타입 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingListByColorTool : ChatTool
    {
        public override string Name => "drawing.list_by_color";
        public override string Description => "특정 ColorIndex 의 엔티티 — '빨간 거', '파란 거' 처럼 색으로 식별.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="color", Type="number", Required=true },
            new ToolParam{ Name="max",   Type="number" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var c = _ToolHelp.AsDouble(args, "color"); if (!c.HasValue) return ToolResult.Fail("color 필요.");
            int target = (int)c.Value;
            int max = 200; var m = _ToolHelp.AsDouble(args, "max"); if (m.HasValue && m.Value > 0) max = (int)m.Value;
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var sb = new StringBuilder();
            int count = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (count >= max) break;
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (ent.ColorIndex != target) continue;
                    Extents3d ex2; try { ex2 = ent.GeometricExtents; } catch { ex2 = new Extents3d(Point3d.Origin, Point3d.Origin); }
                    sb.Append("h=").Append(ent.Handle).Append(" ").Append(ent.GetType().Name)
                      .Append(" ").Append(_AnalyzeHelp.Pt(ex2.MinPoint)).Append("~").Append(_AnalyzeHelp.Pt(ex2.MaxPoint))
                      .Append(" L=").Append(ent.Layer).AppendLine();
                    count++;
                }
                tr.Commit();
            }
            if (count == 0) return ToolResult.Ok("매칭 색상 없음.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    public class DrawingEntityDetailTool : ChatTool
    {
        public override string Name => "drawing.entity_detail";
        public override string Description =>
            "한 엔티티 (handle) 의 정밀 정보 — Polyline 이면 꼭지점 좌표 / Circle 이면 중심·반지름 / Line 이면 시작·끝.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handle", Type="string", Required=true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var hex = _ToolHelp.AsString(args, "handle"); if (string.IsNullOrEmpty(hex)) return ToolResult.Fail("handle 필요.");
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            if (!_AnalyzeHelp.TryHandleToId(doc.Database, hex, out var id)) return ToolResult.Fail("유효하지 않은 핸들.");
            var sb = new StringBuilder();
            object data = null;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) { tr.Commit(); return ToolResult.Fail("엔티티 아님."); }
                sb.Append("type=").Append(ent.GetType().Name).AppendLine();
                sb.Append("layer=").Append(ent.Layer).AppendLine();
                sb.Append("color=").Append(ent.ColorIndex).AppendLine();
                if (ent is Line ln)
                {
                    sb.Append("start=").Append(_AnalyzeHelp.Pt(ln.StartPoint)).AppendLine();
                    sb.Append("end=").Append(_AnalyzeHelp.Pt(ln.EndPoint)).AppendLine();
                    sb.Append("length=").Append(ln.Length.ToString("F2", CultureInfo.InvariantCulture)).AppendLine();
                }
                else if (ent is Circle cir)
                {
                    sb.Append("center=").Append(_AnalyzeHelp.Pt(cir.Center)).AppendLine();
                    sb.Append("radius=").Append(cir.Radius.ToString("F2", CultureInfo.InvariantCulture)).AppendLine();
                }
                else if (ent is Arc arc)
                {
                    sb.Append("center=").Append(_AnalyzeHelp.Pt(arc.Center)).AppendLine();
                    sb.Append("radius=").Append(arc.Radius.ToString("F2", CultureInfo.InvariantCulture)).AppendLine();
                    sb.Append("startAngle=").Append(arc.StartAngle.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
                    sb.Append("endAngle=").Append(arc.EndAngle.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
                }
                else if (ent is Polyline pl)
                {
                    sb.Append("closed=").Append(pl.Closed).AppendLine();
                    sb.Append("vertices=").Append(pl.NumberOfVertices).AppendLine();
                    try { sb.Append("area=").Append(pl.Area.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(); } catch { }
                    try { sb.Append("length=").Append(pl.Length.ToString("F2", CultureInfo.InvariantCulture)).AppendLine(); } catch { }
                    int n = Math.Min(pl.NumberOfVertices, 30);
                    for (int i = 0; i < n; i++)
                        sb.Append("  v").Append(i).Append("=").Append(_AnalyzeHelp.Pt(pl.GetPoint3dAt(i))).AppendLine();
                    if (pl.NumberOfVertices > 30) sb.Append("  ...(").Append(pl.NumberOfVertices - 30).Append(" more)").AppendLine();
                }
                else if (ent is MText mt)
                {
                    sb.Append("location=").Append(_AnalyzeHelp.Pt(mt.Location)).AppendLine();
                    sb.Append("text=").Append(mt.Contents).AppendLine();
                }
                else if (ent is DBText dt)
                {
                    sb.Append("position=").Append(_AnalyzeHelp.Pt(dt.Position)).AppendLine();
                    sb.Append("text=").Append(dt.TextString).AppendLine();
                }
                else if (ent is BlockReference br)
                {
                    sb.Append("name=").Append(br.Name).AppendLine();
                    sb.Append("position=").Append(_AnalyzeHelp.Pt(br.Position)).AppendLine();
                    sb.Append("rotation=").Append(br.Rotation.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
                }
                Extents3d ex2; try { ex2 = ent.GeometricExtents; sb.Append("extents=").Append(_AnalyzeHelp.Pt(ex2.MinPoint)).Append("~").Append(_AnalyzeHelp.Pt(ex2.MaxPoint)); } catch { }
                data = GeometrySnapshot.Read(ent);
                tr.Commit();
            }
            return ToolResult.Ok(sb.ToString().TrimEnd(), data);
        }
    }

    public class DrawingHighlightTool : ChatTool
    {
        public override string Name => "drawing.highlight";
        public override string Description =>
            "지정 핸들들을 잠시 강조 — 도면의 강조 (Highlight) 효과 적용 후 자동으로 zoom_to. 사용자가 시각적으로 확인 가능. " +
            "주의: 강조 표시는 selection 효과로 일시적이며 사용자가 화면 클릭하면 풀린다.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handles", Type="array", Required=true },
            new ToolParam{ Name="zoom",    Type="boolean", Description="true=대상 영역으로 줌 (기본 true)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ids = _ModifyHelp.ResolveHandles(doc.Database, args, out var err); if (err != null) return ToolResult.Fail(err);
            bool zoom = true;
            if (args != null && args.TryGetValue("zoom", out var zv) && zv is bool zb) zoom = zb;

            // 강조 + extents 누적
            Extents3d? union = null;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    try { ent.Highlight(); } catch { }
                    Extents3d ex2;
                    try { ex2 = ent.GeometricExtents; } catch { continue; }
                    if (union == null) union = ex2;
                    else { var u = union.Value; u.AddExtents(ex2); union = u; }
                }
                tr.Commit();
            }
            if (zoom && union.HasValue)
            {
                try
                {
                    var u = union.Value;
                    // 약간 여유
                    var margin = 50.0;
                    var min = new Point3d(u.MinPoint.X - margin, u.MinPoint.Y - margin, 0);
                    var max = new Point3d(u.MaxPoint.X + margin, u.MaxPoint.Y + margin, 0);
                    using (var view = doc.Editor.GetCurrentView())
                    {
                        view.CenterPoint = new Point2d((min.X + max.X) / 2, (min.Y + max.Y) / 2);
                        view.Width  = Math.Max(1.0, max.X - min.X);
                        view.Height = Math.Max(1.0, max.Y - min.Y);
                        doc.Editor.SetCurrentView(view);
                    }
                }
                catch { }
            }
            return ToolResult.Ok("강조 적용: " + ids.Count + "개. 도면에서 확인 후 다음 도구를 호출하세요.");
        }
    }

    public class DrawingZoomToTool : ChatTool
    {
        public override string Name => "drawing.zoom_to";
        public override string Description => "지정 핸들들의 합 영역으로 AutoCAD 화면을 이동 / 확대. 강조는 따로 안 함.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handles", Type="array", Required=true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ids = _ModifyHelp.ResolveHandles(doc.Database, args, out var err); if (err != null) return ToolResult.Fail(err);
            Extents3d? union = null;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    Extents3d ex2; try { ex2 = ent.GeometricExtents; } catch { continue; }
                    if (union == null) union = ex2;
                    else { var u = union.Value; u.AddExtents(ex2); union = u; }
                }
                tr.Commit();
            }
            if (!union.HasValue) return ToolResult.Fail("유효한 영역 없음.");
            try
            {
                var u = union.Value; var margin = 50.0;
                var minX = u.MinPoint.X - margin; var maxX = u.MaxPoint.X + margin;
                var minY = u.MinPoint.Y - margin; var maxY = u.MaxPoint.Y + margin;
                using (var view = doc.Editor.GetCurrentView())
                {
                    view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
                    view.Width = Math.Max(1.0, maxX - minX);
                    view.Height = Math.Max(1.0, maxY - minY);
                    doc.Editor.SetCurrentView(view);
                }
                return ToolResult.Ok("zoom 완료.");
            }
            catch (Exception ex) { return ToolResult.Fail("zoom 실패: " + ex.Message); }
        }
    }

    public class DrawingMoveEntitiesTool : ChatTool
    {
        public override string Name => "drawing.move_entities";
        public override string Description => "지정한 핸들들을 dx,dy 만큼 평행 이동.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam{ Name="handles", Type="array",  Required=true },
            new ToolParam{ Name="dx",      Type="number", Required=true },
            new ToolParam{ Name="dy",      Type="number", Required=true },
        };

        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = _AnalyzeHelp.Doc(); if (doc == null) return ToolResult.Fail("도면 없음.");
            var ids = _ModifyHelp.ResolveHandles(doc.Database, args, out var err); if (err != null) return ToolResult.Fail(err);
            var dx = _ToolHelp.AsDouble(args, "dx"); var dy = _ToolHelp.AsDouble(args, "dy");
            if (!dx.HasValue || !dy.HasValue) return ToolResult.Fail("dx/dy 필요.");
            var mat = Matrix3d.Displacement(new Vector3d(dx.Value, dy.Value, 0));
            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.TransformBy(mat); n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok("이동: " + n + "개 (dx=" + dx.Value + ", dy=" + dy.Value + ")");
        }
    }
}


