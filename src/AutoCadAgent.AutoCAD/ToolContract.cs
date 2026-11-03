using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoCadAgent.Cad
{
    // Shared by the real CAD registry and net48 contract tests. No Autodesk dependency.
    internal static class ToolContract
    {
        internal static readonly HashSet<string> Staged = new HashSet<string>(StringComparer.Ordinal) {
            "draw.line", "draw.circle", "draw.arc", "draw.ellipse", "draw.polyline", "draw.point", "draw.text", "draw.mtext",
            "draw.rectangle", "draw.regular_polygon", "draw.array_polar_lines", "draw.spline", "draw.hatch_solid", "draw.donut",
            "draw.ray", "draw.xline", "draw.revision_cloud", "draw.leader", "draw.tolerance", "draw.wipeout", "draw.helix_2d",
            "draw.dim_aligned", "draw.dim_rotated", "draw.dim_radial", "draw.dim_diametric", "draw.dim_angular", "draw.dim_linear",
            "draw.dim_arc_length", "draw.dim_center_mark", "draw.dim_ordinate", "draw.dim_jogged"
        };
        private static readonly HashSet<string> Reads = new HashSet<string>(StringComparer.Ordinal) {
            "draw.session_status", "drawing.summary_full", "drawing.entity_detail", "drawing.entity_neighbors",
            "drawing.list_texts", "drawing.list_dimensions", "drawing.list_blocks", "drawing.list_in_region", "drawing.list_selected",
            "drawing.list_polylines", "drawing.list_by_layer", "drawing.list_by_type", "drawing.list_by_color",
            "block.list", "dimstyle.list", "group.list", "layer.list", "layout.list", "linetype.list", "textstyle.list",
            "query.area", "query.distance", "query.id_point", "query.list", "select.window", "select.crossing", "select.fence"
        };
        private static readonly HashSet<string> Integers = new HashSet<string>(StringComparer.Ordinal) { "count", "sides", "bulge_count", "segments_per_turn", "rows", "columns", "index", "max", "color", "color_index", "ci" };
        internal static string Kind(string name) => Staged.Contains(name) ? "staged" : Reads.Contains(name) ? "read" : name == "draw.start_session" || name == "draw.clear_session" ? "buffer" : "immediate";
        internal static object Describe(ChatTool tool)
        {
            var parameters = tool.Parameters ?? new ToolParam[0];
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in parameters) {
                var schema = new Dictionary<string, object> { ["type"] = p.Type == "number" && Integers.Contains(p.Name) ? "integer" : p.Type };
                if (!string.IsNullOrWhiteSpace(p.Description)) schema["description"] = p.Description;
                if (p.DefaultValue != null) schema["default"] = p.DefaultValue;
                if (p.Type == "number" && Integers.Contains(p.Name)) { schema["minimum"] = 0; schema["maximum"] = p.Name == "color" || p.Name == "color_index" || p.Name == "ci" ? 256 : 10000; }
                if (p.Type == "array") {
                    schema["maxItems"] = 10000;
                    if (p.Name == "points" || p.Name == "boundary") schema["items"] = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "number" } };
                    if (p.Name == "handles" || p.Name == "targets") schema["items"] = new { type = "string", pattern = "^[0-9A-Fa-f]{1,16}$" };
                }
                if (p.Type == "string" && (p.Name == "handle" || p.Name == "source")) schema["pattern"] = "^[0-9A-Fa-f]{1,16}$";
                properties.Add(p.Name, schema);
            }
            var kind = Kind(tool.Name);
            return new {
                name = tool.Name, description = tool.Description, parameters,
                inputSchema = new { type = "object", properties, required = parameters.Where(p => p.Required).Select(p => p.Name).ToArray(), additionalProperties = false },
                execution = kind == "staged" ? "staged: draw.commit required" : kind,
                executionKind = kind,
                coordinates = kind == "staged" ? "XY offsets added to the explicit WCS origin in draw.commit; angles in degrees unless described otherwise" : "See tool parameters; geometry editing uses WCS, layout tools use paper space",
                verification = kind == "staged" ? "draw.commit then drawing.entity_detail using returned handles" : "Prefer result.Data for numeric checks; verify edits by reading the affected handles",
                limitations = tool.Name == "draw.tolerance" ? "Creates MText annotation, not a native geometric tolerance object" : tool.Name == "draw.spline" ? "Closed shape uses the source fit-point implementation; inspect actual geometry" : null
            };
        }
        internal static string Validate(IList<ToolParam> parameters, IDictionary<string, object> args)
        {
            parameters = parameters ?? new ToolParam[0];
            args = args ?? new Dictionary<string, object>();
            var expected = parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var key in args.Keys) if (!expected.ContainsKey(key)) return "알 수 없는 인자: " + key + ". describe로 정확한 이름을 확인하세요.";
            foreach (var p in parameters) {
                if (!args.TryGetValue(p.Name, out var value)) { if (p.Required) return "필수 인자 누락: " + p.Name; continue; }
                if (value == null) return p.Name + ": null 대신 선택 인자를 생략하세요.";
                var valid = p.Type == "number" ? IsNumber(value) : p.Type == "boolean" ? value is bool : p.Type == "string" ? value is string : p.Type == "array" ? value is object[] : p.Type == "object" && value is IDictionary<string, object>;
                if (!valid) return p.Name + ": JSON " + p.Type + " 형식이 필요합니다.";
                if (p.Type == "number" && Integers.Contains(p.Name)) {
                    var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (number != Math.Truncate(number) || number < 0 || number > 10000) return p.Name + ": 0~10000 범위의 정수 필요.";
                    if ((p.Name == "color" || p.Name == "color_index" || p.Name == "ci") && number > 256) return p.Name + ": ACI 0~256 범위 필요.";
                }
                if (value is object[] array) {
                    if (p.Name == "points" || p.Name == "boundary") foreach (var item in array) {
                        if (!(item is object[] point) || point.Length != 2 || !IsNumber(point[0]) || !IsNumber(point[1])) return p.Name + ": [[x,y],...] 형식의 유한한 숫자 쌍 필요.";
                    }
                    if (p.Name == "handles" || p.Name == "targets") {
                        var unique = new HashSet<ulong>();
                        foreach (var item in array) {
                            if (!IsHandle(item)) return p.Name + ": 16진 핸들 문자열 배열 필요.";
                            if (!unique.Add(ulong.Parse((string)item, NumberStyles.HexNumber, CultureInfo.InvariantCulture))) return p.Name + ": 중복 핸들입니다.";
                        }
                    }
                }
                if (p.Type == "string" && (p.Name == "handle" || p.Name == "source") && !IsHandle(value)) return p.Name + ": 16진 핸들 문자열 필요.";
            }
            try { ValidateNested(args, 0); } catch (ArgumentException ex) { return ex.Message; }
            return null;
        }
        private static bool IsHandle(object value) => value is string s && Regex.IsMatch(s, "\\A[0-9A-Fa-f]{1,16}\\z");
        private static bool IsNumber(object value)
        {
            if (!(value is double || value is float || value is decimal || value is int || value is long || value is short || value is byte || value is uint || value is ulong)) return false;
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
        private static void ValidateNested(object value, int depth)
        {
            if (depth > 16) throw new ArgumentException("JSON 중첩 한도 초과");
            if ((value is double || value is float) && !IsNumber(value)) throw new ArgumentException("유한한 숫자 필요");
            if (value is IDictionary<string, object> map) foreach (var item in map.Values) ValidateNested(item, depth + 1);
            else if (value is object[] array) {
                if (array.Length > 10000) throw new ArgumentException("배열 한도 10000 초과");
                foreach (var item in array) ValidateNested(item, depth + 1);
            }
        }
    }
}
