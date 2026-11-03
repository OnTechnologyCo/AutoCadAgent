using System.Text;
using System.Text.RegularExpressions;

namespace AutoCadAgent.Core;
public sealed record Provider(string Id, string Label, string Command, string NpmEntry)
{
    public static readonly Provider[] All = [
        new("gpt", "GPT · Codex", "codex", Path.Combine("@openai","codex","bin","codex.js")),
        new("claude", "Claude Code", "claude", Path.Combine("@anthropic-ai","claude-code","cli.js")),
        new("gemini", "Gemini CLI", "gemini", Path.Combine("@google","gemini-cli","dist","index.js"))
    ];
    public static Provider Find(string id) => All.SingleOrDefault(x=>x.Id==id) ?? throw new ArgumentException("알 수 없는 모델 제공자: "+id);
    public string? Locate(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            return File.Exists(full) ? full : throw new FileNotFoundException("설정한 CLI를 찾지 못했습니다.",full);
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var candidates = new List<string>();
        if (Id == "gpt") candidates.Add(Path.Combine(local,"Programs","OpenAI","Codex","bin","codex.exe"));
        // Project-local provider installs do not modify the user's global npm setup.
        for(DirectoryInfo? dir=new DirectoryInfo(AppContext.BaseDirectory); dir!=null; dir=dir.Parent)
            candidates.Add(Path.Combine(dir.FullName,".tools","node_modules",".bin",Command+".cmd"));
        if(Id=="claude") candidates.Add(Path.Combine(npm,"node_modules","@anthropic-ai","claude-code","bin","claude.exe"));
        candidates.AddRange(new[] { Path.Combine(user,".local","bin"), Path.Combine(user,".codex","bin"), Path.Combine(local,"Programs",Command), npm }
            .Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
            .Where(x=>!string.IsNullOrWhiteSpace(x)).SelectMany(x=>new[]{".exe",".cmd",".bat",".ps1"}.Select(ext=>Path.Combine(x.Trim('"'),Command+ext))));
        return candidates.FirstOrDefault(File.Exists);
    }
    public string LaunchLine(string path, string model, bool resume)
    {
        if (!string.IsNullOrWhiteSpace(model) && !Regex.IsMatch(model,@"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,159}\z")) throw new ArgumentException("모델명에는 영문, 숫자, 점, 밑줄, :, /, - 만 사용할 수 있습니다.");
        var args = new List<string>();
        if (resume) {
            if (Id=="gpt") args.AddRange(["resume","--last"]);
            else if (Id=="claude") args.Add("--continue");
            else args.AddRange(["--resume","latest"]);
        }
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model",model.Trim()]);
        // Preserve provider's normal permission/sandbox prompts; never inject bypass flags.
        var ext=Path.GetExtension(path).ToLowerInvariant();
        if (ext==".exe") return WindowsQuote(path)+" "+string.Join(" ",args.Select(WindowsQuote));
        if (ext is not (".cmd" or ".bat" or ".ps1")) throw new ArgumentException("CLI 경로는 exe/cmd/bat/ps1이어야 합니다.");
        // Prefer node entry to bypass cmd.exe's separate escaping rules (AgentCLIHub design).
        var js=Path.Combine(Path.GetDirectoryName(path)!,"node_modules",NpmEntry);
        var packageName = Id switch { "gpt" => Path.Combine("@openai","codex"), "claude" => Path.Combine("@anthropic-ai","claude-code"), _ => Path.Combine("@google","gemini-cli") };
        foreach(var package in new[]{Path.Combine(Path.GetDirectoryName(path)!,"node_modules",packageName),Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,"..",packageName))}) {
            var manifest=Path.Combine(package,"package.json");
            if(!File.Exists(manifest)) continue;
            try {
                var meta=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest));
                var entry=meta?["bin"] is System.Text.Json.Nodes.JsonObject bins ? bins[Command]?.GetValue<string>() : meta?["bin"]?.GetValue<string>();
                if(entry!=null) {
                    var actual=Path.GetFullPath(Path.Combine(package,entry));
                    if(File.Exists(actual) && actual.StartsWith(Path.GetFullPath(package)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) {
                        if(actual.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) return WindowsQuote(actual)+" "+string.Join(" ",args.Select(WindowsQuote));
                        if(actual.EndsWith(".js",StringComparison.OrdinalIgnoreCase)) {js=actual; break;}
                    }
                }
            } catch(System.Text.Json.JsonException) { }
        }
        var node=FindNode(Path.GetDirectoryName(path)!);
        if(ext!=".ps1" && File.Exists(js) && node!=null) return WindowsQuote(node)+" "+WindowsQuote(js)+" "+string.Join(" ",args.Select(WindowsQuote));
        // Encoded PowerShell with single-quoted literals; model text is never executable code.
        var script="& "+PsLiteral(path)+" "+string.Join(" ",args.Select(PsLiteral))+"; exit $LASTEXITCODE";
        var shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        return WindowsQuote(shell)+" -NoLogo -NoProfile -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }
    private static string? FindNode(string folder) => new[]{Path.Combine(folder,"node.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe")}
        .Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Where(x=>x.Length>0).Select(x=>Path.Combine(x.Trim('"'),"node.exe"))).FirstOrDefault(File.Exists);
    public static string PsLiteral(string value) => "'"+value.Replace("'","''")+"'";
    public static string WindowsQuote(string value)
    {
        var b=new StringBuilder("\""); var slashes=0;
        foreach(var c in value) {
            if(c=='\\') { slashes++; continue; }
            if(c=='"') { b.Append('\\',slashes*2+1).Append(c); slashes=0; continue; }
            b.Append('\\',slashes).Append(c); slashes=0;
        }
        return b.Append('\\',slashes*2).Append('"').ToString();
    }
    public Dictionary<string,string?> EnvironmentFor(string pipe,string document,string client)
    {
        var env=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase) {
            ["TERM"]="xterm-256color",["COLORTERM"]="truecolor",["TERM_PROGRAM"]="AutoCadAgent",["NO_COLOR"]=null,
            ["CADAGENT_PIPE"]=pipe,["CADAGENT_DOCUMENT"]=document,["CADAGENT_CLIENT"]=client
        };
        foreach(System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables()) {
            var key=item.Key.ToString()!;
            if((Id=="claude" && (key=="CLAUDECODE" || key.StartsWith("CLAUDE_CODE_ENTRYPOINT",StringComparison.OrdinalIgnoreCase))) ||
               (Id=="gpt" && key.StartsWith("CODEX_SANDBOX",StringComparison.OrdinalIgnoreCase))) env[key]=null;
        }
        return env;
    }
}
