using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AutoCadAgent.Cad.Tools;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: ExtensionApplication(typeof(AutoCadAgent.Cad.BridgePlugin))]
[assembly: CommandClass(typeof(AutoCadAgent.Cad.BridgePlugin))]

namespace AutoCadAgent.Cad
{
    public sealed class BridgePlugin : IExtensionApplication
    {
        private static readonly Dictionary<Document, string> Documents = new Dictionary<Document, string>();
        private static BridgeRuntime Runtime;
        private static System.Windows.Forms.Timer Timer;
        private static string StartupError;
        public void Initialize() { Start(); }
        public void Terminate() { Shutdown(); }
        [CommandMethod("CADAGENTSTART")]
        public void StartCommand() { Start(); }
        [CommandMethod("CADAGENTSTOP")]
        public void StopCommand() { Shutdown(); }
        [CommandMethod("CADAGENTSTATUS")]
        public void StatusCommand()
        {
            var state = Runtime == null ? "stopped" : Runtime.PipeName + ", pending=" + Runtime.PendingCount;
            var error = StartupError ?? Runtime?.LastError;
            Write("AutoCadAgent: " + state + (error == null ? "" : "\nLast error: " + error));
        }
        private static void Write(string text)
        {
            var editor = AcApp.DocumentManager.MdiActiveDocument?.Editor;
            if (editor != null) editor.WriteMessage("\n" + text); else Trace.WriteLine(text);
        }
        private static void Start()
        {
            if (Runtime != null) { Write("AutoCadAgent: already running — " + Runtime.PipeName); return; }
            try {
                ToolRegistry.List(); // Surface catalog/type initialization failures during NETLOAD.
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCadAgent", "bridges");
                Runtime = BridgeRuntime.Start(directory, Process.GetCurrentProcess().Id);
                AcApp.Idle += OnIdle;
                AcApp.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;
                // Idle alone may not fire when a background pipe receives data and the UI is stationary.
                // A UI-thread timer supplies a heartbeat; IsQuiescent and document locks still gate all work.
                Timer = new System.Windows.Forms.Timer { Interval = 100 };
                Timer.Tick += OnIdle; Timer.Start();
                StartupError = null;
                Write("AutoCadAgent 연결 준비 완료. CADAGENTSTATUS로 상태 확인.");
            } catch (System.Exception ex) {
                Shutdown(); StartupError = ex.GetType().Name + ": " + ex.Message;
                Write("AutoCadAgent 시작 실패: " + StartupError + "\n문제 해결 후 CADAGENTSTART로 다시 시도하세요.");
            }
        }
        private static void Shutdown()
        {
            AcApp.Idle -= OnIdle;
            AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocumentDestroyed;
            if (Timer != null) { Timer.Stop(); Timer.Tick -= OnIdle; Timer.Dispose(); Timer = null; }
            var previous = Runtime; Runtime = null;
            previous?.Dispose(); Documents.Clear();
        }
        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs e) { Documents.Remove(e.Document); }
        private static string DocumentId(Document doc)
        {
            if (doc == null) return null;
            if (!Documents.TryGetValue(doc, out var id)) Documents[doc] = id = Guid.NewGuid().ToString("N");
            return id;
        }
        private static void OnIdle(object sender, EventArgs e)
        {
            var runtime = Runtime;
            if (runtime == null || runtime.PendingCount == 0) return;
            try {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                runtime.ProcessOne(doc == null || doc.Editor.IsQuiescent, request => Dispatch(runtime, request));
            } catch (System.Exception ex) { Trace.WriteLine("AutoCadAgent UI dispatch: " + ex.Message); }
        }
        private static object Dispatch(BridgeRuntime runtime, BridgeRequest request)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (request.Method == "status") return new { id = request.Id, ok = true, pipe = runtime.PipeName, document = DocumentId(doc), name = doc?.Name, units = doc?.Database.Insunits.ToString(), tools = ToolRegistry.List().Length, protocolVersion = 2, capabilities = new[] { "input-schema", "structured-geometry", "strict-arguments", "request-replay" } };
            if (request.Method == "tools.list") return new { id = request.Id, ok = true, tools = ToolRegistry.List(request.Filter ?? "") };
            if (request.Method == "tools.describe") {
                var tool = ToolRegistry.Describe(request.Tool);
                if (tool == null) return new { id = request.Id, ok = false, error = new { code = "unknown_tool", message = "도구 이름을 tools 목록에서 확인하세요." } };
                return new { id = request.Id, ok = true, tool };
            }
            if (doc == null || request.Document != DocumentId(doc)) return new { id = request.Id, ok = false, error = new { code = "document_changed", message = "대상 도면이 없거나 변경되었습니다. TUI에서 다시 연결하세요." } };
            if (!Guid.TryParse(request.Client, out var clientId)) return new { id = request.Id, ok = false, error = new { code = "invalid_client", message = "유효한 CLI 세션 UUID 필요" } };
            var previous = DrawSession.ClientId;
            try {
                DrawSession.ClientId = clientId.ToString("D");
                var result = ToolRegistry.Execute(request.Tool, request.Args);
                return new { id = request.Id, ok = result.Success, result, executionKind = ToolContract.Kind(request.Tool), error = result.Success ? null : new { code = result.Code ?? "tool_failed", message = result.Error } };
            } finally { DrawSession.ClientId = previous; }
        }
    }
}
