using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AutoCadAgent.Cad.Tools;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoCadAgent.Cad
{
    public static class ToolRegistry
    {
        private static readonly Dictionary<string, ChatTool> Tools = typeof(ChatTool).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ChatTool).IsAssignableFrom(t))
            .Select(t => (ChatTool)Activator.CreateInstance(t)).ToDictionary(t => t.Name, StringComparer.Ordinal);

        public static object[] List(string filter = "") => Tools.Values.Where(t => t.Name.StartsWith(filter, StringComparison.Ordinal))
            .OrderBy(t => t.Name).Select(ToolContract.Describe).ToArray();
        public static object Describe(string name) => Tools.TryGetValue(name ?? "", out var tool) ? ToolContract.Describe(tool) : null;
        public static bool IsStaged(string name) => ToolContract.Staged.Contains(name);

        public static ToolResult Execute(string name, IDictionary<string, object> args)
        {
            if (!Tools.TryGetValue(name ?? "", out var tool)) return ToolResult.Fail("알 수 없는 도구: " + name, "unknown_tool");
            args = args ?? new Dictionary<string, object>();
            var validation = ToolContract.Validate(tool.Parameters, args);
            if (validation != null) return ToolResult.Fail(validation, "invalid_arguments");
            foreach(var key in new[] {"count","sides","bulge_count","segments_per_turn","rows","columns"})
                if(DrawHelpers.TryGetDouble(args,key,out var size) && (size < 0 || size > 10000)) return ToolResult.Fail(key + ": 0~10000 범위 필요");
            if(name=="draw.helix_2d" && DrawHelpers.TryGetDouble(args,"turns",out var turns)) {
                var segments=DrawHelpers.TryGetDouble(args,"segments_per_turn",out var n) ? n : 36;
                if(Math.Abs(turns*segments)>10000) return ToolResult.Fail("나선 점 개수 한도 10000 초과");
            }
            foreach (var p in tool.Parameters ?? new ToolParam[0])
                if (p.Required && (!args.ContainsKey(p.Name) || args[p.Name] == null)) return ToolResult.Fail("필수 인자 누락: " + p.Name);
            return tool.Execute(args);
        }
    }

    public sealed class DrawCommitTool : ChatTool
    {
        public override string Name => "draw.commit";
        public override string Description => "현재 CLI 세션의 누적 도형을 WCS 기준점(x,y)에 일괄 생성. 성공 시 실제 핸들을 반환하고 버퍼를 비움.";
        public override IList<ToolParam> Parameters => new[] {
            new ToolParam { Name="x", Type="number", Required=true }, new ToolParam { Name="y", Type="number", Required=true },
            new ToolParam { Name="layer", Type="string" }, new ToolParam { Name="color", Type="number" }
        };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var doc = DrawHelpers.GetActiveDoc(out var error);
            if (doc == null) return ToolResult.Fail(error);
            if (DrawSession.Count == 0) return ToolResult.Fail("누적 도형이 없습니다.");
            if (!DrawHelpers.TryGetDouble(args,"x",out var x) || !DrawHelpers.TryGetDouble(args,"y",out var y)) return ToolResult.Fail("x,y 필수");
            int? color = DrawHelpers.TryGetInt(args,"color",out var c) ? c : (int?)null;
            using (doc.LockDocument()) {
                var handles = DrawSession.FlushAt(doc.Database,x,y,DrawHelpers.GetString(args,"layer"),color);
                string displayWarning = null;
                try { doc.Editor.Regen(); } catch (System.Exception ex) { displayWarning = "형상은 생성되었지만 화면 갱신 실패: " + ex.Message; }
                return ToolResult.Ok(handles.Count + "개 생성 완료", new { handles, count = handles.Count, pendingCount = DrawSession.Count, displayWarning });
            }
        }
    }
    public sealed class SaveCopyTool : ChatTool
    {
        public override string Name => "drawing.save_copy";
        public override string Description => "도면을 새 DWG 경로에 복사 저장. 기존 파일 덮어쓰기는 거부.";
        public override IList<ToolParam> Parameters => new[] { new ToolParam { Name="path", Type="string", Required=true } };
        public override ToolResult Execute(IDictionary<string, object> args)
        {
            var path = DrawHelpers.GetString(args,"path");
            if (!System.IO.Path.IsPathRooted(path) || !path.EndsWith(".dwg",StringComparison.OrdinalIgnoreCase)) return ToolResult.Fail("절대 DWG 경로가 필요합니다.");
            if (System.IO.File.Exists(path)) return ToolResult.Fail("이미 존재하는 파일입니다. 새 경로를 지정하세요.");
            var doc = DrawHelpers.GetActiveDoc(out var error);
            if (doc == null) return ToolResult.Fail(error);
            using (doc.LockDocument())
            using (var copy = doc.Database.Wblock()) copy.SaveAs(path,DwgVersion.Current);
            return ToolResult.Ok("DWG 복사 저장 완료", new { path });
        }
    }
}
