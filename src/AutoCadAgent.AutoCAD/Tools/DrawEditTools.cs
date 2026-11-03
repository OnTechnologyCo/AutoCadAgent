using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // 즉시 처리 변환/편집 도구 모음.
    //
    // draw.* 누적 도구와 다르게, 이 도구들은 핸들 배열을 받아 즉시 도면을 수정한다.
    // 엔티티 핸들로 지정하는 변환(이동/회전/복사/스케일/미러/오프셋/삭제/분해)
    // + 조회(거리/면적/엔티티 정보) + 레이어 관리 + 유틸리티(zoom/regen/purge).

    internal static class EditToolsExt
    {
        public static List<ObjectId> ResolveHandles(IDictionary<string, object> args, string key, Database db, out string err)
        {
            err = null;
            var handles = DrawHelpers.GetHandleArray(args, key);
            if (handles.Count == 0) { err = key + " 누락 (핸들 hex 배열)"; return null; }
            var ids = DrawHelpers.HandlesToObjectIds(db, handles, out var inv);
            if (ids.Count == 0) { err = "유효한 핸들 없음 (모두 변환 실패)"; return null; }
            if (inv > 0) { err = inv + "개 무효·삭제·중복 핸들입니다. 일부만 수정하지 않도록 요청 전체를 거절했습니다."; return null; }
            return ids;
        }
    }

    // ───────── 변환/편집 ─────────

    public class DrawCopyTool : ChatTool
    {
        public override string Name => "draw.copy";
        public override string Description => "엔티티 복사 (handles 핸들 배열 → dx,dy 만큼 평행이동된 사본 생성).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true, Description = "[hex,...]" },
            new ToolParam { Name = "dx",      Type = "number", Required = true },
            new ToolParam { Name = "dy",      Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "dx", out var dx)) return ToolResult.Fail("dx 누락");
            if (!DrawHelpers.TryGetDouble(args, "dy", out var dy)) return ToolResult.Fail("dy 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var newHandles = new List<string>();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var id in ids)
                {
                    var src = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (src == null) continue;
                    var clone = src.Clone() as Entity;
                    if (clone == null) continue;
                    clone.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
                    ms.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    newHandles.Add(clone.Handle.Value.ToString("X"));
                }
                tr.Commit();
            }
            return ToolResult.Ok($"copy {newHandles.Count}개", new { handles = newHandles, count = newHandles.Count });
        }
    }

    public class DrawMoveTool : ChatTool
    {
        public override string Name => "draw.move";
        public override string Description => "엔티티 이동 (원본을 dx,dy 만큼 평행이동, 사본 생성 안함).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "dx",      Type = "number", Required = true },
            new ToolParam { Name = "dy",      Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "dx", out var dx)) return ToolResult.Fail("dx 누락");
            if (!DrawHelpers.TryGetDouble(args, "dy", out var dy)) return ToolResult.Fail("dy 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok($"move {n}개 ({dx:0.##},{dy:0.##})");
        }
    }

    public class DrawRotateTool : ChatTool
    {
        public override string Name => "draw.rotate";
        public override string Description => "엔티티 회전. 기준점(bx,by) 중심으로 angle(도) 회전.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "bx",      Type = "number", Required = true, Description = "기준점 X" },
            new ToolParam { Name = "by",      Type = "number", Required = true },
            new ToolParam { Name = "angle",   Type = "number", Required = true, Description = "회전각(도)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "bx", out var bx)) return ToolResult.Fail("bx 누락");
            if (!DrawHelpers.TryGetDouble(args, "by", out var by)) return ToolResult.Fail("by 누락");
            if (!DrawHelpers.TryGetDouble(args, "angle", out var ang)) return ToolResult.Fail("angle 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            int n = 0;
            var rot = Matrix3d.Rotation(DrawHelpers.Deg2Rad(ang), Vector3d.ZAxis, new Point3d(bx, by, 0));
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.TransformBy(rot);
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok($"rotate {n}개 ({bx:0.##},{by:0.##}) {ang:0.#}°");
        }
    }

    public class DrawScaleTool : ChatTool
    {
        public override string Name => "draw.scale";
        public override string Description => "엔티티 스케일. 기준점(bx,by) 중심으로 factor 배 확대/축소.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "bx",      Type = "number", Required = true },
            new ToolParam { Name = "by",      Type = "number", Required = true },
            new ToolParam { Name = "factor",  Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "bx", out var bx)) return ToolResult.Fail("bx 누락");
            if (!DrawHelpers.TryGetDouble(args, "by", out var by)) return ToolResult.Fail("by 누락");
            if (!DrawHelpers.TryGetDouble(args, "factor", out var f) || f <= 0) return ToolResult.Fail("factor 누락 또는 0 이하");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            int n = 0;
            var sc = Matrix3d.Scaling(f, new Point3d(bx, by, 0));
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.TransformBy(sc);
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok($"scale {n}개 ×{f:0.###}");
        }
    }

    public class DrawMirrorTool : ChatTool
    {
        public override string Name => "draw.mirror";
        public override string Description => "엔티티 미러링. 미러축 두 점 (x1,y1)~(x2,y2). erase_source=true 면 원본 삭제.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles",      Type = "array",   Required = true },
            new ToolParam { Name = "x1",           Type = "number",  Required = true },
            new ToolParam { Name = "y1",           Type = "number",  Required = true },
            new ToolParam { Name = "x2",           Type = "number",  Required = true },
            new ToolParam { Name = "y2",           Type = "number",  Required = true },
            new ToolParam { Name = "erase_source", Type = "boolean", Required = false, Description = "기본 false (사본 생성)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            var eraseSrc = DrawHelpers.GetBool(args, "erase_source", false);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var mirror = Matrix3d.Mirroring(new Line3d(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)));
            var newHandles = new List<string>();

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var id in ids)
                {
                    var src = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (src == null) continue;
                    if (eraseSrc)
                    {
                        src.UpgradeOpen();
                        src.TransformBy(mirror);
                        newHandles.Add(src.Handle.Value.ToString("X"));
                    }
                    else
                    {
                        var clone = src.Clone() as Entity;
                        if (clone == null) continue;
                        clone.TransformBy(mirror);
                        ms.AppendEntity(clone);
                        tr.AddNewlyCreatedDBObject(clone, true);
                        newHandles.Add(clone.Handle.Value.ToString("X"));
                    }
                }
                tr.Commit();
            }
            return ToolResult.Ok($"mirror {newHandles.Count}개", new { handles = newHandles, count = newHandles.Count });
        }
    }

    public class DrawOffsetTool : ChatTool
    {
        public override string Name => "draw.offset";
        public override string Description => "Curve 계열 엔티티 오프셋 (라인/원/호/폴리라인). distance 양수=한쪽, 음수=반대쪽.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles",  Type = "array",  Required = true },
            new ToolParam { Name = "distance", Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "distance", out var dist)) return ToolResult.Fail("distance 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var newHandles = new List<string>();
            int skipped = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var id in ids)
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c == null) { skipped++; continue; }
                    try
                    {
                        var coll = c.GetOffsetCurves(dist);
                        foreach (Entity ent in coll)
                        {
                            ms.AppendEntity(ent);
                            tr.AddNewlyCreatedDBObject(ent, true);
                            newHandles.Add(ent.Handle.Value.ToString("X"));
                        }
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            return ToolResult.Ok($"offset {newHandles.Count}개 생성 (skip {skipped}). 핸들: {string.Join(",", newHandles)}");
        }
    }

    public class DrawEraseTool : ChatTool
    {
        public override string Name => "draw.erase";
        public override string Description => "엔티티 삭제 (handles 핸들 배열).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.Erase();
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok($"erase {n}개");
        }
    }

    public class DrawExplodeTool : ChatTool
    {
        public override string Name => "draw.explode";
        public override string Description => "엔티티 분해 (블록/폴리라인 등을 기본 도형으로). 원본 삭제, 분해된 도형 새 핸들 반환.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var newHandles = new List<string>();
            int skipped = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var id in ids)
                {
                    var src = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (src == null) { skipped++; continue; }
                    try
                    {
                        var pieces = new DBObjectCollection();
                        src.Explode(pieces);
                        foreach (Entity p in pieces)
                        {
                            ms.AppendEntity(p);
                            tr.AddNewlyCreatedDBObject(p, true);
                            newHandles.Add(p.Handle.Value.ToString("X"));
                        }
                        src.Erase();
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            // 결과 분기 — LLM 이 거짓 보고하지 않도록 케이스별로 명확한 메시지.
            int input = ids.Count;
            if (newHandles.Count == 0)
                return ToolResult.Fail(
                    $"분해 실패 — {input}개 도형이 모두 분해 불가능 (원/직선/점/문자/이미 단순 도형 등은 분해 안 됨). 폴리라인/블록/해치/치수 등 복합 도형만 분해 가능.");
            if (skipped > 0)
                return ToolResult.Ok(
                    $"부분 분해 — {newHandles.Count}개 조각 생성, {skipped}개 도형은 분해 불가. 새 핸들: {string.Join(",", newHandles)}");
            return ToolResult.Ok(
                $"분해 완료 — {newHandles.Count}개 조각. 새 핸들: {string.Join(",", newHandles)}");
        }
    }

    // ───────── 블록 ─────────

    public class BlockInsertTool : ChatTool
    {
        public override string Name => "block.insert";
        public override string Description => "블록 삽입. 도면에 정의된 block_name 블록을 (x,y) 위치에 삽입.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "block_name", Type = "string", Required = true },
            new ToolParam { Name = "x",          Type = "number", Required = true },
            new ToolParam { Name = "y",          Type = "number", Required = true },
            new ToolParam { Name = "scale",      Type = "number", Required = false, Description = "기본 1" },
            new ToolParam { Name = "rotation",   Type = "number", Required = false, Description = "회전(도). 기본 0" },
            new ToolParam { Name = "layer",      Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var bn = DrawHelpers.GetString(args, "block_name");
            if (string.IsNullOrWhiteSpace(bn)) return ToolResult.Fail("block_name 누락");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            DrawHelpers.TryGetDouble(args, "scale", out var sc);
            if (sc <= 0) sc = 1;
            DrawHelpers.TryGetDouble(args, "rotation", out var rotDeg);
            var layer = DrawHelpers.GetString(args, "layer");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(bn)) { tr.Abort(); return ToolResult.Fail($"블록 '{bn}' 정의 없음"); }
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                using (var bref = new BlockReference(new Point3d(x, y, 0), bt[bn]))
                {
                    bref.ScaleFactors = new Scale3d(sc);
                    bref.Rotation = DrawHelpers.Deg2Rad(rotDeg);
                    if (!string.IsNullOrWhiteSpace(layer))
                    {
                        DrawHelpers.EnsureLayer(doc.Database, layer);
                        bref.Layer = layer;
                    }
                    btr.AppendEntity(bref);
                    tr.AddNewlyCreatedDBObject(bref, true);
                    newHandle = bref.Handle.Value.ToString("X");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"block.insert '{bn}' @ ({x:0.##},{y:0.##}) scale={sc} → {newHandle}");
        }
    }

    public class BlockCreateTool : ChatTool
    {
        public override string Name => "block.create";
        public override string Description => "블록 정의 생성. handles 핸들 배열의 엔티티들을 base_x,base_y 기준점으로 새 블록 정의에 묶음. 기존 동명 블록 있으면 실패.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "block_name", Type = "string", Required = true },
            new ToolParam { Name = "handles",    Type = "array",  Required = true },
            new ToolParam { Name = "base_x",     Type = "number", Required = true },
            new ToolParam { Name = "base_y",     Type = "number", Required = true },
            new ToolParam { Name = "erase_source", Type = "boolean", Required = false, Description = "원본 삭제 여부 (기본 false)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var bn = DrawHelpers.GetString(args, "block_name");
            if (string.IsNullOrWhiteSpace(bn)) return ToolResult.Fail("block_name 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_x", out var bx)) return ToolResult.Fail("base_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_y", out var by)) return ToolResult.Fail("base_y 누락");
            var eraseSrc = DrawHelpers.GetBool(args, "erase_source", false);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForWrite);
                if (bt.Has(bn)) { tr.Abort(); return ToolResult.Fail($"블록 '{bn}' 이미 존재"); }
                var newBtr = new BlockTableRecord { Name = bn, Origin = new Point3d(bx, by, 0) };
                var btrId = bt.Add(newBtr);
                tr.AddNewlyCreatedDBObject(newBtr, true);

                foreach (var id in ids)
                {
                    var src = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (src == null) continue;
                    var clone = src.Clone() as Entity;
                    if (clone == null) continue;
                    newBtr.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    if (eraseSrc)
                    {
                        src.UpgradeOpen();
                        src.Erase();
                    }
                }
                tr.Commit();
            }
            return ToolResult.Ok($"block.create '{bn}' (소스 {ids.Count}개)");
        }
    }

    public class BlockExplodeTool : ChatTool
    {
        public override string Name => "block.explode";
        public override string Description => "블록 참조를 분해 (BlockReference → 구성 엔티티들). draw.explode 와 동일한 동작이지만 블록 전용 라벨.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            // 동작은 draw.explode 와 동일.
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var newHandles = new List<string>();
            int skipped = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var id in ids)
                {
                    var bref = tr.GetObject(id, OpenMode.ForWrite) as BlockReference;
                    if (bref == null) { skipped++; continue; }
                    try
                    {
                        var pieces = new DBObjectCollection();
                        bref.Explode(pieces);
                        foreach (Entity p in pieces)
                        {
                            ms.AppendEntity(p);
                            tr.AddNewlyCreatedDBObject(p, true);
                            newHandles.Add(p.Handle.Value.ToString("X"));
                        }
                        bref.Erase();
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            int input = ids.Count;
            if (newHandles.Count == 0)
                return ToolResult.Fail(
                    $"블록 분해 실패 — {input}개 도형이 BlockReference 가 아님. block.explode 는 블록 참조에만 적용 가능 — 일반 도형은 draw.explode 사용.");
            if (skipped > 0)
                return ToolResult.Ok(
                    $"블록 부분 분해 — {newHandles.Count}개 조각 생성, {skipped}개 비-블록 입력 무시. 새 핸들: {string.Join(",", newHandles)}");
            return ToolResult.Ok(
                $"블록 분해 완료 — {newHandles.Count}개 조각. 새 핸들: {string.Join(",", newHandles)}");
        }
    }

    // ───────── 조회 ─────────

    public class QueryDistanceTool : ChatTool
    {
        public override string Name => "query.distance";
        public override string Description => "두 점 (x1,y1)~(x2,y2) 사이 거리 + 각도 계산.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1", Type = "number", Required = true },
            new ToolParam { Name = "y1", Type = "number", Required = true },
            new ToolParam { Name = "x2", Type = "number", Required = true },
            new ToolParam { Name = "y2", Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            double dx = x2 - x1, dy = y2 - y1;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double angDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            return ToolResult.Ok($"거리={dist:0.###}, 각도={angDeg:0.##}°, dx={dx:0.###}, dy={dy:0.###}");
        }
    }

    public class QueryAreaTool : ChatTool
    {
        public override string Name => "query.area";
        public override string Description => "엔티티(폴리라인/원/호/타원/영역) 면적 + 둘레 계산.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    double area = 0, perimeter = 0;
                    string typeName = ent.GetType().Name;
                    try
                    {
                        if (ent is Polyline pl) { area = pl.Area; perimeter = pl.Length; }
                        else if (ent is Circle c) { area = Math.PI * c.Radius * c.Radius; perimeter = 2 * Math.PI * c.Radius; }
                        else if (ent is Arc a) { area = 0.5 * a.Radius * a.Radius * (a.EndAngle - a.StartAngle); perimeter = a.Length; }
                        else if (ent is Ellipse el) { area = Math.PI * el.MajorRadius * el.MinorRadius; perimeter = 0; }
                        else if (ent is Region r) { area = r.Area; perimeter = 0; }
                    }
                    catch { /* 일부 엔티티에서 Area 접근 시 예외 가능 */ }
                    lines.Add($"{ent.Handle.Value:X}({typeName}): 면적={area:0.###}, 둘레={perimeter:0.###}");
                }
                tr.Commit();
            }
            return ToolResult.Ok(string.Join("\n", lines));
        }
    }

    public class QueryListTool : ChatTool
    {
        public override string Name => "query.list";
        public override string Description => "엔티티 상세 정보 (LIST 명령처럼) — 타입/레이어/색상/위치/크기.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"{ent.Handle.Value:X} {ent.GetType().Name} layer={ent.Layer} color={ent.ColorIndex}");
                    if (ent is Line ln) sb.Append($" ({ln.StartPoint.X:0.##},{ln.StartPoint.Y:0.##})~({ln.EndPoint.X:0.##},{ln.EndPoint.Y:0.##}) len={ln.Length:0.###}");
                    else if (ent is Circle c) sb.Append($" 중심({c.Center.X:0.##},{c.Center.Y:0.##}) r={c.Radius:0.###}");
                    else if (ent is Arc a) sb.Append($" 중심({a.Center.X:0.##},{a.Center.Y:0.##}) r={a.Radius:0.###} {a.StartAngle * 180 / Math.PI:0.#}°→{a.EndAngle * 180 / Math.PI:0.#}°");
                    else if (ent is Polyline pl) sb.Append($" 정점{pl.NumberOfVertices}개 closed={pl.Closed} len={pl.Length:0.###}");
                    else if (ent is DBText t) sb.Append($" \"{t.TextString}\" h={t.Height:0.##}");
                    else if (ent is MText mt) sb.Append($" \"{mt.Contents}\" h={mt.TextHeight:0.##}");
                    else if (ent is BlockReference br) sb.Append($" '{br.Name}' @({br.Position.X:0.##},{br.Position.Y:0.##}) scale={br.ScaleFactors.X}");
                    lines.Add(sb.ToString());
                }
                tr.Commit();
            }
            return ToolResult.Ok(string.Join("\n", lines));
        }
    }

    public class QueryIdPointTool : ChatTool
    {
        public override string Name => "query.id_point";
        public override string Description => "핸들의 대표 좌표(시작점 또는 중심) 반환. 디버깅용.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) { tr.Commit(); return ToolResult.Fail("엔티티 아님"); }
                Point3d p;
                if (ent is Line ln) p = ln.StartPoint;
                else if (ent is Circle c) p = c.Center;
                else if (ent is Arc a) p = a.Center;
                else if (ent is Polyline pl) p = pl.GetPoint3dAt(0);
                else if (ent is DBText t) p = t.Position;
                else if (ent is MText mt) p = mt.Location;
                else if (ent is BlockReference br) p = br.Position;
                else p = ent.GeometricExtents.MinPoint;
                tr.Commit();
                return ToolResult.Ok($"{hex}: ({p.X:0.###},{p.Y:0.###},{p.Z:0.###})");
            }
        }
    }

    // ───────── 레이어 관리 ─────────

    public class LayerSetCurrentTool : ChatTool
    {
        public override string Name => "layer.set_current";
        public override string Description => "현재 레이어 설정. 새 도형이 이 레이어에 그려진다.";
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
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name)) { tr.Abort(); return ToolResult.Fail($"레이어 '{name}' 없음"); }
                doc.Database.Clayer = lt[name];
                tr.Commit();
            }
            return ToolResult.Ok($"현재 레이어 '{name}'");
        }
    }

    public class LayerFreezeTool : ChatTool
    {
        public override string Name => "layer.freeze";
        public override string Description => "레이어 동결/해제. frozen=true 동결 / false 해제.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",   Type = "string",  Required = true },
            new ToolParam { Name = "frozen", Type = "boolean", Required = false, Description = "기본 true" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var frozen = DrawHelpers.GetBool(args, "frozen", true);
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name)) { tr.Abort(); return ToolResult.Fail($"레이어 '{name}' 없음"); }
                // AutoCAD 제약 — 현재 레이어는 동결 불가 (eInvalidLayer 회피)
                if (frozen && lt[name] == doc.Database.Clayer)
                {
                    tr.Abort();
                    return ToolResult.Fail($"'{name}' 은 현재 레이어이므로 동결할 수 없습니다. 먼저 다른 레이어를 layer.set_current 로 설정한 뒤 다시 시도하세요.");
                }
                var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                rec.IsFrozen = frozen;
                tr.Commit();
            }
            return ToolResult.Ok($"layer '{name}' frozen={frozen}");
        }
    }

    public class LayerLockTool : ChatTool
    {
        public override string Name => "layer.lock";
        public override string Description => "레이어 잠금/해제. locked=true 잠금 / false 해제.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",   Type = "string",  Required = true },
            new ToolParam { Name = "locked", Type = "boolean", Required = false, Description = "기본 true" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var locked = DrawHelpers.GetBool(args, "locked", true);
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name)) { tr.Abort(); return ToolResult.Fail($"레이어 '{name}' 없음"); }
                var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                rec.IsLocked = locked;
                tr.Commit();
            }
            return ToolResult.Ok($"layer '{name}' locked={locked}");
        }
    }

    public class LayerDeleteTool : ChatTool
    {
        public override string Name => "layer.delete";
        public override string Description => "레이어 삭제 (참조하는 엔티티 없을 때만 가능). 0 / Defpoints 등은 삭제 불가.";
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
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name)) { tr.Abort(); return ToolResult.Fail($"레이어 '{name}' 없음"); }
                try
                {
                    var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                    rec.Erase();
                    tr.Commit();
                    return ToolResult.Ok($"layer '{name}' 삭제");
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    return ToolResult.Fail($"삭제 불가: {ex.Message}");
                }
            }
        }
    }

    // ───────── 유틸리티 ─────────

    public class UtilPurgeTool : ChatTool
    {
        public override string Name => "util.purge";
        public override string Description => "사용하지 않는 레이어/블록/스타일 등 일괄 정리.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            int total = 0;
            using (doc.LockDocument())
            {
                // 반복적으로 PURGE — 한 번 실행으로 안 비워질 수 있음
                for (int pass = 0; pass < 3; pass++)
                {
                    int found = 0;
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        // SymbolTable IDs 모음 (Layer/Linetype/TextStyle/Block/DimStyle/RegApp)
                        var ids = new ObjectIdCollection();
                        var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId id in lt) ids.Add(id);
                        var lts = (LinetypeTable)tr.GetObject(doc.Database.LinetypeTableId, OpenMode.ForRead);
                        foreach (ObjectId id in lts) ids.Add(id);
                        var ts = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                        foreach (ObjectId id in ts) ids.Add(id);
                        var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                        foreach (ObjectId id in bt) ids.Add(id);
                        var dst = (DimStyleTable)tr.GetObject(doc.Database.DimStyleTableId, OpenMode.ForRead);
                        foreach (ObjectId id in dst) ids.Add(id);

                        doc.Database.Purge(ids);
                        foreach (ObjectId id in ids)
                        {
                            try
                            {
                                var sym = tr.GetObject(id, OpenMode.ForWrite) as SymbolTableRecord;
                                if (sym == null) continue;
                                sym.Erase();
                                found++;
                            }
                            catch { /* 일부는 erase 실패 */ }
                        }
                        tr.Commit();
                    }
                    total += found;
                    if (found == 0) break;
                }
            }
            return ToolResult.Ok($"purge: {total}개 제거");
        }
    }

    public class UtilRegenTool : ChatTool
    {
        public override string Name => "util.regen";
        public override string Description => "도면 재생성 (REGEN). 화면 갱신.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            using (doc.LockDocument())
            {
                doc.Editor.Regen();
            }
            return ToolResult.Ok("regen 완료");
        }
    }

    public class UtilZoomTool : ChatTool
    {
        public override string Name => "util.zoom";
        public override string Description => "줌. mode=extents (모두 보기), window (영역 지정 — x1,y1,x2,y2 필수), center (cx,cy + height 필수).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "mode",   Type = "string", Required = true, Description = "extents | window | center" },
            new ToolParam { Name = "x1",     Type = "number", Required = false },
            new ToolParam { Name = "y1",     Type = "number", Required = false },
            new ToolParam { Name = "x2",     Type = "number", Required = false },
            new ToolParam { Name = "y2",     Type = "number", Required = false },
            new ToolParam { Name = "cx",     Type = "number", Required = false },
            new ToolParam { Name = "cy",     Type = "number", Required = false },
            new ToolParam { Name = "height", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var mode = (DrawHelpers.GetString(args, "mode") ?? "extents").ToLowerInvariant();
            using (doc.LockDocument())
            {
                var view = doc.Editor.GetCurrentView();
                if (mode == "extents")
                {
                    doc.Database.UpdateExt(true);
                    var lo = doc.Database.Extmin;
                    var hi = doc.Database.Extmax;
                    view.CenterPoint = new Point2d((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2);
                    view.Width = Math.Max(1, hi.X - lo.X) * 1.05;
                    view.Height = Math.Max(1, hi.Y - lo.Y) * 1.05;
                }
                else if (mode == "window")
                {
                    if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
                    if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
                    if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
                    if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
                    view.CenterPoint = new Point2d((x1 + x2) / 2, (y1 + y2) / 2);
                    view.Width = Math.Max(1, Math.Abs(x2 - x1));
                    view.Height = Math.Max(1, Math.Abs(y2 - y1));
                }
                else if (mode == "center")
                {
                    if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
                    if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
                    if (!DrawHelpers.TryGetDouble(args, "height", out var h) || h <= 0) return ToolResult.Fail("height 누락");
                    view.CenterPoint = new Point2d(cx, cy);
                    view.Height = h;
                    view.Width = h * (view.Width / Math.Max(1, view.Height));
                }
                else
                {
                    return ToolResult.Fail("mode 는 extents | window | center 중 하나");
                }
                doc.Editor.SetCurrentView(view);
            }
            return ToolResult.Ok($"zoom {mode}");
        }
    }
}


