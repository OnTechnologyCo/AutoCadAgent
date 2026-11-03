using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCadAgent.Core;
Console.OutputEncoding = new UTF8Encoding(false);
var argv = args.ToList();
string? Option(string name) { var i=argv.IndexOf(name); if(i<0) return null; if(i+1>=argv.Count) throw new ArgumentException(name+" 값이 필요합니다."); var v=argv[i+1]; argv.RemoveRange(i,2); return v; }
string? requestId=null;
try {
    var pipe=Option("--pipe")??Environment.GetEnvironmentVariable("CADAGENT_PIPE");
    var doc=Option("--document")??Environment.GetEnvironmentVariable("CADAGENT_DOCUMENT");
    var client=Option("--client")??Environment.GetEnvironmentVariable("CADAGENT_CLIENT");
    requestId=Option("--id")??Guid.NewGuid().ToString("D");
    if(!Guid.TryParse(requestId,out var parsedRequestId)) throw new ArgumentException("--id는 UUID 형식이어야 합니다.");
    requestId=parsedRequestId.ToString("D");
    var cmd=argv.FirstOrDefault()??"help";
    if(cmd is "help" or "--help" or "-h") {
        Console.WriteLine("""
        cad-agent discover
        cad-agent [--pipe NAME] status
        cad-agent [--pipe NAME] doctor
        cad-agent [--pipe NAME] describe TOOL
        cad-agent [--pipe NAME] tools [PREFIX]
        cad-agent [--pipe NAME --document TOKEN --client UUID] call TOOL --file args.json
        cad-agent [--pipe NAME --document TOKEN --client UUID] call TOOL '{"x":0,"y":0}'
        cad-agent [--pipe NAME] request REQUEST_UUID
        환경: CADAGENT_PIPE / CADAGENT_DOCUMENT / CADAGENT_CLIENT (TUI 자동 설정)
        --id UUID: 동일 요청 복구용 ID. 시간 초과 뒤에는 request로 조회하세요.
        staged 도형은 draw.commit({"x":0,"y":0}) 성공 후 실제 도면에 생성됩니다.
        """); return 0;
    }
    if(cmd=="discover") { Console.WriteLine(JsonSerializer.Serialize(BridgeClient.Discover())); return 0; }
    if(string.IsNullOrWhiteSpace(pipe)) {
        var bridges=BridgeClient.Discover();
        if(bridges.Count!=1) throw new InvalidOperationException("CAD 브리지를 선택하세요. AutoCAD에서 DLL NETLOAD 후 discover 또는 --pipe 사용.");
        pipe=bridges[0].Pipe;
    }
    var req=new JsonObject { ["id"]=requestId };
    switch(cmd) {
        case "doctor":
        case "status": req["method"]="status"; break;
        case "describe": req["method"]="tools.describe"; req["tool"]=argv.ElementAtOrDefault(1)??throw new ArgumentException("도구 이름 필요"); break;
        case "tools": req["method"]="tools.list"; req["filter"]=argv.ElementAtOrDefault(1)??""; break;
        case "request": req["method"]="request.status"; req["requestId"]=argv.ElementAtOrDefault(1)??throw new ArgumentException("조회할 UUID 필요"); break;
        case "call":
            if(string.IsNullOrWhiteSpace(doc) || !Guid.TryParse(client,out _)) throw new ArgumentException("도면 토큰과 세션 UUID가 필요합니다. TUI에서 실행하거나 --document/--client를 지정하세요.");
            var file=Option("--file");
            var tool=argv.ElementAtOrDefault(1)??throw new ArgumentException("도구 이름 필요");
            var raw=file!=null?File.ReadAllText(file):argv.ElementAtOrDefault(2)??"{}";
            req["method"]="tools.call"; req["tool"]=tool; req["document"]=doc; req["client"]=client;
            req["args"]=JsonNode.Parse(raw) as JsonObject??throw new ArgumentException("도구 인자는 JSON 객체여야 합니다.");
            break;
        default: throw new ArgumentException("알 수 없는 명령: "+cmd);
    }
    var result=await BridgeClient.SendAsync(pipe!,req);
    if(cmd=="doctor") { BridgeProtocol.ValidateStatus(result,doc); result["diagnosis"]="ready"; result["next"]="describe TOOL → call TOOL --file args.json → inspect result.Data"; }
    Console.WriteLine(result.ToJsonString(new JsonSerializerOptions {WriteIndented=true}));
    return result["ok"]?.GetValue<bool>()==true?0:1;
} catch(Exception ex) {
    Console.WriteLine(JsonSerializer.Serialize(new { ok=false,id=requestId,error=ex.Message, recovery="변경 요청의 응답을 잃은 경우 동일 ID를 request 명령으로 조회하세요. 자동 재실행 금지." }));
    return 1;
}
