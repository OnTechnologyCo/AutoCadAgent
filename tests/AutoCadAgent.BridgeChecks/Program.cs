using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AutoCadAgent.Cad;

internal static class Program
{
    private static int passed;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
    private static Dictionary<string, object> Decode(string json) => (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
    private static string Code(string json) { var d = Decode(json); return d.ContainsKey("error") ? (string)((Dictionary<string, object>)d["error"])["code"] : null; }
    private static string Request(string method = "status", string id = null, string extra = "") => "{\"id\":\"" + (id ?? Guid.NewGuid().ToString()) + "\",\"method\":\"" + method + "\"" + extra + "}";
    private static BridgeRuntime Runtime(int waitMs = 25000) => new BridgeRuntime("AutoCadAgent-check-" + Guid.NewGuid().ToString("N"), waitMs);
    private static void Until(Func<bool> test) { var clock = Stopwatch.StartNew(); while (!test()) { if (clock.ElapsedMilliseconds > 8000) throw new TimeoutException("test wait"); Thread.Sleep(5); } }
    private static string Complete(BridgeRuntime runtime, string json, Func<BridgeRequest, object> run = null)
    {
        var task = runtime.Handle(json);
        runtime.ProcessOne(true, run ?? (r => new { id = r.Id, ok = true }));
        return task.GetAwaiter().GetResult();
    }
    private static async Task<string> Exchange(string pipeName, string payload, int fragment = 4096)
    {
        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
            using (var deadline = new CancellationTokenSource(8000)) {
                await pipe.ConnectAsync(3000, deadline.Token);
                using (deadline.Token.Register(() => pipe.Dispose())) {
                    var bytes = Encoding.UTF8.GetBytes(payload + "\n");
                    for (var i = 0; i < bytes.Length; i += fragment) await pipe.WriteAsync(bytes, i, Math.Min(fragment, bytes.Length - i), deadline.Token);
                    await pipe.FlushAsync(deadline.Token);
                    return await BridgeRuntime.ReadLine(pipe);
                }
            }
        }
    }
    private static int Main(string[] args)
    {
        try { Run(); ContractChecks.Run(Check); if (args.Length == 2 && args[0] == "--cli") { CheckCli(args[1]); CliContractChecks.Run(args[1], Check); } Console.WriteLine("Bridge checks passed: " + passed + " (real net48 transport; CAD dispatcher is a test delegate, not AutoCAD)"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void CheckCli(string path)
    {
        using (var rt = Runtime()) {
            rt.Listen();
            using (var process = new Process { StartInfo = new ProcessStartInfo(path, "--pipe " + rt.PipeName + " status") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, CreateNoWindow = true } }) {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                Until(() => rt.PendingCount == 1 || process.HasExited);
                rt.ProcessOne(true, r => new { id = r.Id, ok = true, name = "한국어 도면.dwg", document = "test-only-document", tools = 122, protocolVersion = 2, capabilities = new[]{"input-schema","structured-geometry"} });
                if (!process.WaitForExit(8000)) { process.Kill(); throw new TimeoutException("CLI test timed out"); }
                Check(process.ExitCode == 0 && (string)Decode(output.Result)["name"] == "한국어 도면.dwg", "real net10 cad-agent CLI interoperates with net48 server");
            }
        }
    }
    private static void Run()
    {
        using (var rt = Runtime()) {
            Check(Code(rt.Handle("not json").Result) == "invalid_request", "malformed JSON rejected");
            Check(Code(rt.Handle("{\"id\":\"bad\",\"method\":\"status\"}").Result) == "invalid_request", "invalid UUID rejected");
            Check(Code(rt.Handle(Request("tools.call", extra: ",\"tool\":\"draw.circle\",\"args\":[]")).Result)=="invalid_request", "non-object tool arguments rejected at envelope");
            Check(Code(rt.Handle(Request("shell.exec")).Result) == "unknown_method", "unknown method rejected");
            var task = rt.Handle(Request()); var calls = 0;
            rt.ProcessOne(false, r => { calls++; return null; });
            Check(calls == 0 && !task.IsCompleted && rt.PendingCount == 1, "busy CAD does not execute queued work");
            rt.ProcessOne(true, r => { calls++; return new { id = r.Id, ok = true }; });
            Check((bool)Decode(task.Result)["ok"] && calls == 1 && rt.PendingCount == 0, "UI pump dispatches exactly once");
        }
        using (var rt = Runtime()) {
            var id = Guid.NewGuid().ToString();
            var payload = Request("tools.call", id, ",\"tool\":\"draw.circle\",\"args\":{\"r\":10,\"cx\":0}");
            var calls = 0;
            var first = rt.Handle(payload); var duplicate = rt.Handle(payload);
            rt.ProcessOne(true, r => { calls++; return new { id = r.Id, ok = true, value = "한국어 도면" }; });
            Check(first.Result == duplicate.Result && calls == 1, "concurrent duplicate ID executes once");
            Check(rt.Handle(payload).Result == first.Result && calls == 1, "completed request replay returns cached result");
            var reordered = "{\"args\":{\"cx\":0,\"r\":10},\"tool\":\"draw.circle\",\"method\":\"tools.call\",\"id\":\"" + id + "\"}";
            Check(rt.Handle(reordered).Result == first.Result, "JSON property order does not change identity");
            Check(Code(rt.Handle(payload.Replace("\"r\":10", "\"r\":11")).Result) == "id_conflict", "same ID with changed arguments rejected");
            Check(rt.Handle(Request("request.status", extra: ",\"requestId\":\"" + id + "\"")).Result == first.Result, "request.status returns original result");
            Check(Code(rt.Handle(Request("request.status", extra: ",\"requestId\":\"missing\"")).Result) == "unknown_request", "unknown request is not retried");
        }
        using (var rt = Runtime(25)) {
            var calls = 0; var task = rt.Handle(Request()); Thread.Sleep(40);
            rt.ProcessOne(true, r => { calls++; return null; });
            Check(Code(task.Result) == "expired" && calls == 0 && rt.PendingCount == 0, "expired queue entry never executes later");
        }
        using (var rt = Runtime(1)) {
            var task = rt.Handle(Request());
            Check(Code(task.GetAwaiter().GetResult()) == "expired" && rt.PendingCount == 0, "deadline expires even when UI pump is absent");
        }
        using (var rt = Runtime()) {
            var tasks = Enumerable.Range(0, 16).Select(i => rt.Handle(Request())).ToArray();
            Check(Code(rt.Handle(Request()).Result) == "busy", "pending queue has a bounded capacity");
            rt.Dispose();
            Check(tasks.All(t => Code(t.Result) == "stopped"), "stop cancels all queued work");
            Check(Code(rt.Handle(Request()).Result) == "stopped", "stopped runtime rejects late arrivals");
        }
        using (var rt = Runtime()) {
            for (var i = 0; i < 512; i++) Complete(rt, Request());
            Check(rt.PendingCount == 0 && Code(rt.Handle(Request()).Result) == "history_full", "retained history is distinguished from pending queue");
        }
        using (var rt = Runtime()) {
            var result = Complete(rt, Request(), r => { throw new InvalidOperationException("expected test failure"); });
            Check(Code(result) == "execution_failed" && rt.PendingCount == 0, "dispatcher exception releases queue slot");
            var nested = 0;
            var a = rt.Handle(Request()); var b = rt.Handle(Request());
            rt.ProcessOne(true, r => { rt.ProcessOne(true, n => { nested++; return null; }); return new { id = r.Id, ok = true }; });
            a.GetAwaiter().GetResult();
            Check(!b.IsCompleted && nested == 0, "reentrant UI events cannot run a second CAD tool");
            rt.ProcessOne(true, r => new { id = r.Id, ok = true });
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', BridgeRuntime.FrameLimit + 1) + "\n"))) {
            try { BridgeRuntime.ReadLine(stream).GetAwaiter().GetResult(); Check(false, "oversized frame"); }
            catch (InvalidDataException) { Check(true, "oversized frame rejected"); }
        }
        using (var stream = new MemoryStream(new byte[] { 0xc0, 0xaf, 10 })) {
            try { BridgeRuntime.ReadLine(stream).GetAwaiter().GetResult(); Check(false, "invalid UTF-8"); }
            catch (InvalidDataException) { Check(true, "invalid UTF-8 rejected"); }
        }
        using (var rt = Runtime()) {
            rt.Listen();
            var id = Guid.NewGuid().ToString();
            var a = Exchange(rt.PipeName, Request("tools.call", id, ",\"tool\":\"draw.text\",\"args\":{\"text\":\"한국어 도면 검증\"}"), 1);
            Until(() => rt.PendingCount == 1);
            var state = Exchange(rt.PipeName, Request("request.status", extra: ",\"requestId\":\"" + id + "\""));
            Check((string)Decode(state.Result)["state"] == "queued", "real pipe serves request.status while another connection waits");
            var b = Exchange(rt.PipeName, Request()); var c = Exchange(rt.PipeName, Request());
            Until(() => rt.PendingCount == 3);
            var calls = 0;
            for (var i = 0; i < 3; i++) rt.ProcessOne(true, r => { calls++; return new { id = r.Id, ok = true, text = r.Args == null ? "" : r.Args["text"] }; });
            Check((string)Decode(a.Result)["text"] == "한국어 도면 검증" && (bool)Decode(b.Result)["ok"] && (bool)Decode(c.Result)["ok"] && calls == 3, "three real pipe clients and byte-split Korean UTF-8");
            using (var broken = new NamedPipeClientStream(".", rt.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) { broken.Connect(3000); broken.WriteByte((byte)'{'); }
            var healthy = Exchange(rt.PipeName, Request()); Until(() => rt.PendingCount == 1);
            rt.ProcessOne(true, r => new { id = r.Id, ok = true });
            Check((bool)Decode(healthy.Result)["ok"], "disconnected client does not stop listeners");
        }
        var dir = Path.Combine(Path.GetTempPath(), "AutoCadAgent-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var pid = Process.GetCurrentProcess().Id;
            string name;
            using (var rt = BridgeRuntime.Start(dir, pid)) {
                name = rt.PipeName;
                Check(File.Exists(Path.Combine(dir, pid + ".json")), "endpoint published after listeners are bound");
                var task = Exchange(name, Request()); Until(() => rt.PendingCount == 1);
                rt.ProcessOne(true, r => new { id = r.Id, ok = true });
                Check((bool)Decode(task.Result)["ok"], "published endpoint accepts a real connection");
            }
            Check(!File.Exists(Path.Combine(dir, pid + ".json")), "stop removes owned endpoint manifest");
            for (var i = 0; i < 5; i++) using (var rt = BridgeRuntime.Start(dir, pid)) {
                Check(rt.PipeName != name && rt.PendingCount == 0, "restart " + (i + 1) + " has fresh identity and queue"); name = rt.PipeName;
            }
            var blocked = Path.Combine(dir, "blocked"); File.WriteAllText(blocked, "not a directory");
            var failed = false;
            try { using (var unused = BridgeRuntime.Start(blocked, pid)) { } } catch (IOException) { failed = true; }
            Check(failed && !File.Exists(Path.Combine(dir, pid + ".json")), "failed startup leaves no ready manifest");
            using (var recovered = BridgeRuntime.Start(dir, pid)) Check(recovered.IsRunning, "startup can recover after a failure");
        } finally { Directory.Delete(dir, true); }
    }
}
