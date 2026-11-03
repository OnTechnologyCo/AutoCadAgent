using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoCadAgent.Core;
public sealed record BridgeEndpoint(string Pipe, int Pid, string Version);
public static class BridgeClient
{
    public static IReadOnlyList<BridgeEndpoint> Discover()
    {
        var list = new List<BridgeEndpoint>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadAgent", "bridges");
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (new FileInfo(file).Length > 8192) continue;
                var j = JsonNode.Parse(File.ReadAllText(file))!;
                var pid = j["pid"]!.GetValue<int>();
                using var process = Process.GetProcessById(pid);
                if (process.HasExited || !(process.ProcessName.Equals("acad", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("accoreconsole", StringComparison.OrdinalIgnoreCase))) continue;
                var pipe = j["pipe"]!.GetValue<string>();
                if (!pipe.StartsWith($"AutoCadAgent-{pid}-", StringComparison.Ordinal)) continue;
                list.Add(new(pipe, pid, j["version"]?.GetValue<string>() ?? ""));
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
        return list;
    }
    public static async Task<JsonObject> SendAsync(string pipeName, JsonObject request, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.IndexOfAny(['\\','/']) >= 0) throw new ArgumentException("유효한 로컬 pipe 이름을 지정하세요.");
        request["id"] ??= Guid.NewGuid().ToString("D");
        if (!Guid.TryParse(request["id"]?.GetValue<string>(), out var parsedId)) throw new ArgumentException("UUID 요청 ID 필요");
        request["id"] = parsedId.ToString("D");
        if (request["method"]?.GetValue<string>() == "request.status" && Guid.TryParse(request["requestId"]?.GetValue<string>(), out var targetId)) request["requestId"] = targetId.ToString("D");
        var payload = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        if (payload.Length > 1024*1024) throw new ArgumentException("요청은 1MiB 이하여야 합니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(32));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await pipe.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("브리지 연결이 종료되었습니다. 요청 ID로 결과를 확인하세요: " + request["id"]);
            var end = Array.IndexOf(buffer, (byte)10, 0, count);
            var n = end < 0 ? count : end;
            if (output.Length+n > 1024*1024) throw new InvalidDataException("응답이 1MiB를 초과했습니다. 조회 범위를 줄이세요.");
            output.Write(buffer,0,n);
            if (end < 0) continue;
            var result = JsonNode.Parse(output.ToArray()) as JsonObject ?? throw new InvalidDataException("JSON 객체 응답 필요");
            var responseId = result["id"]?.GetValue<string>();
            if (request["method"]?.GetValue<string>() == "request.status") {
                var target = request["requestId"]?.GetValue<string>();
                if (responseId != request["id"]?.GetValue<string>() && responseId != target) throw new InvalidDataException("조회 응답 ID 불일치");
                if (result["requestId"] != null && result["requestId"]?.GetValue<string>() != target) throw new InvalidDataException("조회 대상 ID 불일치");
            } else if (responseId != request["id"]?.GetValue<string>()) throw new InvalidDataException("응답 ID 불일치");
            return result;
        }
    }
}
