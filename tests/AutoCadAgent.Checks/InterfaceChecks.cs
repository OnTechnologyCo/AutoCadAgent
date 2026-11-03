using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using AutoCadAgent.Core;
internal static class InterfaceChecks
{
    internal static async Task Run(Action<bool,string> check)
    {
        JsonObject Status() => new() { ["ok"]=true,["protocolVersion"]=2,["document"]="document-1",["capabilities"]=new JsonArray("input-schema","structured-geometry") };
        BridgeProtocol.ValidateStatus(Status(),"document-1");check(true,"compatible CAD contract accepted");
        var wrong=Status();wrong["protocolVersion"]=1;var refused=false;
        try {BridgeProtocol.ValidateStatus(wrong);}catch(InvalidOperationException){refused=true;}
        check(refused,"old NETLOAD contract rejected before use");
        refused=false;try {BridgeProtocol.ValidateStatus(Status(),"document-2");}catch(InvalidOperationException){refused=true;}
        check(refused,"changed drawing pin rejected before CLI launch");
        var dir=Path.Combine(Path.GetTempPath(),"CadAgent brief "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir,"AGENTS.md"),"User rule: preserve this line.\n");
            var helper="C:\\설계 폴더\\agent's tools\\cad-agent.exe";
            AgentInstructions.Write(dir,helper);AgentInstructions.Write(dir,helper);
            var agents=File.ReadAllText(Path.Combine(dir,"AGENTS.md"));
            check(agents.Contains("User rule: preserve this line.") && agents.Split("<!-- AutoCadAgent bridge -->").Length==2,"brief update preserves user instructions without duplication");
            var brief=File.ReadAllText(Path.Combine(dir,"CAD_AGENT.md"));
            check(brief.Contains(Provider.PsLiteral(helper)) && brief.Contains("result.Data") && brief.Contains("doctor") && brief.Contains("inputSchema"),"LLM brief has quoted CLI path, schema and verification workflow");
            check(new[]{"AGENTS.md","CLAUDE.md","GEMINI.md"}.All(p=>File.ReadAllText(Path.Combine(dir,p)).Contains("CAD_AGENT.md")),"all three provider context files reference current CAD contract");
        } finally {Directory.Delete(dir,true);}
        foreach(var valid in new[]{false,true}) {
            var pipe="CadAgentStatusCheck-"+Guid.NewGuid().ToString("N");
            var target=Guid.NewGuid().ToString("D");
            using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            var work=Task.Run(async()=>{
                await server.WaitForConnectionAsync();using var reader=new StreamReader(server,Encoding.UTF8,leaveOpen:true);await reader.ReadLineAsync();
                var payload=new JsonObject {["id"]=valid?target:Guid.NewGuid().ToString("D"),["ok"]=true};
                await server.WriteAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()+"\n"));
            });
            refused=false;try {await BridgeClient.SendAsync(pipe,new JsonObject {["method"]="request.status",["requestId"]=target});}catch(InvalidDataException){refused=true;}
            await work;check(refused!=valid,valid?"completed request lookup accepts target ID":"request lookup rejects unrelated response ID");
        }
    }
}
