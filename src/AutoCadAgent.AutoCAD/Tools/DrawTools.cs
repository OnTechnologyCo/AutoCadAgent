using System;
using System.Collections.Generic;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // 범용 도형 도구. staged 도형은 세션 버퍼에 누적되며 draw.commit으로 생성된다.
    // 좌표는 draw.commit의 WCS 기준점에 더해지는 상대 좌표이다.
    internal static class DrawToolsExt
    {
        // 공통 옵션(layer, color) 적용 — 모든 draw.* 도구가 마지막에 호출.
        public static void ApplyCommon(DrawSession.PendingShape s, IDictionary<string, object> args)
        {
            s.Layer = DrawHelpers.GetString(args, "layer");
            if (DrawHelpers.TryGetInt(args, "color", out var c)) s.ColorIndex = c;
        }
    }

    // ───────── 시작 / 종료 / 상태 ─────────

    public class DrawStartSessionTool : ChatTool
    {
        public override string Name => "draw.start_session";
        public override string Description => "도형 버퍼 세션 시작. 이전 누적된 도형은 비워진다. description 은 사용자 원본 요청 텍스트.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "description", Type = "string", Description = "사용자 요청 한 줄 요약", Required = true },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var desc = DrawHelpers.GetString(args, "description", "");
            DrawSession.StartNew(desc);
            return ToolResult.Ok("도형 버퍼 세션 시작: " + desc);
        }
    }

    public class DrawClearSessionTool : ChatTool
    {
        public override string Name => "draw.clear_session";
        public override string Description => "현재 도형 버퍼 세션의 누적 도형을 모두 비운다.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            DrawSession.Clear();
            return ToolResult.Ok("세션 비움.");
        }
    }

    public class DrawSessionStatusTool : ChatTool
    {
        public override string Name => "draw.session_status";
        public override string Description => "현재 누적된 도형 개수 / 세션 ID / 설명 조회.";
        public override IList<ToolParam> Parameters => new ToolParam[0];
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            return ToolResult.Ok("도형 버퍼 상태", new { sessionId = DrawSession.SessionId, pendingCount = DrawSession.Count, description = DrawSession.Description });
        }
    }

    // ───────── 기본 도형 ─────────

    public class DrawLineTool : ChatTool
    {
        public override string Name => "draw.line";
        public override string Description => "직선. 좌표는 draw.commit 기준점에 대한 상대 좌표.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1", Type = "number", Required = true },
            new ToolParam { Name = "y1", Type = "number", Required = true },
            new ToolParam { Name = "x2", Type = "number", Required = true },
            new ToolParam { Name = "y2", Type = "number", Required = true },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");

            var s = new DrawSession.LineShape { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"line ({x1:0.##},{y1:0.##})→({x2:0.##},{y2:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawCircleTool : ChatTool
    {
        public override string Name => "draw.circle";
        public override string Description => "원. 중심(cx,cy)과 반지름(r). 지름 100 이면 r=50.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx", Type = "number", Required = true },
            new ToolParam { Name = "cy", Type = "number", Required = true },
            new ToolParam { Name = "r",  Type = "number", Required = true },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "r", out var r) || r <= 0) return ToolResult.Fail("r 누락 또는 0 이하");
            var s = new DrawSession.CircleShape { Cx = cx, Cy = cy, R = r };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"circle 중심({cx:0.##},{cy:0.##}) r={r:0.##} 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawArcTool : ChatTool
    {
        public override string Name => "draw.arc";
        public override string Description => "호. 중심+반지름+시작각/끝각(도). 0도=동쪽, 반시계방향.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",          Type = "number", Required = true },
            new ToolParam { Name = "cy",          Type = "number", Required = true },
            new ToolParam { Name = "r",           Type = "number", Required = true },
            new ToolParam { Name = "start_angle", Type = "number", Required = true, Description = "시작 각도(도)" },
            new ToolParam { Name = "end_angle",   Type = "number", Required = true, Description = "끝 각도(도)" },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "r", out var r) || r <= 0) return ToolResult.Fail("r 누락");
            if (!DrawHelpers.TryGetDouble(args, "start_angle", out var sa)) return ToolResult.Fail("start_angle 누락");
            if (!DrawHelpers.TryGetDouble(args, "end_angle", out var ea)) return ToolResult.Fail("end_angle 누락");
            var s = new DrawSession.ArcShape
            {
                Cx = cx, Cy = cy, R = r,
                StartAngleRad = DrawHelpers.Deg2Rad(sa),
                EndAngleRad = DrawHelpers.Deg2Rad(ea),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"arc 중심({cx:0.##},{cy:0.##}) r={r:0.##} {sa:0.#}°→{ea:0.#}° 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawEllipseTool : ChatTool
    {
        public override string Name => "draw.ellipse";
        public override string Description => "타원. 중심(cx,cy) + 장축 끝점 상대좌표(major_x, major_y) + 단축/장축 비율(0~1).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",      Type = "number", Required = true },
            new ToolParam { Name = "cy",      Type = "number", Required = true },
            new ToolParam { Name = "major_x", Type = "number", Required = true, Description = "장축 끝점 X (중심 기준 상대)" },
            new ToolParam { Name = "major_y", Type = "number", Required = true, Description = "장축 끝점 Y (중심 기준 상대)" },
            new ToolParam { Name = "ratio",   Type = "number", Required = true, Description = "단축/장축 비율 (0 < ratio ≤ 1)" },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "major_x", out var mx)) return ToolResult.Fail("major_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "major_y", out var my)) return ToolResult.Fail("major_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "ratio", out var ratio) || ratio <= 0 || ratio > 1)
                return ToolResult.Fail("ratio 누락 또는 0~1 범위 밖");

            var s = new DrawSession.EllipseShape
            {
                Cx = cx, Cy = cy, MajorX = mx, MajorY = my,
                RadiusRatio = ratio,
                StartAngleRad = 0, EndAngleRad = Math.PI * 2,
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"ellipse 중심({cx:0.##},{cy:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawPolylineTool : ChatTool
    {
        public override string Name => "draw.polyline";
        public override string Description => "폴리라인. points 는 [[x,y],[x,y],...] 배열. closed=true 면 닫힘.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "points", Type = "array",   Required = true, Description = "[[x,y],...]" },
            new ToolParam { Name = "closed", Type = "boolean", Required = false, Description = "닫힘 여부 (기본 false)" },
            new ToolParam { Name = "width",  Type = "number",  Required = false, Description = "전체 선 폭 (기본 0)" },
            new ToolParam { Name = "layer",  Type = "string",  Required = false },
            new ToolParam { Name = "color",  Type = "number",  Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetPointArray(args, "points", out var pts) || pts.Count < 2)
                return ToolResult.Fail("points 누락 또는 점이 2개 미만");

            var list = new List<Point2d>();
            foreach (Point2d p in pts) list.Add(p);

            DrawHelpers.TryGetDouble(args, "width", out var width);

            var s = new DrawSession.PolylineShape
            {
                Points = list,
                Closed = DrawHelpers.GetBool(args, "closed", false),
                GlobalWidth = width,
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"polyline {list.Count}점 closed={s.Closed} 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawPointTool : ChatTool
    {
        public override string Name => "draw.point";
        public override string Description => "점. 단순 마커.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x",     Type = "number", Required = true },
            new ToolParam { Name = "y",     Type = "number", Required = true },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var s = new DrawSession.PointShape { X = x, Y = y };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"point ({x:0.##},{y:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawTextTool : ChatTool
    {
        public override string Name => "draw.text";
        public override string Description => "단일 행 DBText. 위치(x,y) + 내용 + 글자 높이(기본 2.5) + 회전(도, 기본 0).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x",        Type = "number", Required = true },
            new ToolParam { Name = "y",        Type = "number", Required = true },
            new ToolParam { Name = "content",  Type = "string", Required = true },
            new ToolParam { Name = "height",   Type = "number", Required = false },
            new ToolParam { Name = "rotation", Type = "number", Required = false, Description = "회전 각도(도)" },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
            new ToolParam { Name = "color",    Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var content = DrawHelpers.GetString(args, "content", "");
            DrawHelpers.TryGetDouble(args, "height", out var height);
            DrawHelpers.TryGetDouble(args, "rotation", out var rotDeg);
            var s = new DrawSession.TextShape
            {
                X = x, Y = y, Content = content,
                Height = height, RotationRad = DrawHelpers.Deg2Rad(rotDeg),
                IsMText = false,
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"text \"{content}\" @ ({x:0.##},{y:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawMTextTool : ChatTool
    {
        public override string Name => "draw.mtext";
        public override string Description => "다중 행 MText. content 안에 \\n 으로 줄바꿈 가능.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x",        Type = "number", Required = true },
            new ToolParam { Name = "y",        Type = "number", Required = true },
            new ToolParam { Name = "content",  Type = "string", Required = true },
            new ToolParam { Name = "height",   Type = "number", Required = false },
            new ToolParam { Name = "rotation", Type = "number", Required = false, Description = "회전 각도(도)" },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
            new ToolParam { Name = "color",    Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x", out var x)) return ToolResult.Fail("x 누락");
            if (!DrawHelpers.TryGetDouble(args, "y", out var y)) return ToolResult.Fail("y 누락");
            var content = DrawHelpers.GetString(args, "content", "");
            DrawHelpers.TryGetDouble(args, "height", out var height);
            DrawHelpers.TryGetDouble(args, "rotation", out var rotDeg);
            var s = new DrawSession.TextShape
            {
                X = x, Y = y, Content = content,
                Height = height, RotationRad = DrawHelpers.Deg2Rad(rotDeg),
                IsMText = true,
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"mtext \"{content}\" @ ({x:0.##},{y:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    // ───────── 치수선 ─────────

    public class DrawDimAlignedTool : ChatTool
    {
        public override string Name => "draw.dim_aligned";
        public override string Description => "정렬 치수선. 두 점(x1,y1)~(x2,y2) 거리 + 치수선 베이스 위치(base_x, base_y).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1",     Type = "number", Required = true },
            new ToolParam { Name = "y1",     Type = "number", Required = true },
            new ToolParam { Name = "x2",     Type = "number", Required = true },
            new ToolParam { Name = "y2",     Type = "number", Required = true },
            new ToolParam { Name = "base_x", Type = "number", Required = true, Description = "치수선 위치 X" },
            new ToolParam { Name = "base_y", Type = "number", Required = true },
            new ToolParam { Name = "text",   Type = "string", Required = false, Description = "치수 텍스트 override (없으면 자동 측정값)" },
            new ToolParam { Name = "layer",  Type = "string", Required = false },
            new ToolParam { Name = "color",  Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_x", out var bx)) return ToolResult.Fail("base_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_y", out var by)) return ToolResult.Fail("base_y 누락");
            var s = new DrawSession.DimAlignedShape
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                BaseX = bx, BaseY = by,
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_aligned 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimRotatedTool : ChatTool
    {
        public override string Name => "draw.dim_rotated";
        public override string Description => "수평/수직/임의 회전 치수선. rotation 0=수평, 90=수직.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1",       Type = "number", Required = true },
            new ToolParam { Name = "y1",       Type = "number", Required = true },
            new ToolParam { Name = "x2",       Type = "number", Required = true },
            new ToolParam { Name = "y2",       Type = "number", Required = true },
            new ToolParam { Name = "base_x",   Type = "number", Required = true },
            new ToolParam { Name = "base_y",   Type = "number", Required = true },
            new ToolParam { Name = "rotation", Type = "number", Required = false, Description = "회전 각도(도). 기본 0(수평)" },
            new ToolParam { Name = "text",     Type = "string", Required = false },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
            new ToolParam { Name = "color",    Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_x", out var bx)) return ToolResult.Fail("base_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_y", out var by)) return ToolResult.Fail("base_y 누락");
            DrawHelpers.TryGetDouble(args, "rotation", out var rotDeg);
            var s = new DrawSession.DimRotatedShape
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                BaseX = bx, BaseY = by,
                RotationRad = DrawHelpers.Deg2Rad(rotDeg),
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_rotated rot={rotDeg:0.#}° 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimRadialTool : ChatTool
    {
        public override string Name => "draw.dim_radial";
        public override string Description => "반지름 치수선. 원/호 중심(cx,cy) + 코드 위 점(chord_x, chord_y) + 인출선 길이.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",         Type = "number", Required = true },
            new ToolParam { Name = "cy",         Type = "number", Required = true },
            new ToolParam { Name = "chord_x",    Type = "number", Required = true, Description = "원호 위의 점 X" },
            new ToolParam { Name = "chord_y",    Type = "number", Required = true },
            new ToolParam { Name = "leader_len", Type = "number", Required = false, Description = "인출선 길이 (기본 0)" },
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
            DrawHelpers.TryGetDouble(args, "leader_len", out var leader);
            var s = new DrawSession.DimRadialShape
            {
                Cx = cx, Cy = cy, ChordX = chx, ChordY = chy,
                LeaderLen = leader,
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_radial 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimDiametricTool : ChatTool
    {
        public override string Name => "draw.dim_diametric";
        public override string Description => "지름 치수선. 원 위의 두 점(x1,y1)~(x2,y2) — 보통 정반대 두 점.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1",         Type = "number", Required = true },
            new ToolParam { Name = "y1",         Type = "number", Required = true },
            new ToolParam { Name = "x2",         Type = "number", Required = true },
            new ToolParam { Name = "y2",         Type = "number", Required = true },
            new ToolParam { Name = "leader_len", Type = "number", Required = false },
            new ToolParam { Name = "text",       Type = "string", Required = false },
            new ToolParam { Name = "layer",      Type = "string", Required = false },
            new ToolParam { Name = "color",      Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            DrawHelpers.TryGetDouble(args, "leader_len", out var leader);
            var s = new DrawSession.DimDiametricShape
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                LeaderLen = leader,
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_diametric 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimAngularTool : ChatTool
    {
        public override string Name => "draw.dim_angular";
        public override string Description => "각도 치수선 (3점). 꼭짓점(vx,vy) + 두 변 위 점(p1, p2) + 호 위치(arc_x, arc_y).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "vx",    Type = "number", Required = true, Description = "꼭짓점 X" },
            new ToolParam { Name = "vy",    Type = "number", Required = true },
            new ToolParam { Name = "p1x",   Type = "number", Required = true, Description = "한쪽 변 위 점 X" },
            new ToolParam { Name = "p1y",   Type = "number", Required = true },
            new ToolParam { Name = "p2x",   Type = "number", Required = true, Description = "다른 변 위 점 X" },
            new ToolParam { Name = "p2y",   Type = "number", Required = true },
            new ToolParam { Name = "arc_x", Type = "number", Required = true, Description = "호 위치 X (치수선이 그려질 위치)" },
            new ToolParam { Name = "arc_y", Type = "number", Required = true },
            new ToolParam { Name = "text",  Type = "string", Required = false },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "vx", out var vx)) return ToolResult.Fail("vx 누락");
            if (!DrawHelpers.TryGetDouble(args, "vy", out var vy)) return ToolResult.Fail("vy 누락");
            if (!DrawHelpers.TryGetDouble(args, "p1x", out var p1x)) return ToolResult.Fail("p1x 누락");
            if (!DrawHelpers.TryGetDouble(args, "p1y", out var p1y)) return ToolResult.Fail("p1y 누락");
            if (!DrawHelpers.TryGetDouble(args, "p2x", out var p2x)) return ToolResult.Fail("p2x 누락");
            if (!DrawHelpers.TryGetDouble(args, "p2y", out var p2y)) return ToolResult.Fail("p2y 누락");
            if (!DrawHelpers.TryGetDouble(args, "arc_x", out var ax)) return ToolResult.Fail("arc_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "arc_y", out var ay)) return ToolResult.Fail("arc_y 누락");
            var s = new DrawSession.DimAngularShape
            {
                Vx = vx, Vy = vy, P1x = p1x, P1y = p1y, P2x = p2x, P2y = p2y, ArcX = ax, ArcY = ay,
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_angular 추가. 누적 {DrawSession.Count}개");
        }
    }

    // ───────── 배열 (대량 자동 생성) ─────────

    public class DrawArrayPolarLinesTool : ChatTool
    {
        public override string Name => "draw.array_polar_lines";
        public override string Description => "원형 배열로 직선 N개를 한 번에 추가. 중심에서 반지름 R 까지 N등분 방사선. 18등분 같은 작업에 유용.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",          Type = "number", Required = true,  Description = "중심 X" },
            new ToolParam { Name = "cy",          Type = "number", Required = true,  Description = "중심 Y" },
            new ToolParam { Name = "r",           Type = "number", Required = true,  Description = "반지름" },
            new ToolParam { Name = "count",       Type = "number", Required = true,  Description = "분할 수 (방사선 개수)" },
            new ToolParam { Name = "start_angle", Type = "number", Required = false, Description = "시작 각도(도). 기본 0" },
            new ToolParam { Name = "layer",       Type = "string", Required = false },
            new ToolParam { Name = "color",       Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "r", out var r) || r <= 0) return ToolResult.Fail("r 누락");
            if (!DrawHelpers.TryGetInt(args, "count", out var n) || n < 2) return ToolResult.Fail("count 는 2 이상");
            DrawHelpers.TryGetDouble(args, "start_angle", out var sa);

            var layer = DrawHelpers.GetString(args, "layer");
            int? color = null;
            if (DrawHelpers.TryGetInt(args, "color", out var c)) color = c;

            for (int i = 0; i < n; i++)
            {
                double ang = DrawHelpers.Deg2Rad(sa + 360.0 * i / n);
                var s = new DrawSession.LineShape
                {
                    X1 = cx, Y1 = cy,
                    X2 = cx + r * Math.Cos(ang),
                    Y2 = cy + r * Math.Sin(ang),
                    Layer = layer, ColorIndex = color,
                };
                DrawSession.Add(s);
            }
            return ToolResult.Ok($"array_polar_lines: {n}개 방사선 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawRegularPolygonTool : ChatTool
    {
        public override string Name => "draw.regular_polygon";
        public override string Description => "정 N각형 (외접원 반지름 r). 18각형으로 원 근사 같은 용도.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",          Type = "number", Required = true },
            new ToolParam { Name = "cy",          Type = "number", Required = true },
            new ToolParam { Name = "r",           Type = "number", Required = true, Description = "외접원 반지름" },
            new ToolParam { Name = "sides",       Type = "number", Required = true, Description = "변의 수 (3 이상)" },
            new ToolParam { Name = "start_angle", Type = "number", Required = false, Description = "첫 꼭짓점 각도(도). 기본 0" },
            new ToolParam { Name = "layer",       Type = "string", Required = false },
            new ToolParam { Name = "color",       Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "r", out var r) || r <= 0) return ToolResult.Fail("r 누락");
            if (!DrawHelpers.TryGetInt(args, "sides", out var sides) || sides < 3) return ToolResult.Fail("sides 는 3 이상");
            DrawHelpers.TryGetDouble(args, "start_angle", out var sa);

            var pts = new List<Point2d>();
            for (int i = 0; i < sides; i++)
            {
                double ang = DrawHelpers.Deg2Rad(sa + 360.0 * i / sides);
                pts.Add(new Point2d(cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
            var s = new DrawSession.PolylineShape { Points = pts, Closed = true };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"regular_polygon {sides}각형 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawRectangleTool : ChatTool
    {
        public override string Name => "draw.rectangle";
        public override string Description => "직사각형. 좌하단(x1,y1) ~ 우상단(x2,y2).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1",    Type = "number", Required = true },
            new ToolParam { Name = "y1",    Type = "number", Required = true },
            new ToolParam { Name = "x2",    Type = "number", Required = true },
            new ToolParam { Name = "y2",    Type = "number", Required = true },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            var pts = new List<Point2d>
            {
                new Point2d(x1, y1), new Point2d(x2, y1),
                new Point2d(x2, y2), new Point2d(x1, y2),
            };
            var s = new DrawSession.PolylineShape { Points = pts, Closed = true };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"rectangle ({x1:0.##},{y1:0.##})~({x2:0.##},{y2:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    // ───────── Phase 1 추가 도형 ─────────

    public class DrawSplineTool : ChatTool
    {
        public override string Name => "draw.spline";
        public override string Description => "스플라인 곡선. points 는 통과 점들 [[x,y],...]. closed=true 면 폐곡선.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "points", Type = "array",   Required = true, Description = "[[x,y],...] 통과 점 (3개 이상)" },
            new ToolParam { Name = "closed", Type = "boolean", Required = false },
            new ToolParam { Name = "layer",  Type = "string",  Required = false },
            new ToolParam { Name = "color",  Type = "number",  Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetPointArray(args, "points", out var pts) || pts.Count < 3)
                return ToolResult.Fail("points 누락 또는 점이 3개 미만");
            var list = new List<Point3d>();
            foreach (Point2d p in pts) list.Add(new Point3d(p.X, p.Y, 0));
            var s = new DrawSession.SplineShape
            {
                FitPoints = list,
                Closed = DrawHelpers.GetBool(args, "closed", false),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"spline {list.Count}점 closed={s.Closed} 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawHatchSolidTool : ChatTool
    {
        public override string Name => "draw.hatch_solid";
        public override string Description => "솔리드(꽉 찬) 해치. boundary 는 닫힌 영역 외곽 점 [[x,y],...]. commit 시 닫힌 경계와 SOLID Hatch 엔티티를 함께 생성.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "boundary", Type = "array", Required = true, Description = "[[x,y],...] 닫힌 영역 외곽" },
            new ToolParam { Name = "layer",    Type = "string", Required = false },
            new ToolParam { Name = "color",    Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetPointArray(args, "boundary", out var pts) || pts.Count < 3)
                return ToolResult.Fail("boundary 누락 또는 점이 3개 미만");
            var list = new List<Point2d>();
            foreach (Point2d p in pts) list.Add(p);
            var s = new DrawSession.HatchShape
            {
                BoundaryPoints = list,
                PatternName = "SOLID",
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"hatch_solid {list.Count}점 경계 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDonutTool : ChatTool
    {
        public override string Name => "draw.donut";
        public override string Description => "도넛: 외경/내경 경계와 SOLID 해치를 commit 시 생성.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",             Type = "number", Required = true },
            new ToolParam { Name = "cy",             Type = "number", Required = true },
            new ToolParam { Name = "outer_diameter", Type = "number", Required = true },
            new ToolParam { Name = "inner_diameter", Type = "number", Required = false, Description = "내경. 0이면 원판" },
            new ToolParam { Name = "layer",          Type = "string", Required = false },
            new ToolParam { Name = "color",          Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "outer_diameter", out var od) || od <= 0) return ToolResult.Fail("outer_diameter 누락");
            DrawHelpers.TryGetDouble(args, "inner_diameter", out var id);
            if (id < 0 || id >= od) return ToolResult.Fail("inner_diameter는 0 이상 외경 미만이어야 합니다.");
            var s = new DrawSession.DonutShape { Cx = cx, Cy = cy, OuterDiameter = od, InnerDiameter = id };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"donut 중심({cx:0.##},{cy:0.##}) 외경={od:0.##} 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawRayTool : ChatTool
    {
        public override string Name => "draw.ray";
        public override string Description => "반직선 (시작점 + 방향). bx,by 시작점, dx,dy 방향벡터.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "bx",    Type = "number", Required = true },
            new ToolParam { Name = "by",    Type = "number", Required = true },
            new ToolParam { Name = "dx",    Type = "number", Required = true },
            new ToolParam { Name = "dy",    Type = "number", Required = true },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "bx", out var bx)) return ToolResult.Fail("bx 누락");
            if (!DrawHelpers.TryGetDouble(args, "by", out var by)) return ToolResult.Fail("by 누락");
            if (!DrawHelpers.TryGetDouble(args, "dx", out var dx)) return ToolResult.Fail("dx 누락");
            if (!DrawHelpers.TryGetDouble(args, "dy", out var dy)) return ToolResult.Fail("dy 누락");
            if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) return ToolResult.Fail("방향벡터가 0");
            var s = new DrawSession.RayShape { Bx = bx, By = by, Dx = dx, Dy = dy };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"ray ({bx:0.##},{by:0.##}) 방향({dx:0.##},{dy:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawXlineTool : ChatTool
    {
        public override string Name => "draw.xline";
        public override string Description => "무한선 (한 점 + 방향). bx,by 통과점, dx,dy 방향벡터.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "bx",    Type = "number", Required = true },
            new ToolParam { Name = "by",    Type = "number", Required = true },
            new ToolParam { Name = "dx",    Type = "number", Required = true },
            new ToolParam { Name = "dy",    Type = "number", Required = true },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "bx", out var bx)) return ToolResult.Fail("bx 누락");
            if (!DrawHelpers.TryGetDouble(args, "by", out var by)) return ToolResult.Fail("by 누락");
            if (!DrawHelpers.TryGetDouble(args, "dx", out var dx)) return ToolResult.Fail("dx 누락");
            if (!DrawHelpers.TryGetDouble(args, "dy", out var dy)) return ToolResult.Fail("dy 누락");
            if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) return ToolResult.Fail("방향벡터가 0");
            var s = new DrawSession.XlineShape { Bx = bx, By = by, Dx = dx, Dy = dy };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"xline ({bx:0.##},{by:0.##}) 방향({dx:0.##},{dy:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawRevisionCloudTool : ChatTool
    {
        public override string Name => "draw.revision_cloud";
        public override string Description => "리비전 클라우드(타원형 호 연속). cx,cy 중심 + rx,ry 반축. bulge_count 호 개수(기본 16).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",           Type = "number", Required = true },
            new ToolParam { Name = "cy",           Type = "number", Required = true },
            new ToolParam { Name = "rx",           Type = "number", Required = true, Description = "X 반축" },
            new ToolParam { Name = "ry",           Type = "number", Required = true, Description = "Y 반축" },
            new ToolParam { Name = "bulge_count",  Type = "number", Required = false },
            new ToolParam { Name = "layer",        Type = "string", Required = false },
            new ToolParam { Name = "color",        Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "rx", out var rx) || rx <= 0) return ToolResult.Fail("rx 누락");
            if (!DrawHelpers.TryGetDouble(args, "ry", out var ry) || ry <= 0) return ToolResult.Fail("ry 누락");
            int bc = 16;
            if (DrawHelpers.TryGetInt(args, "bulge_count", out var bcArg) && bcArg >= 4) bc = bcArg;
            var s = new DrawSession.RevisionCloudShape { Cx = cx, Cy = cy, Rx = rx, Ry = ry, BulgeCount = bc };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"revision_cloud 중심({cx:0.##},{cy:0.##}) 반축({rx:0.##},{ry:0.##}) bc={bc} 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimLinearTool : ChatTool
    {
        public override string Name => "draw.dim_linear";
        public override string Description => "선형 치수 (수평 horizontal=true / 수직 horizontal=false).";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "x1",         Type = "number", Required = true },
            new ToolParam { Name = "y1",         Type = "number", Required = true },
            new ToolParam { Name = "x2",         Type = "number", Required = true },
            new ToolParam { Name = "y2",         Type = "number", Required = true },
            new ToolParam { Name = "base_x",     Type = "number", Required = true },
            new ToolParam { Name = "base_y",     Type = "number", Required = true },
            new ToolParam { Name = "horizontal", Type = "boolean", Required = false, Description = "기본 true" },
            new ToolParam { Name = "text",       Type = "string", Required = false },
            new ToolParam { Name = "layer",      Type = "string", Required = false },
            new ToolParam { Name = "color",      Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "x1", out var x1)) return ToolResult.Fail("x1 누락");
            if (!DrawHelpers.TryGetDouble(args, "y1", out var y1)) return ToolResult.Fail("y1 누락");
            if (!DrawHelpers.TryGetDouble(args, "x2", out var x2)) return ToolResult.Fail("x2 누락");
            if (!DrawHelpers.TryGetDouble(args, "y2", out var y2)) return ToolResult.Fail("y2 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_x", out var bx)) return ToolResult.Fail("base_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_y", out var by)) return ToolResult.Fail("base_y 누락");
            var s = new DrawSession.DimLinearShape
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, BaseX = bx, BaseY = by,
                Horizontal = DrawHelpers.GetBool(args, "horizontal", true),
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_linear horizontal={s.Horizontal} 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimArcLengthTool : ChatTool
    {
        public override string Name => "draw.dim_arc_length";
        public override string Description => "호 길이 치수. 중심(cx,cy) + 호 시작점/끝점 + 치수선 위치.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",          Type = "number", Required = true },
            new ToolParam { Name = "cy",          Type = "number", Required = true },
            new ToolParam { Name = "arc_start_x", Type = "number", Required = true },
            new ToolParam { Name = "arc_start_y", Type = "number", Required = true },
            new ToolParam { Name = "arc_end_x",   Type = "number", Required = true },
            new ToolParam { Name = "arc_end_y",   Type = "number", Required = true },
            new ToolParam { Name = "base_x",      Type = "number", Required = true },
            new ToolParam { Name = "base_y",      Type = "number", Required = true },
            new ToolParam { Name = "text",        Type = "string", Required = false },
            new ToolParam { Name = "layer",       Type = "string", Required = false },
            new ToolParam { Name = "color",       Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            if (!DrawHelpers.TryGetDouble(args, "arc_start_x", out var asx)) return ToolResult.Fail("arc_start_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "arc_start_y", out var asy)) return ToolResult.Fail("arc_start_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "arc_end_x", out var aex)) return ToolResult.Fail("arc_end_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "arc_end_y", out var aey)) return ToolResult.Fail("arc_end_y 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_x", out var bx)) return ToolResult.Fail("base_x 누락");
            if (!DrawHelpers.TryGetDouble(args, "base_y", out var by)) return ToolResult.Fail("base_y 누락");
            var s = new DrawSession.DimArcLengthShape
            {
                Cx = cx, Cy = cy,
                ArcStartX = asx, ArcStartY = asy,
                ArcEndX = aex, ArcEndY = aey,
                BaseX = bx, BaseY = by,
                DimText = DrawHelpers.GetString(args, "text"),
            };
            DrawToolsExt.ApplyCommon(s, args);
            DrawSession.Add(s);
            return ToolResult.Ok($"dim_arc_length 추가. 누적 {DrawSession.Count}개");
        }
    }

    public class DrawDimCenterMarkTool : ChatTool
    {
        public override string Name => "draw.dim_center_mark";
        public override string Description => "중심 마크 (+ 자형 두 직선). 원/호 중심에 시각적 마크.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "cx",    Type = "number", Required = true },
            new ToolParam { Name = "cy",    Type = "number", Required = true },
            new ToolParam { Name = "size",  Type = "number", Required = false, Description = "마크 절반 길이 (기본 5)" },
            new ToolParam { Name = "layer", Type = "string", Required = false },
            new ToolParam { Name = "color", Type = "number", Required = false },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            if (!DrawHelpers.TryGetDouble(args, "cx", out var cx)) return ToolResult.Fail("cx 누락");
            if (!DrawHelpers.TryGetDouble(args, "cy", out var cy)) return ToolResult.Fail("cy 누락");
            DrawHelpers.TryGetDouble(args, "size", out var size);
            if (size <= 0) size = 5;
            var layer = DrawHelpers.GetString(args, "layer");
            int? color = null;
            if (DrawHelpers.TryGetInt(args, "color", out var ci)) color = ci;
            // 가로 + 세로 두 직선 누적
            DrawSession.Add(new DrawSession.LineShape
            {
                X1 = cx - size, Y1 = cy, X2 = cx + size, Y2 = cy,
                Layer = layer, ColorIndex = color,
            });
            DrawSession.Add(new DrawSession.LineShape
            {
                X1 = cx, Y1 = cy - size, X2 = cx, Y2 = cy + size,
                Layer = layer, ColorIndex = color,
            });
            return ToolResult.Ok($"dim_center_mark 중심({cx:0.##},{cy:0.##}) 추가. 누적 {DrawSession.Count}개");
        }
    }

    // ───────── 레이어 ─────────

    public class DrawLayerCreateTool : ChatTool
    {
        public override string Name => "draw.layer_create";
        public override string Description => "레이어 생성 (이미 있으면 무시). 즉시 도면에 반영.";
        public override IList<ToolParam> Parameters => new ToolParam[]
        {
            new ToolParam { Name = "name",  Type = "string", Required = true },
            new ToolParam { Name = "color", Type = "number", Required = false, Description = "레이어 색상 (ACI)" },
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var name = DrawHelpers.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("name 누락");
            var doc = DrawHelpers.GetActiveDoc(out var err);
            if (doc == null) return ToolResult.Fail(err);
            using (doc.LockDocument())
            {
                DrawHelpers.EnsureLayer(doc.Database, name);
                if (DrawHelpers.TryGetInt(args, "color", out var colorIdx))
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                        if (lt.Has(name))
                        {
                            var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                            rec.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)Math.Max(1, Math.Min(255, colorIdx)));
                        }
                        tr.Commit();
                    }
                }
            }
            return ToolResult.Ok($"레이어 '{name}' 준비됨.");
        }
    }
}


