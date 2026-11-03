using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AutoCadAgent.Cad
{
    // No AutoCAD API here. The production transport is also compiled into net48 integration checks.
    internal sealed class BridgeRequest
    {
        internal string Id, Method, Tool, Document, Client, Filter, Fingerprint;
        internal Dictionary<string, object> Args;
        internal DateTime Expires, Completed;
        internal int State; // 0 queued, 1 running, 2 completed, 3 never executed
        internal string Result;
        internal readonly TaskCompletionSource<string> Done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class BridgeRuntime : IDisposable
    {
        internal const int FrameLimit = 1024 * 1024;
        private readonly object gate = new object();
        private readonly Queue<BridgeRequest> queue = new Queue<BridgeRequest>();
        private readonly Dictionary<string, BridgeRequest> requests = new Dictionary<string, BridgeRequest>(StringComparer.Ordinal);
        private readonly HashSet<NamedPipeServerStream> pipes = new HashSet<NamedPipeServerStream>();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly int waitMs;
        private int pending, disposed;
        private bool pumping;
        private string endpointFile;
        internal string PipeName { get; }
        internal string LastError { get; private set; }
        internal int PendingCount { get { lock (gate) return pending; } }
        internal bool IsRunning => Volatile.Read(ref disposed) == 0;
        private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = FrameLimit, RecursionLimit = 32 };
        internal static string Encode(object value) => Json().Serialize(value);
        internal static string Error(string id, string code, string message) => Encode(new { id, ok = false, error = new { code, message } });
        private static string S(Dictionary<string, object> m, string key) => m.TryGetValue(key, out var value) ? value as string : null;

        internal BridgeRuntime(string pipeName, int waitMilliseconds = 25000)
        {
            PipeName = pipeName;
            waitMs = waitMilliseconds;
        }

        internal static BridgeRuntime Start(string directory, int pid)
        {
            var runtime = new BridgeRuntime("AutoCadAgent-" + pid + "-" + Guid.NewGuid().ToString("N"));
            try {
                runtime.Listen(); // Bind all listeners before publishing the endpoint.
                Directory.CreateDirectory(directory);
                runtime.endpointFile = Path.Combine(directory, pid + ".json");
                var temporary = runtime.endpointFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    File.WriteAllText(temporary, Encode(new { pipe = runtime.PipeName, pid, version = "0.1.0", createdUtc = DateTime.UtcNow.ToString("O") }), new UTF8Encoding(false));
                    if (File.Exists(runtime.endpointFile)) File.Replace(temporary, runtime.endpointFile, null);
                    else File.Move(temporary, runtime.endpointFile);
                } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return runtime;
            } catch { runtime.Dispose(); throw; }
        }

        internal void Listen()
        {
            var initial = new List<NamedPipeServerStream>();
            try {
                // Separate listeners keep request.status and other CLI sessions responsive while CAD is busy.
                for (var i = 0; i < 8; i++) initial.Add(CreatePipe());
                foreach (var pipe in initial) { var captured = pipe; Task.Run(() => Serve(captured)); }
            } catch { Dispose(); throw; }
        }

        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            lock (gate) {
                if (!IsRunning) throw new OperationCanceledException();
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                pipes.Add(pipe);
                return pipe;
            }
        }

        private async Task Serve(NamedPipeServerStream first)
        {
            var pipe = first;
            while (IsRunning) {
                try {
                    if (pipe == null) pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
                        timeout.CancelAfter(30000);
                        var connected = pipe;
                        using (timeout.Token.Register(() => { try { connected.Dispose(); } catch { } })) {
                            string result;
                            try { result = await Handle(await ReadLine(pipe).ConfigureAwait(false)).ConfigureAwait(false); }
                            catch (InvalidDataException ex) { result = Error(null, "invalid_frame", ex.Message); }
                            var bytes = Encoding.UTF8.GetBytes(result + "\n");
                            await pipe.WriteAsync(bytes, 0, bytes.Length, stop.Token).ConfigureAwait(false);
                            await pipe.FlushAsync(stop.Token).ConfigureAwait(false);
                        }
                    }
                } catch (OperationCanceledException) { }
                  catch (System.Exception ex) {
                    if (IsRunning) { LastError = ex.GetType().Name + ": " + ex.Message; Trace.WriteLine("AutoCadAgent bridge: " + LastError); }
                } finally {
                    if (pipe != null) { lock (gate) pipes.Remove(pipe); pipe.Dispose(); pipe = null; }
                }
                // Bound retries if pipe creation or the environment fails.
                if (IsRunning) { try { await Task.Delay(10, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } }
            }
            // Stop can win before this worker enters the loop.
            if (pipe != null) { lock (gate) pipes.Remove(pipe); pipe.Dispose(); }
        }

        internal static async Task<string> ReadLine(Stream stream)
        {
            var buffer = new byte[4096];
            using (var output = new MemoryStream()) {
                while (true) {
                    var n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (n == 0) throw new EndOfStreamException();
                    var end = Array.IndexOf(buffer, (byte)10, 0, n);
                    var count = end < 0 ? n : end;
                    if (output.Length + count > FrameLimit) throw new InvalidDataException("request exceeds 1 MiB");
                    output.Write(buffer, 0, count);
                    if (end >= 0) {
                        try { return new UTF8Encoding(false, true).GetString(output.ToArray()); }
                        catch (DecoderFallbackException) { throw new InvalidDataException("invalid UTF-8"); }
                    }
                }
            }
        }

        private static object Canonical(object value)
        {
            if (value is Dictionary<string, object> map) return new SortedDictionary<string, object>(map.ToDictionary(p => p.Key, p => Canonical(p.Value)), StringComparer.Ordinal);
            if (value is object[] items) return items.Select(Canonical).ToArray();
            return value;
        }
        private static string Fingerprint(Dictionary<string, object> request)
        {
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(Encode(Canonical(request)))));
        }

        internal async Task<string> Handle(string line)
        {
            string id = null;
            try {
                var request = Json().DeserializeObject(line) as Dictionary<string, object> ?? throw new ArgumentException("JSON 객체 필요");
                id = S(request, "id");
                if (!Guid.TryParse(id, out var parsedId)) return Error(id, "invalid_request", "UUID 요청 ID 필요");
                id = parsedId.ToString("D");
                request["id"] = id;
                var method = S(request, "method");
                BridgeRequest work;
                lock (gate) {
                    if (!IsRunning) return Error(id, "stopped", "브리지가 중지되었습니다.");
                    if (method == "request.status") {
                        var target = S(request, "requestId");
                        if (Guid.TryParse(target, out var targetId)) target = targetId.ToString("D");
                        if (target != null && requests.TryGetValue(target, out var previous)) return previous.Result ?? Encode(new { id, ok = true, state = previous.State == 1 ? "running" : "queued", requestId = target });
                        return Error(id, "unknown_request", "기록이 없거나 만료되었습니다. 변경 작업을 자동 재전송하지 마세요.");
                    }
                    if (method != "status" && method != "tools.list" && method != "tools.describe" && method != "tools.call") return Error(id, "unknown_method", "지원하지 않는 메서드");
                    if ((method == "tools.call" || method == "tools.describe") && string.IsNullOrWhiteSpace(S(request, "tool"))) return Error(id, "invalid_request", "tool 이름 필요");
                    if (method == "tools.call" && request.TryGetValue("args", out var arguments) && !(arguments is Dictionary<string, object>)) return Error(id, "invalid_request", "args는 JSON 객체여야 합니다.");
                    var fingerprint = Fingerprint(request);
                    if (requests.TryGetValue(id, out work)) {
                        if (work.Fingerprint != fingerprint) return Error(id, "id_conflict", "같은 요청 ID에 다른 내용을 사용할 수 없습니다.");
                    } else {
                        foreach (var key in requests.Where(p => p.Value.State >= 2 && p.Value.Completed < DateTime.UtcNow.AddMinutes(-5)).Select(p => p.Key).ToArray()) requests.Remove(key);
                        if (pending >= 16) return Error(id, "busy", "실행 대기 중인 요청이 16개입니다.");
                        // Do not silently evict recent mutation IDs: retries must never execute twice within retention.
                        if (requests.Count >= 512) return Error(id, "history_full", "중복 실행 방지 기록 한도입니다. 완료 기록은 5분 후 만료됩니다. 대기열 포화와 다릅니다.");
                        work = new BridgeRequest { Id = id, Method = method, Tool = S(request, "tool"), Document = S(request, "document"), Client = S(request, "client"), Filter = S(request, "filter"), Args = request.TryGetValue("args", out var a) ? a as Dictionary<string, object> : null, Fingerprint = fingerprint, Expires = DateTime.UtcNow.AddMilliseconds(waitMs) };
                        requests.Add(id, work); queue.Enqueue(work); pending++;
                    }
                }
                var completed = await Task.WhenAny(work.Done.Task, Task.Delay(waitMs + 1000)).ConfigureAwait(false);
                if (completed == work.Done.Task) return await work.Done.Task.ConfigureAwait(false);
                lock (gate) {
                    if (work.State == 0) Finish(work, Error(id, "expired", "AutoCAD의 대기시간이 초과되었습니다. 이 요청은 실행되지 않았습니다."), 3);
                    return work.Result ?? Error(id, "execution_unknown", "실행이 시작되어 결과를 기다리는 중입니다. request.status로 조회하고 자동 재실행하지 마세요.");
                }
            } catch (System.Exception ex) { return Error(id, "invalid_request", ex.Message); }
        }

        // Must be invoked only on the AutoCAD UI thread. Never call CAD APIs in pipe workers.
        internal void ProcessOne(bool ready, Func<BridgeRequest, object> dispatch)
        {
            BridgeRequest work;
            lock (gate) {
                if (!IsRunning || pumping) return;
                while (queue.Count > 0 && (queue.Peek().State != 0 || queue.Peek().Expires <= DateTime.UtcNow)) {
                    var expired = queue.Dequeue();
                    if (expired.State == 0) Finish(expired, Error(expired.Id, "expired", "대기시간 초과. 실행하지 않았습니다."), 3);
                }
                if (!ready || queue.Count == 0) return;
                work = queue.Dequeue(); work.State = 1; pumping = true;
            }
            string result;
            try {
                result = Encode(dispatch(work));
                if (Encoding.UTF8.GetByteCount(result) > FrameLimit) result = Error(work.Id, "response_too_large", "실행은 완료되었지만 응답 한도를 초과했습니다. 변경 작업을 재전송하지 마세요.");
            } catch (System.Exception ex) { result = Error(work.Id, "execution_failed", ex.GetType().Name + ": " + ex.Message); }
            lock (gate) { Finish(work, result, 2); pumping = false; }
        }

        // gate is held by the caller.
        private void Finish(BridgeRequest work, string result, int state)
        {
            if (work.State >= 2) return;
            work.Result = result; work.State = state; work.Completed = DateTime.UtcNow; pending--;
            work.Done.TrySetResult(result);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel();
            lock (gate) {
                foreach (var pipe in pipes.ToArray()) { try { pipe.Dispose(); } catch { } }
                foreach (var work in requests.Values) if (work.State == 0) Finish(work, Error(work.Id, "stopped", "브리지가 중지되었습니다. 이 요청은 실행되지 않았습니다."), 3);
                queue.Clear();
            }
            if (endpointFile != null) {
                try {
                    // A previous instance must not delete a newer instance's manifest.
                    var data = Json().DeserializeObject(File.ReadAllText(endpointFile)) as Dictionary<string, object>;
                    if (data != null && S(data, "pipe") == PipeName) File.Delete(endpointFile);
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // Workers still observe stop.Token; do not dispose the source while they unwind.
        }
    }
}
