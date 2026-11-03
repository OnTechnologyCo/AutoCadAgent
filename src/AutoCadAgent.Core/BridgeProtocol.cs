using System.Text.Json.Nodes;
namespace AutoCadAgent.Core;
public static class BridgeProtocol
{
    public const int Version = 2;
    public static void ValidateStatus(JsonObject status, string? expectedDocument = null)
    {
        if (status["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException(status.ToJsonString());
        if (status["protocolVersion"]?.GetValue<int>() != Version) throw new InvalidOperationException("CAD 브리지 버전이 맞지 않습니다. 최신 DLL을 NETLOAD하고 AutoCAD를 재시작하세요.");
        var document = status["document"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(document)) throw new InvalidOperationException("AutoCAD에서 작업 도면을 열고 TUI에서 연결하세요.");
        if (!string.IsNullOrWhiteSpace(expectedDocument) && document != expectedDocument) throw new InvalidOperationException("대상 도면이 바뀌었습니다. TUI에서 다시 연결하세요.");
        var capabilities = status["capabilities"]?.AsArray().Select(c => c?.GetValue<string>()).ToHashSet();
        if (capabilities == null || !capabilities.Contains("input-schema") || !capabilities.Contains("structured-geometry")) throw new InvalidOperationException("CAD 브리지가 현재 도구 계약을 지원하지 않습니다. 최신 DLL을 로드하세요.");
    }
}
