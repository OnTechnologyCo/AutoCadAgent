using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using AutoCadAgent.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace AutoCadAgent.Tui;
public sealed class MainWindow : Window
{
    private sealed class Preferences
    {
        public string Workspace { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"AutoCadAgent");
        public Dictionary<string,string> Paths { get; set; } = new();
        public Dictionary<string,string> Models { get; set; } = new();
        public Dictionary<string,string> Clients { get; set; } = new();
    }
    private sealed class Session
    {
        public required string Id;
        public required ConPty Terminal;
        public BlockingCollection<byte[]> Output = new(256);
        public CancellationTokenSource Stop = new();
    }
    private readonly WebView2 _web=new();
    private readonly Dictionary<string,Session> _sessions=new();
    private readonly HashSet<string> _starting=new();
    private readonly DispatcherTimer _flush=new() { Interval=TimeSpan.FromMilliseconds(40) };
    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AutoCadAgent");
    private static string SettingsPath => Path.Combine(DataDir,"settings.json");
    private Preferences _prefs;
    private string? _pipe, _document;
    private string _documentName="CAD 미연결";
    private bool _ready, _closing;
    public MainWindow(string[] args)
    {
        Title="AutoCAD Agent · GPT / Claude / Gemini"; Width=1240; Height=850; MinWidth=860; MinHeight=580;
        Background=System.Windows.Media.Brushes.Black; Content=_web;
        try { _prefs=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath))??new(); } catch { _prefs=new(); }
        var index=Array.IndexOf(args,"--workspace"); if(index>=0 && index+1<args.Length) _prefs.Workspace=Path.GetFullPath(args[index+1]);
        Loaded+=async (_,_)=>await Initialize();
        _flush.Tick+=(_,_)=>Flush();
        Closed+=(_,_)=> { _closing=true; _flush.Stop(); foreach(var session in _sessions.Values.ToArray()) StopSession(session.Id); _web.Dispose(); };
    }
    private async Task Initialize()
    {
        try {
            Directory.CreateDirectory(DataDir);
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(DataDir,"webview"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping("cadagent.local",Path.Combine(AppContext.BaseDirectory,"web"),CoreWebView2HostResourceAccessKind.DenyCors);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled=false;
            _web.CoreWebView2.NavigationStarting+=(_,e)=> { if(!e.Uri.StartsWith("https://cadagent.local/",StringComparison.Ordinal)) e.Cancel=true; };
            _web.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
            _web.CoreWebView2.WebMessageReceived+=async (_,e)=> {
                if(!e.Source.StartsWith("https://cadagent.local/",StringComparison.Ordinal)) return;
                try { await Handle(JsonNode.Parse(e.WebMessageAsJson)!.AsObject()); }
                catch(Exception ex) { Send(new {t="notice",error=true,text=ex.Message}); }
            };
            _web.Source=new Uri("https://cadagent.local/index.html");
            _flush.Start();
        } catch(Exception ex) { MessageBox.Show(this,"TUI 초기화 실패: "+ex.Message,"AutoCadAgent"); }
    }
    private void Send(object value) { if(!_closing && _ready) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value)); }
    private void Save() {
        Directory.CreateDirectory(DataDir);
        var temp=SettingsPath+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(_prefs,new JsonSerializerOptions {WriteIndented=true}),new UTF8Encoding(false));
        File.Move(temp,SettingsPath,true);
    }
    private void SendSettings() => Send(new {t="settings",workspace=_prefs.Workspace,providers=Provider.All.Select(p=>new {id=p.Id,label=p.Label,path=_prefs.Paths.GetValueOrDefault(p.Id,""),model=_prefs.Models.GetValueOrDefault(p.Id,"")})});
    private async Task Handle(JsonObject m)
    {
        var type=m["t"]?.GetValue<string>();
        var id=m["id"]?.GetValue<string>()??"";
        switch(type) {
            case "ready": _ready=true; SendSettings(); SendBridges(); break;
            case "refresh": SendBridges(); break;
            case "connect":
                var pipe=m["pipe"]?.GetValue<string>()??throw new ArgumentException("AutoCAD를 선택하세요.");
                if(!BridgeClient.Discover().Any(e=>e.Pipe==pipe)) throw new InvalidOperationException("실행 중인 CAD 브리지가 아닙니다.");
                var status=await BridgeClient.SendAsync(pipe,new JsonObject {["method"]="status"});
                BridgeProtocol.ValidateStatus(status);
                _pipe=pipe; _document=status["document"]?.GetValue<string>(); _documentName=status["name"]?.GetValue<string>()??"열린 도면 없음";
                Send(new {t="connected",name=_documentName,units=status["units"],tools=status["tools"],document=_document});
                Send(new {t="notice",text="도면 연결 완료. 실행 중인 터미널은 기존 도면을 유지하므로 대상 변경 시 다시 열어주세요."}); break;
            case "browseWorkspace":
                var folder=new OpenFolderDialog {Title="CAD 설계 작업 폴더",InitialDirectory=Directory.Exists(_prefs.Workspace)?_prefs.Workspace:""};
                if(folder.ShowDialog(this)==true) { _prefs.Workspace=folder.FolderName; Save(); SendSettings(); } break;
            case "browseCli":
                Provider.Find(id);
                var picker=new OpenFileDialog {Title="CLI 실행 파일",Filter="CLI|*.exe;*.cmd;*.bat;*.ps1|All|*.*"};
                if(picker.ShowDialog(this)==true) { _prefs.Paths[id]=picker.FileName; Save(); SendSettings(); } break;
            case "start": await StartSession(m,id); break;
            case "input": if(_sessions.TryGetValue(id,out var target)) target.Terminal.Write(m["data"]?.GetValue<string>()??""); break;
            case "resize": if(_sessions.TryGetValue(id,out var resize)) resize.Terminal.Resize((short)Math.Clamp(m["cols"]?.GetValue<int>()??100,2,500),(short)Math.Clamp(m["rows"]?.GetValue<int>()??30,2,250)); break;
            case "interrupt": if(_sessions.TryGetValue(id,out var interrupt)) interrupt.Terminal.Write("\x03"); break;
            case "stop": StopSession(id); break;
            case "clear": Send(new {t="clear",id}); break;
        }
    }
    private void SendBridges() => Send(new {t="bridges",items=BridgeClient.Discover().Select(b=>new {pipe=b.Pipe,pid=b.Pid})});
    private void StopSession(string id)
    {
        if(!_sessions.Remove(id,out var session)) return;
        session.Stop.Cancel();
        _=Task.Run(()=> { session.Terminal.Dispose(); session.Stop.Dispose(); session.Output.Dispose(); });
        Send(new {t="stopped",id});
    }
    private void Flush()
    {
        if(!_ready) return;
        foreach(var session in _sessions.Values) {
            using var bytes=new MemoryStream();
            while(bytes.Length<65536 && session.Output.TryTake(out var next)) bytes.Write(next);
            if(bytes.Length>0) Send(new {t="output",id=session.Id,data=Convert.ToBase64String(bytes.ToArray())});
        }
    }
    private async Task StartSession(JsonObject m,string id)
    {
        if(!_starting.Add(id)) throw new InvalidOperationException("CLI를 시작하는 중입니다.");
        var pipe=_pipe; var document=_document; var documentName=_documentName;
        try {
                if(_sessions.ContainsKey(id)) throw new InvalidOperationException("이미 실행 중인 제공자입니다. 중지한 뒤 다시 여세요.");
                var provider=Provider.Find(id);
                var path=m["path"]?.GetValue<string>()??""; var model=m["model"]?.GetValue<string>()??"";
                var work=Path.GetFullPath(m["workspace"]?.GetValue<string>()??_prefs.Workspace);
                if(!string.IsNullOrWhiteSpace(pipe) && !string.IsNullOrWhiteSpace(document)) {
                    var check=await BridgeClient.SendAsync(pipe,new JsonObject {["method"]="status"});
                    BridgeProtocol.ValidateStatus(check,document);
                }
                var cli=provider.Locate(path)??throw new FileNotFoundException(provider.Label+" CLI를 찾지 못했습니다. 설치 후 실행 파일을 지정하세요.");
                var resume=m["resume"]?.GetValue<bool>()??false;
                var line=provider.LaunchLine(cli,model,resume);
                Directory.CreateDirectory(work);
                var helper=Path.Combine(AppContext.BaseDirectory,"agent","cad-agent.exe");
                if(!File.Exists(helper)) throw new FileNotFoundException("cad-agent.exe가 없습니다. scripts/build.ps1로 빌드하세요.",helper);
                var clientKey=id+"|"+work.ToUpperInvariant();
                var client=resume?_prefs.Clients.GetValueOrDefault(clientKey,Guid.NewGuid().ToString("D")):Guid.NewGuid().ToString("D");
                AgentInstructions.Write(work,helper);
                _prefs.Paths[id]=path; _prefs.Models[id]=model; _prefs.Workspace=work; _prefs.Clients[clientKey]=client; Save();
                var session=new Session {Id=id,Terminal=new ConPty()};
                session.Terminal.OnOutput+=bytes=> { try {session.Output.Add(bytes,session.Stop.Token);} catch(OperationCanceledException){} catch(InvalidOperationException){} };
                session.Terminal.OnExit+=code=>Dispatcher.BeginInvoke(()=>Send(new {t="exit",id,code}));
                _sessions.Add(id,session);
                Send(new {t="opened",id,label=provider.Label,document=documentName});
                try { session.Terminal.Start(line,work,100,30,provider.EnvironmentFor(pipe??"",document??"",client)); }
                catch {StopSession(id);throw;}

        } finally { _starting.Remove(id); }
    }
}
