using System.Text;
using AutoCadAgent.Core;
static class ProviderChecks
{
    public static async Task Run(string root)
    {
        var home=Environment.GetEnvironmentVariable("CADAGENT_TEST_USER")??Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths=new Dictionary<string,string> {
            ["gpt"]=Path.Combine(home,"AppData","Local","Programs","OpenAI","Codex","bin","codex.exe"),
            ["claude"]=Path.Combine(home,"AppData","Roaming","npm","node_modules","@anthropic-ai","claude-code","bin","claude.exe"),
            ["gemini"]=Path.Combine(root,".tools","node_modules",".bin","gemini.cmd")
        };
        foreach(var provider in Provider.All) {
            var path=provider.Locate(paths[provider.Id])??throw new Exception("CLI missing: "+provider.Label);
            var line=provider.LaunchLine(path,"",false);
            if(line.Contains("EncodedCommand")) throw new Exception("Expected direct native/node launch for installed provider: "+provider.Id);
            using var pty=new ConPty();
            using var output=new MemoryStream();
            var done=new TaskCompletionSource<bool>();
            pty.OnOutput+=b=> {lock(output){output.Write(b);var text=Encoding.UTF8.GetString(output.ToArray());if(text.Contains("--help"))done.TrySetResult(true);}};
            pty.Start(line+" --help",root,100,32,provider.EnvironmentFor("","",Guid.NewGuid().ToString("D")));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("PASS actual "+provider.Label+" CLI via ConPTY");
        }
    }
}
