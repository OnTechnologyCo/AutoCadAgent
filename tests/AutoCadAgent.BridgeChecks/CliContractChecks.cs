using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using AutoCadAgent.Cad;
internal static class CliContractChecks
{
    internal static void Run(string executable, Action<bool,string> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"CAD interface "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        using(var runtime=new BridgeRuntime("AutoCadAgent-contract-"+Guid.NewGuid().ToString("N"))) {
            runtime.Listen();
            var tool=new ContractChecks.CircleTool();
            object Dispatch(BridgeRequest request) {
                if(request.Method=="status")return new {id=request.Id,ok=true,protocolVersion=2,document="fixture-doc",capabilities=new[]{"input-schema","structured-geometry"}};
                if(request.Method=="tools.describe")return new {id=request.Id,ok=true,tool=ToolContract.Describe(tool)};
                var error=ToolContract.Validate(tool.Parameters,request.Args);
                if(error!=null)return new {id=request.Id,ok=false,error=new {code="invalid_arguments",message=error}};
                return new {id=request.Id,ok=true,result=new {Success=true,Data=new {cx=request.Args["cx"],cy=request.Args["cy"],r=request.Args["r"]}}};
            }
            Dictionary<string,object> Call(string arguments, int expectedExit) {
                using(var process=new Process {StartInfo=new ProcessStartInfo(executable,"--pipe "+runtime.PipeName+" "+arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8}}) {
                    process.Start();var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();var clock=Stopwatch.StartNew();
                    while(!process.HasExited) {runtime.ProcessOne(true,Dispatch);if(clock.ElapsedMilliseconds>10000){process.Kill();throw new TimeoutException("CLI contract check");}Thread.Sleep(5);}
                    if(process.ExitCode!=expectedExit)throw new Exception("CLI exit mismatch: "+stdout.Result+stderr.Result);
                    return (Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(stdout.Result);
                }
            }
            try {
                check((string)Call("doctor",0)["diagnosis"]=="ready","real CLI doctor validates bridge capabilities");
                var descriptor=(Dictionary<string,object>)Call("describe draw.circle",0)["tool"];
                check(descriptor.ContainsKey("inputSchema") && (string)descriptor["executionKind"]=="staged","real CLI returns machine-readable exact tool schema");
                var file=Path.Combine(directory,"한국어 도형.json");File.WriteAllText(file,"{\"cx\":1.123456789,\"cy\":-2.987654321,\"r\":5}",new UTF8Encoding(false));
                var command="--document fixture-doc --client "+Guid.NewGuid()+" call draw.circle --file \""+file+"\"";
                var response=Call(command,0);var data=(Dictionary<string,object>)((Dictionary<string,object>)response["result"])["Data"];
                check(Math.Abs(Convert.ToDouble(data["cx"])-1.123456789)<1e-12 && Math.Abs(Convert.ToDouble(data["cy"])+2.987654321)<1e-12,"CLI file to net48 result preserves exact fractional geometry and Korean path");
                File.WriteAllText(file,"{\"cx\":0,\"cy\":0,\"r\":\"five\"}",new UTF8Encoding(false));
                check((string)((Dictionary<string,object>)Call(command,1)["error"])["code"]=="invalid_arguments","LLM argument error is returned as structured failure and nonzero CLI exit");
            } finally {Directory.Delete(directory,true);}
        }
    }
}
