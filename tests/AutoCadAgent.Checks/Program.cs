using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using AutoCadAgent.Core;
var passed=0;
void Check(bool condition,string name) { if(!condition)throw new Exception("FAIL: "+name); Console.WriteLine("PASS "+name);passed++; }
Check(Provider.All.Length==3 && Provider.Find("gemini").Command=="gemini","three native CLI providers");
Check(Provider.WindowsQuote("C:\\drawing folder\\")=="\"C:\\drawing folder\\\\\"","Windows path trailing slash quoting");
Check(Provider.PsLiteral("한글 'file'")=="'한글 ''file'''","PowerShell literal escaping");
var rejected=false;try {Provider.Find("gpt").LaunchLine("codex.exe","x;calc.exe",false);}catch(ArgumentException){rejected=true;}
Check(rejected,"model command injection rejected");
Check(Provider.Find("gpt").LaunchLine("C:\\a b\\codex.exe","gpt-test",true).Contains("\"resume\" \"--last\""),"Codex resume args");
Check(Provider.Find("claude").LaunchLine("claude.exe","sonnet",true).Contains("\"--continue\""),"Claude resume args");
Check(Provider.Find("gemini").LaunchLine("gemini.exe","gemini-test",true).Contains("\"--resume\" \"latest\""),"Gemini resume args");
foreach(var p in Provider.All) Check(!p.LaunchLine(p.Command+".exe","",false).Contains("bypass"),p.Id+" preserves native approvals");
var pipe="CadAgentChecks-"+Guid.NewGuid().ToString("N");
using(var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
    var id=Guid.NewGuid().ToString("D");
    var work=Task.Run(async()=> {
        await server.WaitForConnectionAsync();
        using var reader=new StreamReader(server,Encoding.UTF8,leaveOpen:true);
        var request=JsonNode.Parse((await reader.ReadLineAsync())!)!;
        if(request["tool"]!.GetValue<string>()!="draw.circle")throw new Exception("request corruption");
        var response=Encoding.UTF8.GetBytes(new JsonObject {["id"]=id,["ok"]=true,["summary"]="한글 도면 응답 ✅"}.ToJsonString()+"\n");
        // Fragment even inside UTF-8 and JSON tokens.
        foreach(var b in response) await server.WriteAsync(new byte[]{b});
    });
    var reply=await BridgeClient.SendAsync(pipe,new JsonObject {["id"]=id,["method"]="tools.call",["tool"]="draw.circle",["args"]=new JsonObject {["r"]=5}});
    await work;
    Check(reply["summary"]!.GetValue<string>()=="한글 도면 응답 ✅","fragmented UTF-8 pipe response");
}
using(var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
    var work=Task.Run(async()=> {
        await server.WaitForConnectionAsync();
        using var reader=new StreamReader(server,Encoding.UTF8,leaveOpen:true);await reader.ReadLineAsync();
        await server.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"wrong\",\"ok\":true}\n"));
    });
    rejected=false;try{await BridgeClient.SendAsync(pipe,new JsonObject {["method"]="status"});}catch(InvalidDataException){rejected=true;}
    await work;Check(rejected,"mismatched response rejected");
}
using(var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
    using var cancel=new CancellationTokenSource(150);
    rejected=false;try{await BridgeClient.SendAsync(pipe,new JsonObject {["method"]="status"},cancel.Token);}catch(OperationCanceledException){rejected=true;}
    Check(rejected,"blocked pipe cancellation is bounded");
}
var env=Provider.Find("gpt").EnvironmentFor("pipe","doc","client");
Check(env["CADAGENT_DOCUMENT"]=="doc" && env["CADAGENT_CLIENT"]=="client","document/session pin propagation");
if(OperatingSystem.IsWindows()) {
    Check(ConPty.Available,"Windows ConPTY available");
    using var terminal=new ConPty();
    var output=new MemoryStream();var done=new TaskCompletionSource<bool>();
    terminal.OnOutput+=b=>{ lock(output) {output.Write(b);if(Encoding.UTF8.GetString(output.ToArray()).Contains("CAD_PTY_OK"))done.TrySetResult(true);} };
    var cmd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
    terminal.Start(Provider.WindowsQuote(cmd)+" /d /c echo CAD_PTY_OK",Environment.CurrentDirectory,80,24);
    await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(true,"native terminal real process output");
}
await InterfaceChecks.Run(Check);
Console.WriteLine($"TOTAL {passed} PASSED");
if(args.Length==2 && args[0]=="--providers-root") await ProviderChecks.Run(args[1]);
