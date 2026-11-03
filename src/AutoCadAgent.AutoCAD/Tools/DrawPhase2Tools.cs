using System;
using System.Collections.Generic;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // Phase 2 도구 모음.
    //
    // 누적(DrawSession) 도형: ordinate / jogged radius / leader / tolerance / wipeout
    // 즉시 처리: textstyle / dimstyle / table / region / mleader / attdef
    //
    // 즉시 처리 도구는 활성 도면이 있고 LockDocument 안에서 트랜잭션을 사용한다.

    // ───────── 누적 도형 도구 ─────────

    public class DrawDimOrdinateTool : ChatTool
    {
        public override string Name => "draw.dim_ordinate";
        public override string Description => "좌표(원점) 치수. feature 점의 x 또는 y 좌표를 leader 위치에 표시. using_x_axis=true 면 X좌표.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "feature_x",     Type = "number",  Required = true, Description = "측정 대상 점 X" },
            new ToolParam { Name = "feature_y",     Type = "number",  Required = true },
            new ToolParam { Name = "leader_x",      Type = "number",  Required = true, Description = "치수선 끝 위치 X" },
            new ToolParam { Name = "leader_y",      Type = "number",  Required = true },
            new ToolParam { Name = "using_x_axis",  Type = "boolean", Required = false, Description = "true=X좌표 측정 / false=Y좌표 측정 (기본 true)" },
            new ToolParam { Name = "text",          Type = "string",  Required = false },
            new ToolParam { Name = "layer",         Type = "string",  Required = false },
            new ToolParam { Name = "color",         Type = "number",  Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "feature_x", out var fx)) return ToolResult.Fail("feature_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "feature_y", out var fy)) return ToolResult.Fail("feature_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "leader_x", out var lx)) return ToolResult.Fail("leader_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "leader_y", out var ly)) return ToolResult.Fail("leader_y 누락");
            var s = new DrawSession.DimOrdinateShape
            {
                FeatureX = fx, FeatureY = fy, LeaderX = lx, LeaderY = ly,
                UsingXAxis = DrawHelpers.GetBool(args, "using_x_axis", true),
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_ordinate 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimJoggedTool : ChatTool
    {
        public override string Name => "draw.dim_jogged";
        public override string Description => "Jogged 반지름 치수 (큰 원의 반지름 부분 표시). cx,cy 중심 / chord 호 위 점 / jog 꺾이는 위치 / override 텍스트 위치.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",         Type = "number", Required = true },
            new ToolParam { Name = "cy",         Type = "number", Required = true },
            new ToolParam { Name = "chord_x",    Type = "number", Required = true },
            new ToolParam { Name = "chord_y",    Type = "number", Required = true },
            new ToolParam { Name = "jog_x",      Type = "number", Required = true, Description = "꺾이는 위치 X" },
            new ToolParam { Name = "jog_y",      Type = "number", Required = true },
            new ToolParam { Name = "override_x", Type = "number", Required = true, Description = "텍스트 위치 X" },
            new ToolParam { Name = "override_y", Type = "number", Required = true },
            new ToolParam { Name = "text",       Type = "string", Required = false },
            new ToolParam { Name = "layer",      Type = "string", Required = false },
            new ToolParam { Name = "color",      Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "chord_x", out var chx)) return ToolResult.Fail("chord_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "chord_y", out var chy)) return ToolResult.Fail("chord_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "jog_x", out var jx)) return ToolResult.Fail("jog_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "jog_y", out var jy)) return ToolResult.Fail("jog_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "override_x", out var ox)) return ToolResult.Fail("override_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "override_y", out var oy)) return ToolResult.Fail("override_y 누락");
            var s = new DrawSession.DimJoggedShape
            {
                Cx = cx, Cy = cy, ChordX = chx, ChordY = chy,
                JogX = jx, JogY = jy, OverrideX = ox, OverrideY = oy,
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_jogged 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawLeaderTool : ChatTool
    {
        public override string Name => "draw.leader";
        public override string Description => "단일 인출선 (꺾임 가능). vertices 는 [[x,y],...] 정점 (최소 2개). 화살표는 첫 정점에서 시작. 텍스트가 필요하면 끝 정점 위치에 별도 draw.text 추가.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "vertices", Type = "array",  Required = true, Description = "[[x,y],...] 인출선 정점 (2개 이상)" },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
            new ToolParam { Name = "color",    Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetPointArray(args, "vertices", out var pts) || pts.Count < 2)
                return ToolResult.Fail("vertices 누락 또는 정점 2개 미만");
            var list = new List<Point2d>();
            foreach (Point2d p in pts) list.Add(p);
            var s = new DrawSession.LeaderShape { Vertices = list };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"leader {list.Count}점 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawToleranceTool : ChatTool
    {
        public override string Name => "draw.tolerance";
        public override string Description => "Geometric Tolerance(GD&T) 박스. content 는 control frame 문자열 (예: '{\\Fgdt;j}%%v∅0.05%%vA').";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x",       Type = "number", Required = true },
            new ToolParam { Name = "y",       Type = "number", Required = true },
            new ToolParam { Name = "content", Type = "string", Required = true },
            new ToolParam { Name = "layer",   Type = "string", Required = false },
            new ToolParam { Name = "color",   Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var content = DrawHelpers.GetString(args, "content", "");
            var s = new DrawSession.ToleranceShape { X = x, Y = y, Content = content };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"tolerance @ ({x:0.##},{y:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawWipeoutTool : ChatTool
    {
        public override string Name => "draw.wipeout";
        public override string Description => "와이프아웃(가려짐 영역). vertices 는 [[x,y],...] 닫힌 다각형 정점.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "vertices", Type = "array",  Required = true },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
            new ToolParam { Name = "color",    Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetPointArray(args, "vertices", out var pts) || pts.Count < 3)
                return ToolResult.Fail("vertices 누락 또는 정점 3개 미만");
            var list = new List<Point2d>();
            foreach (Point2d p in pts) list.Add(p);
            var s = new DrawSession.WipeoutShape { Vertices = list };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"wipeout {list.Count}점 추가. 누적 {DrawSession.Count}개");
        }
    }

    // ───────── 즉시 처리: 스타일 ─────────

    public class TextStyleCreateTool : ChatTool
    {
        public override string Name => "textstyle.create";
        public override string Description => "텍스트 스타일 생성. font 는 SHX 또는 TTF 파일명 (예: 'arial.ttf' / 'romans.shx').";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",   Type = "string", Required = true },
            new ToolParam { Name = "font",   Type = "string", Required = true, Description = "폰트 파일명 (예: arial.ttf)" },
            new ToolParam { Name = "height", Type = "number", Required = false, Description = "고정 높이 (0=가변)" },
            new ToolParam { Name = "width_factor", Type = "number", Required = false, Description = "폭 비율 (기본 1)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var font = DrawHelpers.GetString(args, "font", "arial.ttf");
            DrawHelpers.TryGetDouble(args, "height", out var height);
            if (!DrawHelpers.TryGetDouble(args, "width_factor", out var wf)) wf = 1.0;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var tst = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForWrite);
                TextStyleTableRecord rec;
                if (tst.Has(name))
                {
                    rec = (TextStyleTableRecord)tr.GetObject(tst[name], OpenMode.ForWrite);
                }
                else
                {
                    rec = new TextStyleTableRecord { Name = name };
                    tst.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                rec.FileName = font;
                rec.TextSize = height;
                rec.XScale = wf;
                tr.Commit();
            }
            return ToolResult.Ok($"textstyle '{name}' (font={font}, h={height}, wf={wf}) 준비됨.");
        }
    }

    public class DimStyleCreateTool : ChatTool
    {
        public override string Name => "dimstyle.create";
        public override string Description => "치수 스타일 생성/갱신. scale=DIMSCALE / arrow_size=화살표 크기 / text_height=치수 텍스트 높이.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",        Type = "string", Required = true },
            new ToolParam { Name = "scale",       Type = "number", Required = false, Description = "전체 스케일 (기본 1)" },
            new ToolParam { Name = "arrow_size",  Type = "number", Required = false, Description = "화살표 크기 (기본 2.5)" },
            new ToolParam { Name = "text_height", Type = "number", Required = false, Description = "치수 텍스트 높이 (기본 2.5)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            DrawHelpers.TryGetDouble(args, "scale", out var scale); if (scale <= 0) scale = 1;
            DrawHelpers.TryGetDouble(args, "arrow_size", out var asz); if (asz <= 0) asz = 2.5;
            DrawHelpers.TryGetDouble(args, "text_height", out var th); if (th <= 0) th = 2.5;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var dst = (DimStyleTable)tr.GetObject(doc.Database.DimStyleTableId, OpenMode.ForWrite);
                DimStyleTableRecord rec;
                if (dst.Has(name))
                {
                    rec = (DimStyleTableRecord)tr.GetObject(dst[name], OpenMode.ForWrite);
                }
                else
                {
                    rec = new DimStyleTableRecord { Name = name };
                    dst.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                rec.Dimscale = scale;
                rec.Dimasz = asz;
                rec.Dimtxt = th;
                tr.Commit();
            }
            return ToolResult.Ok($"dimstyle '{name}' (scale={scale}, arrow={asz}, h={th}) 준비됨.");
        }
    }

    public class DimStyleSetCurrentTool : ChatTool
    {
        public override string Name => "dimstyle.set_current";
        public override string Description => "현재 치수 스타일 설정.";
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
                var dst = (DimStyleTable)tr.GetObject(doc.Database.DimStyleTableId, OpenMode.ForRead);
                if (!dst.Has(name)) { tr.Abort(); return ToolResult.Fail($"dimstyle '{name}' 없음"); }
                doc.Database.Dimstyle = dst[name];
                tr.Commit();
            }
            return ToolResult.Ok($"현재 dimstyle '{name}'");
        }
    }

    // ───────── 즉시 처리: 테이블 ─────────

    public class TableInsertTool : ChatTool
    {
        public override string Name => "table.insert";
        public override string Description => "테이블 삽입. (x,y) 위치에 rows×cols 셀 테이블. 셀 너비/높이/내용은 옵션.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x",           Type = "number", Required = true },
            new ToolParam { Name = "y",           Type = "number", Required = true },
            new ToolParam { Name = "rows",        Type = "number", Required = true },
            new ToolParam { Name = "cols",        Type = "number", Required = true },
            new ToolParam { Name = "row_height",  Type = "number", Required = false, Description = "기본 8" },
            new ToolParam { Name = "col_width",   Type = "number", Required = false, Description = "기본 30" },
            new ToolParam { Name = "data",        Type = "array",  Required = false, Description = "[[r0c0,r0c1,...],...] 셀 텍스트" },
            new ToolParam { Name = "layer",       Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            if (!DrawHelpers.TryGetInt(args, "rows", out var rows) || rows < 1) return ToolResult.Fail("rows 누락");
            if (!DrawHelpers.TryGetInt(args, "cols", out var cols) || cols < 1) return ToolResult.Fail("cols 누락");
            DrawHelpers.TryGetDouble(args, "row_height", out var rh); if (rh <= 0) rh = 8;
            DrawHelpers.TryGetDouble(args, "col_width", out var cw); if (cw <= 0) cw = 30;
            var layer = DrawHelpers.GetString(args, "layer");

            // data: object[][] 형태
            object[] dataRaw = null;
            if (args.ContainsKey("data") && args["data"] is object[] arr) dataRaw = arr;

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var tbl = new Table();
                tbl.TableStyle = doc.Database.Tablestyle;
                tbl.Position = new Point3d(x, y, 0);
                tbl.SetSize(rows, cols);
                for (int r = 0; r < rows; r++) tbl.Rows[r].Height = rh;
                for (int c = 0; c < cols; c++) tbl.Columns[c].Width = cw;

                if (dataRaw != null)
                {
                    for (int r = 0; r < rows && r < dataRaw.Length; r++)
                    {
                        if (!(dataRaw[r] is object[] rowArr)) continue;
                        for (int c = 0; c < cols && c < rowArr.Length; c++)
                        {
                            var cell = rowArr[c]?.ToString() ?? "";
                            tbl.Cells[r, c].TextString = cell;
                        }
                    }
                }
                tbl.GenerateLayout();
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    DrawHelpers.EnsureLayer(doc.Database, layer);
                    tbl.Layer = layer;
                }
                ms.AppendEntity(tbl);
                tr.AddNewlyCreatedDBObject(tbl, true);
                newHandle = tbl.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"table.insert {rows}×{cols} @ ({x:0.##},{y:0.##}) → {newHandle}");
        }
    }

    // ───────── 즉시 처리: Region (Curve 들로부터 영역 생성) ─────────

    public class RegionCreateTool : ChatTool
    {
        public override string Name => "region.create";
        public override string Description => "Curve 들 (handles 핸들 배열)로 닫힌 영역 Region 생성. 원본 Curve 는 유지.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "handles", Type = "array",  Required = true },
            new ToolParam { Name = "layer",   Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            var ids = EditToolsExt.ResolveHandles(args, "handles", doc.Database, out var werr);
            if (ids == null) return ToolResult.Fail(werr);
            var layer = DrawHelpers.GetString(args, "layer");

            var newHandles = new List<string>();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var coll = new DBObjectCollection();
                foreach (var id in ids)
                {
                    var c = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (c != null) coll.Add(c);
                }
                if (coll.Count == 0) { tr.Abort(); return ToolResult.Fail("Curve 가 없음"); }
                try
                {
                    var regions = Region.CreateFromCurves(coll);
                    foreach (Region r in regions)
                    {
                        if (!string.IsNullOrWhiteSpace(layer))
                        {
                            DrawHelpers.EnsureLayer(doc.Database, layer);
                            r.Layer = layer;
                        }
                        ms.AppendEntity(r);
                        tr.AddNewlyCreatedDBObject(r, true);
                        newHandles.Add(r.Handle.Value.ToString("X"));
                    }
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    return ToolResult.Fail($"region 생성 실패: {ex.Message}");
                }
                tr.Commit();
            }
            return ToolResult.Ok($"region.create {newHandles.Count}개 → 핸들: {string.Join(",", newHandles)}");
        }
    }

    // ───────── 즉시 처리: MLeader / Attdef ─────────

    public class DrawMLeaderTool : ChatTool
    {
        public override string Name => "draw.mleader";
        public override string Description => "다중 인출선(MLeader). dogleg 길이/방향 자동. arrow_x,arrow_y → text_x,text_y 로 인출선이 그려지고 끝점에 MText 표시. (즉시 처리)";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "arrow_x",     Type = "number", Required = true, Description = "화살표 시작 X" },
            new ToolParam { Name = "arrow_y",     Type = "number", Required = true },
            new ToolParam { Name = "text_x",      Type = "number", Required = true, Description = "텍스트 위치 X" },
            new ToolParam { Name = "text_y",      Type = "number", Required = true },
            new ToolParam { Name = "content",     Type = "string", Required = true, Description = "MText 내용" },
            new ToolParam { Name = "text_height", Type = "number", Required = false, Description = "기본 2.5" },
            new ToolParam { Name = "layer",       Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "arrow_x", out var ax)) return ToolResult.Fail("arrow_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "arrow_y", out var ay)) return ToolResult.Fail("arrow_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "text_x", out var tx)) return ToolResult.Fail("text_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "text_y", out var ty)) return ToolResult.Fail("text_y 누락");
            var content = DrawHelpers.GetString(args, "content", "");
            DrawHelpers.TryGetDouble(args, "text_height", out var th); if (th <= 0) th = 2.5;
            var layer = DrawHelpers.GetString(args, "layer");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var ml = new MLeader();
                ml.ContentType = ContentType.MTextContent;
                int leaderIdx = ml.AddLeader();
                int leaderLineIdx = ml.AddLeaderLine(leaderIdx);
                ml.AddFirstVertex(leaderLineIdx, new Point3d(ax, ay, 0));
                ml.AddLastVertex(leaderLineIdx, new Point3d(tx, ty, 0));

                var mt = new MText
                {
                    Contents = content,
                    Location = new Point3d(tx, ty, 0),
                    TextHeight = th,
                };
                ml.MText = mt;

                if (!string.IsNullOrWhiteSpace(layer))
                {
                    DrawHelpers.EnsureLayer(doc.Database, layer);
                    ml.Layer = layer;
                }

                ms.AppendEntity(ml);
                tr.AddNewlyCreatedDBObject(ml, true);
                newHandle = ml.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"draw.mleader → {newHandle}");
        }
    }

    public class AttdefCreateTool : ChatTool
    {
        public override string Name => "attdef.create";
        public override string Description => "Attribute Definition 생성 — 블록 정의 안에 들어가는 속성 자리표시자. 단독 도면에 그리면 일반 텍스트처럼 보임.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x",       Type = "number", Required = true },
            new ToolParam { Name = "y",       Type = "number", Required = true },
            new ToolParam { Name = "tag",     Type = "string", Required = true, Description = "속성 태그 (변수명, 예: PART_NO)" },
            new ToolParam { Name = "prompt",  Type = "string", Required = false, Description = "사용자 입력 프롬프트" },
            new ToolParam { Name = "default", Type = "string", Required = false, Description = "기본값" },
            new ToolParam { Name = "height",  Type = "number", Required = false, Description = "기본 2.5" },
            new ToolParam { Name = "layer",   Type = "string", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var e); if (doc == null) return ToolResult.Fail(e);
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var tag = DrawHelpers.GetString(args, "tag");
            if (string.IsNullOrWhiteSpace(tag)) return ToolResult.Fail("tag 누락");
            var prompt = DrawHelpers.GetString(args, "prompt", "");
            var defv = DrawHelpers.GetString(args, "default", "");
            DrawHelpers.TryGetDouble(args, "height", out var h); if (h <= 0) h = 2.5;
            var layer = DrawHelpers.GetString(args, "layer");

            string newHandle;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var ad = new AttributeDefinition
                {
                    Position = new Point3d(x, y, 0),
                    Tag = tag,
                    Prompt = prompt,
                    TextString = defv,
                    Height = h,
                };
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    DrawHelpers.EnsureLayer(doc.Database, layer);
                    ad.Layer = layer;
                }
                ms.AppendEntity(ad);
                tr.AddNewlyCreatedDBObject(ad, true);
                newHandle = ad.Handle.Value.ToString("X");
                tr.Commit();
            }
            return ToolResult.Ok($"attdef '{tag}' @ ({x:0.##},{y:0.##}) → {newHandle}");
        }
    }
}


