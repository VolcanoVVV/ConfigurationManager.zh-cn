using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using ConfigurationManager;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private const BindingFlags AuditFlags = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _auditAssertions;
    private static TaskCompletionSource<string> AuditReply;
    private static Func<string, string, Task<string>> AuditTransport;
    public static Task<string> AuditFakePost(string route, string body) => AuditTransport == null ? AuditReply.Task : AuditTransport(route, body);
    private static void AuditCheck(bool value, string message)
    {
        if (!value) throw new Exception("Audit regression: " + message);
        _auditAssertions++;
    }
    private static void AuditField(Type type, string name, object value) => type.GetField(name, AuditFlags).SetValue(null, value);
    private static TranslationWorkItem AuditItem(string guid, string source) => TranslationWorkItem.ForSetting(guid, "Enabled", "Description", source, "Description");
    private static void AuditCurrent(TranslationWorkItem item)
    {
        var all = (Dictionary<string, Dictionary<string, TranslationWorkItem>>)typeof(JsoncLocalization).GetField("CurrentItems", AuditFlags).GetValue(null);
        all[item.PluginGuid] = new Dictionary<string, TranslationWorkItem> { [item.Id] = item };
    }
    private static void AuditSave(TranslationWorkItem item, TranslationLayer layer, string value) => JsoncLocalization.SaveLayer(item.PluginGuid, layer, new[] { item }, new Dictionary<string, string> { [item.Id] = value }, "audit-profile");
    private static void AuditPump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.ElapsedMilliseconds < 3000) { MainThreadDispatcher.Pump(); Thread.Yield(); }
        if (!task.IsCompleted) throw new Exception("Audit fixture did not complete");
        task.GetAwaiter().GetResult();
    }
    private static void AuditReject<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { _auditAssertions++; return; }
        throw new Exception("Audit expected " + typeof(T).Name + ": " + message);
    }
    private static void AuditClearDocuments() => ((System.Collections.IDictionary)typeof(JsoncLocalization).GetField("Documents", AuditFlags).GetValue(null)).Clear();
    private static void AuditDrain()
    {
        for (var i = 0; i < 100; i++) { MainThreadDispatcher.Pump(); Thread.Yield(); }
    }
    internal static void CheckAuditFixes()
    {
        var previousRoot = Paths.PluginPath;
        var root = Path.Combine(Path.GetTempPath(), "cm-audit-fixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        typeof(Paths).GetProperty("PluginPath").SetValue(null, root);
        JsoncLocalization.Initialize(null, LocalizationDisplayMode.Chinese, true, true, true);
        MainThreadDispatcher.Initialize();
        try
        {
            CheckAuditStorage();
            CheckAuditServer();
            CheckAuditDynamicControls();
            CheckAuditUnload();
            Console.WriteLine("PASS: " + _auditAssertions + " P1/P2 audit regression assertions; temporary fixture: " + root);
        }
        finally
        {
            AuditTransport = null;
            ServerWorkbenchClient.RequestTimeoutMilliseconds = 15000;
            ServerTranslationFeature.Dispose();
            MainThreadDispatcher.Shutdown();
            typeof(Paths).GetProperty("PluginPath").SetValue(null, previousRoot);
        }
    }
    private static void CheckAuditStorage()
    {
        var manualItems = new[] { AuditItem("audit.manual", "Sense"), TranslationWorkItem.ForPlugin("audit.title", "Sense"),
            TranslationWorkItem.ForEnum("audit.enum", "Mode", "Sense", "Sense", "Enum"), TranslationWorkItem.ForCustom("audit.custom", "Enabled", "Sense", "Custom") };
        foreach (var manual in manualItems)
        {
            AuditCurrent(manual);
            AuditSave(manual, TranslationLayer.AI, "感知");
            AuditSave(manual, TranslationLayer.Manual, manual.Source);
            AuditCheck(JsoncLocalization.GetDisplayValue(manual) == manual.Source, "explicit manual keep-original wins over AI for " + manual.Kind);
            AuditCheck(JsoncLocalization.GetStoredValue(manual, TranslationLayer.Manual) == manual.Source, "manual editor round trip for " + manual.Kind);
        }
        foreach (var layer in new[] { TranslationLayer.AI, TranslationLayer.Machine })
        {
            var old = AuditItem("audit.atomic." + layer, "Enable old feature"); AuditCurrent(old); AuditSave(old, layer, "启用旧功能");
            var updated = AuditItem(old.PluginGuid, "Enable new feature"); AuditCurrent(updated);
            var path = JsoncLocalization.GetTranslationPath(old.PluginGuid, layer);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                AuditReject<IOException>(() => AuditSave(updated, layer, "启用新功能"), "locked layer body rejects replacement");
            AuditClearDocuments();
            AuditCheck(JsoncLocalization.GetDisplayValue(updated) == updated.Source, "failed body commit cannot approve old text for new source");
            AuditSave(updated, layer, "启用新功能");
            AuditCheck(JsoncLocalization.GetDisplayValue(updated) == "启用新功能", "body commit recovers after lock release");
            var newest = AuditItem(old.PluginGuid, "Enable latest feature"); AuditCurrent(newest);
            var metadata = JsoncLocalization.GetTranslationPath(old.PluginGuid);
            using (var held = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
                AuditReject<IOException>(() => AuditSave(newest, layer, "启用最新功能"), "cache failure after body commit is surfaced");
            AuditClearDocuments();
            AuditCheck(JsoncLocalization.GetDisplayValue(newest) == "启用最新功能", "atomic body carries authoritative source tracking after cache failure");
            File.Delete(metadata); AuditClearDocuments();
            AuditCheck(JsoncLocalization.GetDisplayValue(newest) == "启用最新功能", "layer remains source-checked without baseline cache");
            var document = JObject.Parse(File.ReadAllText(path));
            document[layer.ToString()]["ConfigEntries"]["Enabled"]["Description"] = "被改动的旧译文";
            File.WriteAllText(path, document.ToString()); AuditClearDocuments();
            AuditCheck(JsoncLocalization.GetDisplayValue(newest) == newest.Source, "mismatched body hash cannot display as approved source");
            document["Tracking"][layer.ToString()] = new JObject();
            File.WriteAllText(path, document.ToString()); AuditClearDocuments();
            AuditCheck(JsoncLocalization.GetDisplayValue(newest) == newest.Source, "self-tracked text without its own source record cannot borrow a baseline");
        }
        var corrupt = AuditItem("audit.schema", "Enable feature"); AuditCurrent(corrupt); AuditSave(corrupt, TranslationLayer.Manual, "启用功能");
        var sidecar = JsoncLocalization.GetTranslationPath(corrupt.PluginGuid);
        File.WriteAllText(sidecar, "{\"Tracking\":true}"); AuditClearDocuments();
        var snapshot = new LocalizationSnapshot { Guid = corrupt.PluginGuid, PluginName = "Audit" };
        snapshot.Settings.Add(new SettingSnapshot { StorageKey = "Enabled", Category = "General", DispName = "Enabled", Description = corrupt.Source });
        AuditCheck(JsoncLocalization.GetChanges(snapshot) != null, "malformed metadata does not escape change review");
        AuditCheck(JsoncLocalization.GetInventory(snapshot).Error != null, "malformed file remains visible as inventory error");
        AuditReject<InvalidDataException>(() => AuditSave(corrupt, TranslationLayer.Manual, "重新保存"), "invalid metadata is not silently overwritten");
        foreach (var invalid in new[] { "{\"Tracking\":{\"AI\":[]}}", "{\"Tracking\":{\"Baseline\":{\"x\":true}}}", "{\"Manual\":{\"ConfigEntries\":{\"x\":42}}}" })
            AuditReject<InvalidDataException>(() => JsoncLocalization.ValidateDocumentShape(JObject.Parse(invalid)), "invalid nested schema is rejected");
    }
    private static void CheckAuditServer()
    {
        var feature = typeof(ServerTranslationFeature);
        AuditField(feature, "_initialized", true); AuditField(feature, "_capabilities", new ServerTranslationCapabilities());
        AuditField(feature, "_connectionIdentity", null); AuditField(feature, "_boundConnection", null);
        AuditField(feature, "_postJsonAsync", typeof(Program).GetMethod("AuditFakePost", BindingFlags.Public | BindingFlags.Static));
        AuditReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pull = ServerWorkbenchClient.PullAsync();
        var generation = ServerTranslationFeature.Generation;
        AuditField(feature, "_generation", generation + 1); ServerWorkbenchClient.Reset();
        AuditReject<OperationCanceledException>(() => AuditPump(pull), "generation reset cancels in-flight request");
        AuditReply.SetResult("{\"ok\":true,\"locale\":\"ch\",\"values\":{\"audit-old-server\":\"旧服务器译文\"}}"); AuditDrain();
        AuditCheck(!EFT.LocalizationManager._instance._locales["ch"].ContainsKey("audit-old-server"), "late server response cannot change locales");
        var host = "old-host";
        AuditField(feature, "_connectionIdentity", (Func<string>)(() => host)); AuditField(feature, "_boundConnection", host);
        AuditReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var switched = ServerWorkbenchClient.PullAsync(); host = "new-host";
        AuditReply.SetResult("{\"ok\":true,\"values\":{\"audit-host-switch\":\"旧主机译文\"}}");
        AuditReject<OperationCanceledException>(() => AuditPump(switched), "raw transport identity change is rejected even before a periodic reset");
        AuditCheck(!EFT.LocalizationManager._instance._locales["ch"].ContainsKey("audit-host-switch"), "changed connection identity cannot merge old host text");
        AuditField(feature, "_connectionIdentity", null); AuditField(feature, "_boundConnection", null);
        // A response already waiting in the dispatcher must be checked again at merge time.
        AuditTransport = (route, body) => Task.FromResult("{\"ok\":true,\"locale\":\"ch\",\"values\":{\"audit-queued-server\":\"旧译文\"}}");
        var queued = Task.Run(() => ServerWorkbenchClient.PullAsync());
        var queue = (System.Collections.ICollection)typeof(MainThreadDispatcher).GetField("Pending", AuditFlags).GetValue(null);
        var timer = Stopwatch.StartNew();
        while (queue.Count == 0 && !queued.IsCompleted && timer.ElapsedMilliseconds < 2000) Thread.Yield();
        AuditCheck(queue.Count > 0, "fixture reaches main-thread merge boundary");
        ServerWorkbenchClient.Reset();
        AuditReject<OperationCanceledException>(() => AuditPump(queued), "queued locale merge rejects reset session");
        AuditCheck(!EFT.LocalizationManager._instance._locales["ch"].ContainsKey("audit-queued-server"), "queued stale locale was not merged");
        AuditTransport = null;
        AuditReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource(); AuditField(typeof(TranslationPanel), "_cancellation", cancellation);
        var operation = (Task)typeof(TranslationPanel).GetMethod("RunServerOperationAsync", AuditFlags).Invoke(null,
            new object[] { (Func<Task<string>>)(() => ServerWorkbenchClient.PullAsync()) });
        cancellation.Cancel(); AuditPump(operation);
        AuditCheck(operation.IsCompleted, "UI operation cancels without waiting for transport response");
        AuditReply.SetResult("{\"ok\":true,\"locale\":\"ch\",\"values\":{\"audit-cancelled\":\"迟到译文\"}}"); AuditDrain();
        AuditCheck(!EFT.LocalizationManager._instance._locales["ch"].ContainsKey("audit-cancelled"), "cancelled operation ignores late response");
        AuditReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerWorkbenchClient.RequestTimeoutMilliseconds = 30;
        AuditReject<TimeoutException>(() => AuditPump(ServerWorkbenchClient.PullAsync()), "unresponsive transport has bounded wait");
        AuditReply.SetException(new IOException("late fixture transport failure")); AuditDrain();
        ServerWorkbenchClient.RequestTimeoutMilliseconds = 15000;
        var capabilities = new ServerTranslationCapabilities { WriteAuthRequired = true };
        capabilities.Features.Add("admin-access"); capabilities.Features.Add("catalog");
        AuditField(feature, "_capabilities", capabilities);
        AuditReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        AuditTransport = (route, body) => { calls++; return AuditReply.Task; };
        var refresh = ServerWorkbenchClient.RefreshAsync(); ServerWorkbenchClient.Reset();
        AuditReject<OperationCanceledException>(() => AuditPump(refresh), "access check is session-bound");
        AuditReply.SetResult("{\"ok\":true,\"isAdmin\":true}"); AuditDrain();
        AuditCheck(!ServerWorkbenchClient.IsAdmin && ServerWorkbenchClient.Snapshot == null && !ServerWorkbenchClient.Refreshing && calls == 1,
            "stale access response neither grants privileges nor starts follow-up catalog request");
        var cancelWait = new CancellationTokenSource(); AuditField(typeof(TranslationPanel), "_cancellation", cancelWait);
        AuditField(typeof(ServerWorkbenchClient), "_jobId", "fixture-remote-job");
        AuditTransport = (route, body) =>
        {
            AuditCheck(route == ServerWorkbenchClient.JobCancelRoute && (string)JObject.Parse(body)["jobId"] == "fixture-remote-job",
                "server cancel addresses the known task");
            return Task.FromResult("{\"ok\":true,\"state\":\"running\"}");
        };
        AuditPump((Task)typeof(TranslationPanel).GetMethod("RequestServerCancelAsync", AuditFlags).Invoke(null, null));
        AuditCheck(!cancelWait.IsCancellationRequested, "acknowledgement is not a terminal state; status monitoring continues");
        AuditTransport = null;
    }
    private sealed class AuditDynamicSetting : SettingEntryBase
    {
        public override Type SettingType => typeof(string);
        public override object Get() => "unchanged";
        protected override void SetValue(object value) { throw new Exception("Unexpected config write"); }
    }
    private static void CheckAuditDynamicControls()
    {
        var setting = new AuditDynamicSetting();
        var guid = "audit.dynamic";
        var snapshot = new LocalizationSnapshot { Guid = guid, PluginName = "Dynamic fixture" };
        var source = new SettingSnapshot { StorageKey = "Status", Category = "General", DispName = "Status" }; snapshot.Settings.Add(source);
        var contexts = (System.Collections.IDictionary)typeof(JsoncLocalization).GetField("SettingContexts", AuditFlags).GetValue(null);
        var type = typeof(JsoncLocalization).GetNestedType("SettingContext", BindingFlags.NonPublic);
        var context = Activator.CreateInstance(type, true);
        type.GetField("Guid", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, guid);
        type.GetField("Setting", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, source);
        contexts[setting] = context;
        ((Dictionary<string, LocalizationSnapshot>)typeof(JsoncLocalization).GetField("Snapshots", AuditFlags).GetValue(null))[guid] = snapshot;
        AuditCurrent(AuditItem(guid, "Status"));
        for (var i = 0; i < 3000; i++) JsoncLocalization.RegisterCustomControl(setting, "Elapsed seconds: " + i);
        AuditCheck(snapshot.CustomControls.Count == 0, "automatic discovery ignores changing numeric state labels");
        var captured = JsoncLocalization.RegisterCustomControl(setting, "Elapsed seconds: 123", true);
        AuditCheck(captured != null && snapshot.CreateWorkItems().Any(item => item.Id == captured.Id), "explicit capture remains manually editable");
        AuditCheck(!snapshot.CreateWorkItems(TranslationScope.All).Any(item => item.Id == captured.Id), "captured dynamic text stays out of default batch translation");
        AuditCheck(JsoncLocalization.RegisterCustomControl(setting, new string('a', 513), true) == null, "custom label length is bounded");
        for (var i = 0; i < 3000; i++)
        {
            var name = "State " + (char)('a' + i % 26) + (char)('a' + i / 26 % 26) + (char)('a' + i / 676 % 26);
            JsoncLocalization.RegisterCustomControl(setting, name);
        }
        AuditCheck(snapshot.CustomControls.Count == JsoncLocalization.MaxCustomControlsPerSetting, "nonnumeric discovery has a hard per-setting cap");
        var count = snapshot.CustomControls.Count;
        JsoncLocalization.RegisterCustomControl(setting, "Elapsed seconds: 123", true);
        AuditCheck(snapshot.CustomControls.Count == count, "repeated known labels do not allocate additional records");
        for (var group = 0; group < 6; group++)
        {
            source.StorageKey = "Setting " + group;
            for (var i = 0; i < 100; i++) JsoncLocalization.RegisterCustomControl(setting, "Stable " + (char)('a' + i % 26) + (char)('a' + i / 26));
        }
        AuditCheck(snapshot.CustomControls.Count == JsoncLocalization.MaxCustomControlsPerPlugin, "many settings share a bounded plugin-wide discovery budget");
    }
    private static void CheckAuditUnload()
    {
        var cancellation = new CancellationTokenSource(); AuditField(typeof(TranslationPanel), "_cancellation", cancellation);
        AuditField(typeof(TranslationPanel), "_shutdown", false);
        var item = AuditItem("audit.unload", "Enable feature"); AuditCurrent(item);
        var plan = new TranslationBatchPlan();
        plan.Targets.Add(new TranslationBatchTarget { Guid = item.PluginGuid, Name = "Fixture", Items = new[] { item } });
        plan.Targets.Add(new TranslationBatchTarget { Guid = "audit.next", Name = "Next fixture", Items = new[] { AuditItem("audit.next", "Enable feature") } });
        var response = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        Func<TranslationBatchTarget, Action<int, int>, CancellationToken, Task<TranslationResult>> translate = (target, progress, token) => { requests++; return response.Task; };
        var diagnostics = new TranslationDiagnostics("audit-unload", new[] { item });
        var batch = (Task)typeof(TranslationPanel).GetMethod("RunBatchAsync", AuditFlags).Invoke(null,
            new object[] { plan, TranslationLayer.AI, "fixture", translate, cancellation.Token, 1, diagnostics, 10000, null });
        TranslationPanel.Shutdown();
        AuditCheck(cancellation.IsCancellationRequested, "unload cancels local tasks");
        var result = new TranslationResult(); result.Values[item.Id] = "启用功能"; response.SetResult(result); AuditPump(batch);
        AuditCheck(requests == 1 && !File.Exists(JsoncLocalization.GetTranslationPath(item.PluginGuid, TranslationLayer.AI)),
            "unload blocks late checkpoint saves and subsequent requests even if transport ignores cancellation");
    }
}
