using System;
using System.Collections.Generic;
using System.IO;

using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // Phase 4 도구 모음.
    //
    // 그리기/엔티티 보강 (solid_2d/face_2d/image/hatch_pattern/pline 정점 편집),
    // 속성 (linetype.load/create, match.props, lineweight/transparency),
    // 레이아웃/뷰포트 (layout/viewport/named view),
    // 선택집합 (window/crossing/fence),
    // 파일 (wblock.export).

    // ───────── 그리기/엔티티 ─────────

    public class DrawSolid2DTool : ChatTool
    {
        public override string Name => "draw.solid_2d";
        public override string Description => "2D 솔리드(칠하기 면). 3 또는 4개의 꼭짓점. 4점일 때 순서: 좌상→우상→좌하→우하 (지그재그).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "points", Type = "array",  Required = true, Description = "[[x,y],[x,y],[x,y]] 또는 [[x,y],[x,y],[x,y],[x,y]]" },
            new ToolParam { Name = "layer",  Type = "string", Required = false },
            new ToolParam { Name = "color",  Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetPointArray(args, "points", out var pts) || pts.Count < 3 || pts.Count > 4)
                return ToolResult.Fail("points 는 3 또는 4개 점");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var sol = new Solid();
                for (short i = 0; i < pts.Count; i++)
                    sol.SetPointAt(i, new Point3d(pts[i].X, pts[i].Y, 0));
                if (pts.Count == 3)
                    sol.SetPointAt(3, new Point3d(pts[2].X, pts[2].Y, 0));  // 4번째 = 3번째 (삼각형)
                DrawHelpers.ApplyLayerAndColor(sol, doc.Database, DrawHelpers.GetString(args, "layer"),
                    DrawHelpers.TryGetInt(args, "color", out var c) ? (int?)c : null);
                ms.AppendEntity(sol);
                tr.AddNewlyCreatedDBObject(sol, true);
                newHandle = sol.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"solid_2d {pts.Count}점 → {newHandle}");
        }
    }

    public class DrawFace2DTool : ChatTool
    {
        public override string Name => "draw.face_2d";
        public override string Description => "2D Face 엔티티 (구형 3DFace 의 평면 변형). 3 또는 4 꼭짓점.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "points", Type = "array",  Required = true },
            new ToolParam { Name = "layer",  Type = "string", Required = false },
            new ToolParam { Name = "color",  Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetPointArray(args, "points", out var pts) || pts.Count < 3 || pts.Count > 4)
                return ToolResult.Fail("points 는 3 또는 4개 점");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var p0 = new Point3d(pts[0].X, pts[0].Y, 0);
                var p1 = new Point3d(pts[1].X, pts[1].Y, 0);
                var p2 = new Point3d(pts[2].X, pts[2].Y, 0);
                var p3 = pts.Count == 4 ? new Point3d(pts[3].X, pts[3].Y, 0) : p2;
                var face = new Face(p0, p1, p2, p3, true, true, true, true);
                DrawHelpers.ApplyLayerAndColor(face, doc.Database, DrawHelpers.GetString(args, "layer"),
                    DrawHelpers.TryGetInt(args, "color", out var c) ? (int?)c : null);
                ms.AppendEntity(face);
                tr.AddNewlyCreatedDBObject(face, true);
                newHandle = face.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"face_2d {pts.Count}점 → {newHandle}");
        }
    }

    public class ImageAttachTool : ChatTool
    {
        public override string Name => "image.attach";
        public override string Description => "비트맵 이미지(PNG/JPG/BMP/TIFF)를 도면에 삽입. 좌하단 위치(x,y) + 너비(width). 높이는 비율 유지.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "path",     Type = "string", Required = true },
            new ToolParam { Name = "x",        Type = "number", Required = true },
            new ToolParam { Name = "y",        Type = "number", Required = true },
            new ToolParam { Name = "width",    Type = "number", Required = true, Description = "이미지 너비 (도면 단위)" },
            new ToolParam { Name = "rotation", Type = "number", Required = false },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var path = DrawHelpers.GetString(args, "path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return ToolResult.Fail($"파일 없음: {path}");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            if (!DrawHelpers.TryGetDouble(args, "width", out var w) || w <= 0) return ToolResult.Fail("width 누락");
            DrawHelpers.TryGetDouble(args, "rotation", out var rotDeg);
            var layer = DrawHelpers.GetString(args, "layer");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId defId;
                var imgDictId = RasterImageDef.GetImageDictionary(doc.Database);
                if (imgDictId.IsNull) imgDictId = RasterImageDef.CreateImageDictionary(doc.Database);
                var imgDict = (DBDictionary)tr.GetObject(imgDictId, OpenMode.ForWrite);
                var name = Path.GetFileNameWithoutExtension(path);
                if (imgDict.Contains(name))
                {
                    defId = imgDict.GetAt(name);
                }
                else
                {
                    var def = new RasterImageDef { SourceFileName = path };
                    def.Load();
                    defId = imgDict.SetAt(name, def);
                    tr.AddNewlyCreatedDBObject(def, true);
                }

                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var img = new RasterImage();
                img.ImageDefId = defId;
                // 정렬 — 단순화: x축 = (width,0), y축 비율 유지
                var def2 = (RasterImageDef)tr.GetObject(defId, OpenMode.ForRead);
                double ratio = def2.ResolutionMMPerPixel.Y / Math.Max(1e-9, def2.ResolutionMMPerPixel.X);
                double imgPxW = def2.Size.X;
                double imgPxH = def2.Size.Y;
                double h = w * (imgPxH / Math.Max(1, imgPxW));

                var rotRad = DrawHelpers.Deg2Rad(rotDeg);
                var u = new Vector3d(w * Math.Cos(rotRad), w * Math.Sin(rotRad), 0);
                var v = new Vector3d(-h * Math.Sin(rotRad), h * Math.Cos(rotRad), 0);
                img.Orientation = new CoordinateSystem3d(new Point3d(x, y, 0), u, v);

                if (!string.IsNullOrWhiteSpace(layer))
                {
                    DrawHelpers.EnsureLayer(doc.Database, layer);
                    img.Layer = layer;
                }

                ms.AppendEntity(img);
                tr.AddNewlyCreatedDBObject(img, true);
                RasterImage.EnableReactors(true);
                img.AssociateRasterDef(def2);

                newHandle = img.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"image.attach '{Path.GetFileName(path)}' @ ({x:0.##},{y:0.##}) → {newHandle}");
        }
    }

    public class DrawHatchPatternTool : ChatTool
    {
        public override string Name => "draw.hatch_pattern";
        public override string Description => "패턴 해치 — handles 핸들 배열의 닫힌 Curve 들을 경계로, pattern_name (ANSI31, ANSI32, NET, BRICK, EARTH 등) 으로 채움. 즉시 처리.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles",      Type = "array",  Required = true, Description = "닫힌 Curve hex 배열" },
            new ToolParam { Name = "pattern_name", Type = "string", Required = false, Description = "기본 ANSI31" },
            new ToolParam { Name = "scale",        Type = "number", Required = false, Description = "기본 1" },
            new ToolParam { Name = "angle",        Type = "number", Required = false, Description = "각도(도) 기본 0" },
            new ToolParam { Name = "layer",        Type = "string", Required = false },
            new ToolParam { Name = "color",        Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);
            var pat = DrawHelpers.GetString(args, "pattern_name", "ANSI31");
            DrawHelpers.TryGetDouble(args, "scale", out var sc); if (sc <= 0) sc = 1;
            DrawHelpers.TryGetDouble(args, "angle", out var ang);

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var hat = new Hatch();
                ms.AppendEntity(hat);
                tr.AddNewlyCreatedDBObject(hat, true);
                hat.SetHatchPattern(HatchPatternType.PreDefined, pat);
                hat.PatternScale = sc;
                hat.PatternAngle = DrawHelpers.Deg2Rad(ang);
                DrawHelpers.ApplyLayerAndColor(hat, doc.Database, DrawHelpers.GetString(args, "layer"),
                    DrawHelpers.TryGetInt(args, "color", out var ci) ? (int?)ci : null);
                hat.Associative = false;
                var loop = new ObjectIdCollection();
                foreach (var id in ids) loop.Add(id);
                try { hat.AppendLoop(HatchLoopTypes.Default, loop); }
                catch (Exception ex) { tr.Abort(); return ToolResult.Fail($"AppendLoop 실패: {ex.Message}"); }
                hat.EvaluateHatch(true);
                newHandle = hat.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"hatch_pattern '{pat}' → {newHandle}");
        }
    }

    public class PlineAddVertexTool : ChatTool
    {
        public override string Name => "pline.add_vertex";
        public override string Description => "Polyline 에 정점 추가. handle + index(삽입 위치) + (x,y) + bulge(곡률, 기본 0).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
            new ToolParam { Name = "index",  Type = "number", Required = true, Description = "삽입할 인덱스 (0=시작)" },
            new ToolParam { Name = "x",      Type = "number", Required = true },
            new ToolParam { Name = "y",      Type = "number", Required = true },
            new ToolParam { Name = "bulge",  Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            if (!DrawHelpers.TryGetInt(args, "index", out var idx) || idx < 0) return ToolResult.Fail("index 누락");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            DrawHelpers.TryGetDouble(args, "bulge", out var bulge);
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var pl = tr.GetObject(id, OpenMode.ForWrite) as Polyline;
                if (pl == null) { tr.Abort(); return ToolResult.Fail("Polyline 아님"); }
                if (idx > pl.NumberOfVertices) idx = pl.NumberOfVertices;
                pl.AddVertexAt(idx, new Point2d(x, y), bulge, 0, 0);
                tr.Commit();
            }
            return ToolResult.Ok($"pline.add_vertex idx={idx} ({x:0.##},{y:0.##})");
        }
    }

    public class PlineRemoveVertexTool : ChatTool
    {
        public override string Name => "pline.remove_vertex";
        public override string Description => "Polyline 의 정점 삭제. handle + index.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
            new ToolParam { Name = "index",  Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            if (!DrawHelpers.TryGetInt(args, "index", out var idx) || idx < 0) return ToolResult.Fail("index 누락");
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var pl = tr.GetObject(id, OpenMode.ForWrite) as Polyline;
                if (pl == null) { tr.Abort(); return ToolResult.Fail("Polyline 아님"); }
                if (idx >= pl.NumberOfVertices) { tr.Abort(); return ToolResult.Fail("index 범위 밖"); }
                if (pl.NumberOfVertices <= 2) { tr.Abort(); return ToolResult.Fail("정점이 2개 이하 — 삭제 불가"); }
                pl.RemoveVertexAt(idx);
                tr.Commit();
            }
            return ToolResult.Ok($"pline.remove_vertex idx={idx}");
        }
    }

    // ───────── 속성 / 선종류 ─────────

    public class LinetypeLoadTool : ChatTool
    {
        public override string Name => "linetype.load";
        public override string Description => ".lin 파일에서 선종류 로드. file_path 안 주면 표준 acad.lin 에서 로드.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",      Type = "string", Required = true, Description = "선종류 이름 (예: DASHED, CENTER, HIDDEN)" },
            new ToolParam { Name = "file_path", Type = "string", Required = false, Description = "기본 acad.lin" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var fp = DrawHelpers.GetString(args, "file_path", "acad.lin");
            using (doc.LockDocument())
            {
                try
                {
                    doc.Database.LoadLineTypeFile(name, fp);
                }
                catch (Exception ex) { return ToolResult.Fail($"linetype.load 실패: {ex.Message}"); }
            }
            return ToolResult.Ok($"linetype '{name}' 로드 완료 ({fp})");
        }
    }

    public class LinetypeCreateTool : ChatTool
    {
        public override string Name => "linetype.create";
        public override string Description => "커스텀 단순 선종류 생성. dash_pattern 은 [길이, ...] — 양수=실선구간, 음수=공백구간, 0=점.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",         Type = "string", Required = true },
            new ToolParam { Name = "description",  Type = "string", Required = false },
            new ToolParam { Name = "dash_pattern", Type = "array",  Required = true, Description = "[길이,...] — 양수=실선/음수=공백/0=점" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var desc = DrawHelpers.GetString(args, "description", name);

            if (!args.ContainsKey("dash_pattern") || !(args["dash_pattern"] is object[] arr) || arr.Length == 0)
                return ToolResult.Fail("dash_pattern 누락");
            var dashes = new List<double>();
            foreach (var o in arr)
            {
                if (o == null) continue;
                if (!double.TryParse(o.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return ToolResult.Fail("dash_pattern 숫자 아님");
                dashes.Add(d);
            }

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LinetypeTable)tr.GetObject(doc.Database.LinetypeTableId, OpenMode.ForWrite);
                if (lt.Has(name)) { tr.Abort(); return ToolResult.Fail($"linetype '{name}' 이미 존재"); }
                var rec = new LinetypeTableRecord
                {
                    Name = name,
                    AsciiDescription = desc,
                    NumDashes = dashes.Count,
                };
                double total = 0;
                for (int i = 0; i < dashes.Count; i++) { rec.SetDashLengthAt(i, dashes[i]); total += Math.Abs(dashes[i]); }
                rec.PatternLength = total;
                lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                tr.Commit();
            }
            return ToolResult.Ok($"linetype.create '{name}' (dashes={dashes.Count})");
        }
    }

    public class MatchPropsTool : ChatTool
    {
        public override string Name => "match.props";
        public override string Description => "엔티티 속성 복사 (source 의 layer/color/linetype/lineweight 를 targets 들에 적용).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "source",  Type = "string", Required = true, Description = "원본 핸들" },
            new ToolParam { Name = "targets", Type = "array",  Required = true, Description = "대상 핸들 배열" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var srcHex = DrawHelpers.GetString(args, "source");
            if (string.IsNullOrWhiteSpace(srcHex)) return ToolResult.Fail("source 누락");
            var srcId = DrawHelpers.HandleToObjectId(doc.Database, srcHex);
            if (srcId.IsNull || !srcId.IsValid) return ToolResult.Fail("source 유효하지 않음");
            var ids = EditToolsExt.ResolveHandles(args, "targets", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            int n = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var src = tr.GetObject(srcId, OpenMode.ForRead) as Entity;
                if (src == null) { tr.Abort(); return ToolResult.Fail("source 엔티티 아님"); }
                foreach (var id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;
                    ent.Layer = src.Layer;
                    ent.Color = src.Color;
                    ent.Linetype = src.Linetype;
                    ent.LineWeight = src.LineWeight;
                    ent.LinetypeScale = src.LinetypeScale;
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok($"match.props {n}개 적용");
        }
    }

    public class EntitySetLineweightTool : ChatTool
    {
        public override string Name => "entity.set_lineweight";
        public override string Description => "엔티티 선굵기 설정. lineweight 값: -1=ByLayer, -2=ByBlock, -3=Default, 0~211=mm×100 (예: 25=0.25mm).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles",    Type = "array",  Required = true },
            new ToolParam { Name = "lineweight", Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetInt(args, "lineweight", out var lw)) return ToolResult.Fail("lineweight 누락");
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
                    try { ent.LineWeight = (LineWeight)lw; n++; } catch { /* invalid value */ }
                }
                tr.Commit();
            }
            return ToolResult.Ok($"entity.set_lineweight {n}개 lw={lw}");
        }
    }

    public class EntitySetTransparencyTool : ChatTool
    {
        public override string Name => "entity.set_transparency";
        public override string Description => "엔티티 투명도 설정. alpha: 0=완전투명, 255=완전 불투명. AutoCAD 표시값: alpha=255-(투명도%×2.55).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "alpha",   Type = "number", Required = true, Description = "0~255 (255=불투명)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetInt(args, "alpha", out var a)) return ToolResult.Fail("alpha 누락");
            a = Math.Max(0, Math.Min(255, a));
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
                    ent.Transparency = new Transparency((byte)a);
                    n++;
                }
                tr.Commit();
            }
            return ToolResult.Ok($"entity.set_transparency {n}개 alpha={a}");
        }
    }

    // ───────── 레이아웃 / 뷰포트 / 명명 뷰 ─────────

    public class LayoutCreateTool : ChatTool
    {
        public override string Name => "layout.create";
        public override string Description => "새 레이아웃(Paper Space) 생성.";
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
            {
                var lm = LayoutManager.Current;
                if (!lm.GetLayoutId(name).IsNull) return ToolResult.Fail($"레이아웃 '{name}' 이미 존재");
                var id = lm.CreateLayout(name);
                if (id.IsNull) return ToolResult.Fail("CreateLayout 실패");
            }
            return ToolResult.Ok($"layout.create '{name}'");
        }
    }

    public class LayoutDeleteTool : ChatTool
    {
        public override string Name => "layout.delete";
        public override string Description => "레이아웃 삭제. Model 은 삭제 불가.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name", Type = "string", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            if (string.Equals(name, "Model", StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail("Model 삭제 불가");
            using (doc.LockDocument())
            {
                var lm = LayoutManager.Current;
                if (lm.GetLayoutId(name).IsNull) return ToolResult.Fail($"레이아웃 '{name}' 없음");
                lm.DeleteLayout(name);
            }
            return ToolResult.Ok($"layout.delete '{name}'");
        }
    }

    public class LayoutListTool : ChatTool
    {
        public override string Name => "layout.list";
        public override string Description => "도면의 모든 레이아웃 목록.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lines = new List<string>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dd = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry kv in dd)
                {
                    var lay = tr.GetObject(kv.Value, OpenMode.ForRead) as Layout;
                    if (lay == null) continue;
                    lines.Add($"{lay.LayoutName} (tab={lay.TabOrder})");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"레이아웃 {lines.Count}개:\n" + string.Join("\n", lines));
        }
    }

    public class ViewportCreateTool : ChatTool
    {
        public override string Name => "viewport.create";
        public override string Description => "지정한 레이아웃에 뷰포트 생성. layout_name, center(x,y), width, height. 모델 영역 zoom_extents 자동.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "layout_name", Type = "string", Required = true },
            new ToolParam { Name = "x",           Type = "number", Required = true, Description = "뷰포트 중심 X (Paper Space)" },
            new ToolParam { Name = "y",           Type = "number", Required = true },
            new ToolParam { Name = "width",       Type = "number", Required = true },
            new ToolParam { Name = "height",      Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var lname = DrawHelpers.GetString(args, "layout_name");
            if (string.IsNullOrWhiteSpace(lname)) return ToolResult.Fail("layout_name 누락");
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            if (!DrawHelpers.TryGetDouble(args, "width", out var w) || w <= 0) return ToolResult.Fail("width 누락");
            if (!DrawHelpers.TryGetDouble(args, "height", out var h) || h <= 0) return ToolResult.Fail("height 누락");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dd = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
                if (!dd.Contains(lname)) { tr.Abort(); return ToolResult.Fail($"레이아웃 '{lname}' 없음"); }
                var layId = dd.GetAt(lname);
                var lay = (Layout)tr.GetObject(layId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);
                var vp = new Viewport
                {
                    CenterPoint = new Point3d(x, y, 0),
                    Width = w,
                    Height = h,
                };
                btr.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.On = true;
                newHandle = vp.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"viewport.create on '{lname}' → {newHandle}");
        }
    }

    public class ViewportSetScaleTool : ChatTool
    {
        public override string Name => "viewport.set_scale";
        public override string Description => "뷰포트 축척 설정. handle + scale (예: 0.01 = 1:100).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handle", Type = "string", Required = true },
            new ToolParam { Name = "scale",  Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var hex = DrawHelpers.GetString(args, "handle");
            if (string.IsNullOrWhiteSpace(hex)) return ToolResult.Fail("handle 누락");
            if (!DrawHelpers.TryGetDouble(args, "scale", out var sc) || sc <= 0) return ToolResult.Fail("scale 누락");
            var id = DrawHelpers.HandleToObjectId(doc.Database, hex);
            if (id.IsNull || !id.IsValid) return ToolResult.Fail("유효하지 않은 핸들");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var vp = tr.GetObject(id, OpenMode.ForWrite) as Viewport;
                if (vp == null) { tr.Abort(); return ToolResult.Fail("Viewport 아님"); }
                vp.CustomScale = sc;
                tr.Commit();
            }
            return ToolResult.Ok($"viewport.set_scale {sc:0.######}");
        }
    }

    public class ViewSaveTool : ChatTool
    {
        public override string Name => "view.save";
        public override string Description => "현재 뷰를 명명된 뷰로 저장.";
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
                var vt = (ViewTable)tr.GetObject(doc.Database.ViewTableId, OpenMode.ForWrite);
                var cur = doc.Editor.GetCurrentView();
                ViewTableRecord rec;
                if (vt.Has(name))
                {
                    rec = (ViewTableRecord)tr.GetObject(vt[name], OpenMode.ForWrite);
                }
                else
                {
                    rec = new ViewTableRecord { Name = name };
                    vt.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                rec.CenterPoint = cur.CenterPoint;
                rec.Width = cur.Width;
                rec.Height = cur.Height;
                rec.ViewDirection = cur.ViewDirection;
                tr.Commit();
            }
            return ToolResult.Ok($"view.save '{name}'");
        }
    }

    public class ViewRestoreTool : ChatTool
    {
        public override string Name => "view.restore";
        public override string Description => "명명된 뷰 복원 (현재 뷰포트에 적용).";
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
                var vt = (ViewTable)tr.GetObject(doc.Database.ViewTableId, OpenMode.ForRead);
                if (!vt.Has(name)) { tr.Abort(); return ToolResult.Fail($"view '{name}' 없음"); }
                var rec = (ViewTableRecord)tr.GetObject(vt[name], OpenMode.ForRead);
                var cur = doc.Editor.GetCurrentView();
                cur.CenterPoint = rec.CenterPoint;
                cur.Width = rec.Width;
                cur.Height = rec.Height;
                cur.ViewDirection = rec.ViewDirection;
                doc.Editor.SetCurrentView(cur);
                tr.Commit();
            }
            return ToolResult.Ok($"view.restore '{name}'");
        }
    }

    // ───────── 선택 집합 ─────────

    internal static class SelectHelpers
    {
        public static string IdsToHandles(Database db, ObjectId[] ids)
        {
            var hexes = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in ids)
                {
                    if (id.IsNull || !id.IsValid) continue;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as DBObject;
                    if (obj == null) continue;
                    hexes.Add(obj.Handle.Value.ToString("X"));
                }
                tr.Commit();
            }
            return string.Join(",", hexes);
        }
    }

    public class SelectWindowTool : ChatTool
    {
        public override string Name => "select.window";
        public override string Description => "사각 영역 (x1,y1)~(x2,y2) 안에 완전히 포함된 엔티티들의 핸들 반환.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1", Type = "number", Required = true },
            new ToolParam { Name = "y1", Type = "number", Required = true },
            new ToolParam { Name = "x2", Type = "number", Required = true },
            new ToolParam { Name = "y2", Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");

            var res = doc.Editor.SelectWindow(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            if (res.Status != PromptStatus.OK || res.Value == null || res.Value.Count == 0)
                return ToolResult.Ok("선택 결과 없음");
            var ids = res.Value.GetObjectIds();
            return ToolResult.Ok($"select.window {ids.Length}개: {SelectHelpers.IdsToHandles(doc.Database, ids)}");
        }
    }

    public class SelectCrossingTool : ChatTool
    {
        public override string Name => "select.crossing";
        public override string Description => "사각 영역 (x1,y1)~(x2,y2) 와 교차하는 엔티티들의 핸들 반환 (포함 + 걸침).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1", Type = "number", Required = true },
            new ToolParam { Name = "y1", Type = "number", Required = true },
            new ToolParam { Name = "x2", Type = "number", Required = true },
            new ToolParam { Name = "y2", Type = "number", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");

            var res = doc.Editor.SelectCrossingWindow(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            if (res.Status != PromptStatus.OK || res.Value == null || res.Value.Count == 0)
                return ToolResult.Ok("선택 결과 없음");
            var ids = res.Value.GetObjectIds();
            return ToolResult.Ok($"select.crossing {ids.Length}개: {SelectHelpers.IdsToHandles(doc.Database, ids)}");
        }
    }

    public class SelectFenceTool : ChatTool
    {
        public override string Name => "select.fence";
        public override string Description => "꺾임 경로 (points: [[x,y],...]) 와 교차하는 엔티티들의 핸들 반환.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "points", Type = "array", Required = true, Description = "[[x,y],...] 펜스 경로 (2점 이상)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetPointArray(args, "points", out var pts) || pts.Count < 2)
                return ToolResult.Fail("points 누락 또는 2점 미만");

            var p3 = new Point3dCollection();
            foreach (Point2d p in pts) p3.Add(new Point3d(p.X, p.Y, 0));

            var res = doc.Editor.SelectFence(p3);
            if (res.Status != PromptStatus.OK || res.Value == null || res.Value.Count == 0)
                return ToolResult.Ok("선택 결과 없음");
            var ids = res.Value.GetObjectIds();
            return ToolResult.Ok($"select.fence {ids.Length}개: {SelectHelpers.IdsToHandles(doc.Database, ids)}");
        }
    }

    // ───────── 파일 ─────────

    public class WblockExportTool : ChatTool
    {
        public override string Name => "wblock.export";
        public override string Description => "지정한 핸들 엔티티들을 새 DWG 파일로 내보냄.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "path",    Type = "string", Required = true, Description = "저장 경로 (.dwg)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var path = DrawHelpers.GetString(args, "path");
            if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path 누락");
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);

            try
            {
                var idColl = new ObjectIdCollection();
                foreach (var id in ids) idColl.Add(id);
                using (var newDb = doc.Database.Wblock(idColl, Point3d.Origin))
                {
                    newDb.SaveAs(path, DwgVersion.Current);
                }
            }
            catch (Exception ex) { return ToolResult.Fail($"wblock 실패: {ex.Message}"); }
            return ToolResult.Ok($"wblock.export {ids.Count}개 → {path}");
        }
    }
}


