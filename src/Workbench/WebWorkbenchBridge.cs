using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    internal static class WebWorkbenchBridge
    {
        private static WebWorkbenchFiles Files => new WebWorkbenchFiles(Paths.PluginPath);
        private static ServerWorkbenchSnapshot _serverCatalog;
        private static string _serverExport;
        private static int _generation;
        private static JObject _preview;
        private static Vector2 _scroll;
        private static Task<string> _pending;
        private static string _status = "";
        private static string _draftStatus = "";
        private static float _nextDraftCheck;
        private static bool _open;
        private static bool _openForLayout;
        private static readonly object ConnectionSync = new object();
        private static System.Threading.Timer _connectionHeartbeat;
        private static int _connectionGeneration = -1;
        private static string _connectionId;
        private static bool _connectionLocked, _publishedConnection, _connectionWriteFailed;
        private static float _nextConnectionCheck;
        private static float _nextReceiptCheck;
        private static Task _receiptRefresh;
        private static bool _showServerForLayout;

        internal static void UpdateServerConnection(ManualLogSource log, bool force = false)
        {
            var now = Time.realtimeSinceStartup;
            if (!force && now < _nextConnectionCheck) return;
            _nextConnectionCheck = now + 0.25f;
            var generation = ServerTranslationFeature.Generation;
            var available = ServerWorkbenchClient.SupportsWebContributions;
            var locked = available && !ServerWorkbenchClient.CanUseWorkbench;
            var changed = generation != _connectionGeneration || available != (_connectionId != null) || locked != _connectionLocked;
            if (!force && !changed && available && !locked && now >= _nextReceiptCheck &&
                (_receiptRefresh == null || _receiptRefresh.IsCompleted) &&
                ServerTranslationFeature.Capabilities?.SupportsFeature("contribution-receipts") == true)
            {
                _nextReceiptCheck = now + 5f;
                _receiptRefresh = RefreshServerReceiptAsync(generation, _connectionId, log);
            }
            if (!changed && !force) return;
            lock (ConnectionSync)
            {
                if (generation != _connectionGeneration || !available)
                {
                    _connectionGeneration = generation;
                    _connectionId = null;
                    _serverCatalog = null;
                    _serverExport = null;
                }
                if (available && _connectionId == null) _connectionId = Guid.NewGuid().ToString("N");
                _connectionLocked = locked;
                PublishServerConnection(log);
                if (force)
                {
                    _connectionHeartbeat?.Dispose();
                    _connectionHeartbeat = null;
                }
                else if (_connectionHeartbeat == null && available)
                {
                    // Unity may stop updating when the browser has focus. Keep the lease on a managed timer.
                    _connectionHeartbeat = new System.Threading.Timer(_ =>
                    {
                        lock (ConnectionSync) PublishServerConnection(log);
                    }, null, 5000, 5000);
                }
            }
        }

        private static void PublishServerConnection(ManualLogSource log)
        {
            if (_connectionId == null && !_publishedConnection) return;
            try
            {
                Files.Write("server", "connection", new JObject
                {
                    ["id"] = _connectionId, ["locked"] = _connectionLocked,
                    ["expiresUtcTicks"] = DateTime.UtcNow.AddSeconds(20).Ticks
                });
                _publishedConnection = _connectionId != null;
                _connectionWriteFailed = false;
            }
            catch (Exception ex)
            {
                if (!_connectionWriteFailed) log?.LogWarning("无法同步网页服务端工作台连接状态：" + ex.GetBaseException().Message);
                _connectionWriteFailed = true;
            }
        }

        internal static void DrawToolbar()
        {
            if (Event.current.type == EventType.Layout) _openForLayout = _open;
            if (GUILayout.Button(TranslationPanel.Content("打开网页", "打开本机网页汉化工作台"), GUILayout.ExpandWidth(true))) Run(OpenWorkbench);
            if (GUILayout.Button(TranslationPanel.Content(_openForLayout ? "快照与草稿 ▴" : "快照与草稿 ▾", "刷新网页快照、应用草稿或提交服务端译文"),
                GUILayout.ExpandWidth(true)))
                _open = !_open;
        }

        internal static void DrawDetails(bool manualDirty, bool jobRunning)
        {
            if (!_openForLayout)
            {
                if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, TranslationPanelTheme.Muted);
                return;
            }
            if (_pending != null && _pending.IsCompleted && Event.current.type == EventType.Layout)
            {
                _status = _pending.Status == TaskStatus.RanToCompletion ? _pending.Result :
                    _pending.Exception?.GetBaseException().Message ?? "连接已切换或操作已取消。";
                _pending = null;
            }
            if (Event.current.type == EventType.Layout)
            {
                var showServer = ServerWorkbenchClient.SupportsWebContributions && ServerWorkbenchClient.CanUseWorkbench;
                if (!showServer || _generation != ServerTranslationFeature.Generation) _preview = null;
                if (showServer != _showServerForLayout || Time.realtimeSinceStartup >= _nextDraftCheck)
                {
                    _showServerForLayout = showServer;
                    _nextDraftCheck = Time.realtimeSinceStartup + 2f;
                    try
                    {
                        _draftStatus = ReadDraftStatus("f12");
                        if (_showServerForLayout && _serverExport != null) _draftStatus += "\n" + ReadDraftStatus("server");
                    }
                    catch (Exception ex) { _draftStatus = "读取网页草稿状态失败：" + ex.GetBaseException().Message; }
                }
            }
            GUILayout.Label("刷新快照可将游戏内最新原文与译文发给网页。网页草稿在这里确认应用或投稿。", TranslationPanelTheme.Muted);
            if (!string.IsNullOrEmpty(_draftStatus)) GUILayout.Label(_draftStatus, TranslationPanelTheme.Muted);
            var enabled = GUI.enabled;
            GUI.enabled = enabled && _pending == null;
            if (GUILayout.Button("刷新网页 F12 快照")) Run(ExportLocal);
            GUI.enabled = enabled && _pending == null && !manualDirty && !jobRunning;
            if (GUILayout.Button("应用网页 F12 草稿")) Run(ApplyLocal);
            if (_showServerForLayout)
            {
                GUI.enabled = enabled && _pending == null;
                if (GUILayout.Button("刷新网页服务端快照")) _pending = ExportServerAsync();
                if (GUILayout.Button("预览网页服务端投稿")) Run(PreviewServer);
            }
            GUI.enabled = enabled;
            if (_showServerForLayout && _preview != null)
            {
                GUILayout.Label("请核对当前服务器投稿。确认后以玩家身份进入审核队列。", TranslationPanelTheme.Section);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(180f));
                foreach (var row in (JArray)_preview["edits"])
                    GUILayout.Label((string)row["source"] + "\n" + (string)row["text"], TranslationPanelTheme.Muted);
                GUILayout.EndScrollView();
                GUI.enabled = enabled && _pending == null && ServerWorkbenchClient.SupportsWebContributions;
                if (GUILayout.Button("确认提交玩家投稿"))
                {
                    var preview = _preview;
                    _preview = null;
                    _pending = SubmitServerAsync(preview);
                }
                GUI.enabled = enabled;
                if (GUILayout.Button("取消预览")) _preview = null;
            }
            if (manualDirty) GUILayout.Label("当前人工译文有未保存修改，请先保存或放弃。", TranslationPanelTheme.Muted);
            if (_pending != null) GUILayout.Label("正在等待服务端回应…", TranslationPanelTheme.Muted);
            else if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, TranslationPanelTheme.Muted);
        }

        private static void Run(Func<string> action)
        {
            try { _status = action(); }
            catch (Exception ex) { _status = ex.GetBaseException().Message; }
        }

        private static string OpenWorkbench()
        {
            var tools = Path.Combine(Paths.GameRootPath, "网页汉化工作台");
            var executable = Path.Combine(Paths.GameRootPath, "网页汉化工作台.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(tools, "网页汉化工作台.dll")))
                throw new FileNotFoundException("请下载独立网页汉化工作台并完整解压到游戏根目录，确保根目录有「网页汉化工作台.exe」。");

            var snapshot = Files.Read("f12", "snapshot");
            if ((int?)snapshot?["draftVersion"] != WebWorkbenchFiles.F12DraftVersion && JsoncLocalization.GetSnapshots().Any())
                ExportLocal();

            using (Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = tools,
                UseShellExecute = false,
                CreateNoWindow = true
            })) { }
            return "正在打开浏览器。已有网页工作台会直接复用，同步新原文可使用下方导出。";
        }

        private static JObject Snapshot(string revision, JArray rows) => new JObject
        {
            ["revision"] = revision, ["capturedAt"] = DateTime.UtcNow.ToString("O"), ["rows"] = rows
        };

        private static string ReadDraftStatus(string domain)
        {
            var label = domain == "f12" ? "F12 " : "服务端 ";
            var draft = Files.Read(domain, "draft");
            if (draft == null || (domain == "server" && (string)draft["connection"] != _connectionId))
                return label + "尚无网页草稿";
            var receipt = Files.Read(domain, "receipt");
            return receipt != null && (string)receipt["id"] == (string)draft["id"]
                ? label + (string)receipt["detail"] : label + "网页草稿已保存，等待游戏确认";
        }

        private static string ExportLocal()
        {
            var rows = new JArray();
            var removed = new JArray();
            foreach (var snapshot in JsoncLocalization.GetSnapshots())
            {
                var changes = JsoncLocalization.GetChanges(snapshot);
                var indexed = changes.ToDictionary(change => change.Id, StringComparer.Ordinal);
                foreach (var item in snapshot.CreateWorkItems(TranslationScope.All))
                {
                    var row = new JObject
                    {
                        ["id"] = item.Id, ["group"] = snapshot.Guid, ["name"] = snapshot.PluginName,
                        ["version"] = snapshot.PluginVersion, ["source"] = item.Source, ["context"] = item.Context,
                        ["field"] = item.Field ?? item.Kind, ["manual"] = JsoncLocalization.GetStoredValue(item, TranslationLayer.Manual),
                        ["ai"] = JsoncLocalization.GetStoredValue(item, TranslationLayer.AI),
                        ["machine"] = JsoncLocalization.GetStoredValue(item, TranslationLayer.Machine)
                    };
                    if (item.OriginalSection != null) row["section"] = item.OriginalSection;
                    if (!string.IsNullOrWhiteSpace(item.BriefDescription)) row["descriptionHint"] = item.BriefDescription;
                    if (indexed.TryGetValue(item.Id, out var change))
                    {
                        row["change"] = change.State;
                        row["previousSource"] = change.OldSource;
                    }
                    rows.Add(row);
                }
                foreach (var change in changes.Where(change => change.State == "已移除"))
                    removed.Add(new JObject
                    {
                        ["id"] = change.Id, ["group"] = snapshot.Guid, ["name"] = snapshot.PluginName,
                        ["source"] = "", ["previousSource"] = change.OldSource, ["context"] = change.Context,
                        ["field"] = "", ["change"] = change.State, ["removed"] = true
                    });
            }
            if (rows.Count == 0) throw new InvalidOperationException("尚未收集到可翻译的插件设置。");
            var file = Snapshot(Guid.NewGuid().ToString("N"), rows);
            file["updateBasis"] = "f12-tracking";
            file["draftVersion"] = WebWorkbenchFiles.F12DraftVersion;
            file["removed"] = removed;
            var previous = Files.Read("f12", "snapshot");
            if (previous != null && JToken.DeepEquals(previous["rows"], rows) &&
                JToken.DeepEquals(previous["removed"], removed) && (string)previous["updateBasis"] == "f12-tracking")
            {
                if ((int?)previous["draftVersion"] == WebWorkbenchFiles.F12DraftVersion) return "F12 快照已是最新。";
                file["revision"] = previous["revision"];
            }
            Files.Write("f12", "snapshot", file);
            return "已导出 " + rows.Count + " 条 F12 文本。";
        }

        private static async Task<string> ExportServerAsync()
        {
            var generation = ServerTranslationFeature.Generation;
            var snapshot = await ServerWorkbenchClient.ReadWebContributionCatalogAsync().ConfigureAwait(false);
            return await MainThreadDispatcher.InvokeAsync(() =>
            {
                if (!ServerTranslationFeature.IsCurrentConnection(generation) || !ServerWorkbenchClient.CanUseWorkbench ||
                    _connectionGeneration != generation || _connectionId == null)
                    throw new InvalidOperationException("服务器会话已切换，请重新导出。");
                var rows = new JArray(snapshot.Entries.Select(item => new JObject
                {
                    ["id"] = item.Id, ["group"] = item.SourceGroup, ["name"] = item.SourceGroupName,
                    ["source"] = item.English, ["context"] = item.Domain, ["field"] = item.Domain, ["manual"] = item.Chinese
                }));
                if (_serverCatalog != null && _generation == generation && _serverCatalog.Revision == snapshot.Revision)
                {
                    var current = Files.Read("server", "snapshot");
                    if ((string)current?["revision"] == _serverExport && (string)current["connection"] == _connectionId &&
                        SameServerRows((JArray)current["rows"], rows))
                        return "服务端网页快照已是最新，可继续使用现有草稿。";
                }
                _preview = null;
                var export = Guid.NewGuid().ToString("N");
                var file = Snapshot(export, rows);
                file["connection"] = _connectionId;
                if (_serverCatalog != null && _generation == generation)
                {
                    var earlier = _serverCatalog.Entries.ToDictionary(item => item.Id, StringComparer.Ordinal);
                    foreach (var row in rows.OfType<JObject>())
                    {
                        if (!earlier.TryGetValue((string)row["id"], out var previous))
                            row["change"] = "目录新增";
                        else if (previous.English != (string)row["source"])
                        {
                            row["change"] = "已修改";
                            row["previousSource"] = previous.English;
                        }
                    }
                    file["updateBasis"] = "server-previous-export";
                }
                Files.Write("server", "snapshot", file);
                _generation = generation;
                _serverCatalog = snapshot;
                _serverExport = export;
                return "已导出 " + rows.Count + " 条服务端文本。";
            }).ConfigureAwait(false);
        }

        private static bool SameServerRows(JArray previous, JArray current)
        {
            if (previous == null || previous.Count != current.Count) return false;
            var fields = new[] { "id", "group", "name", "source", "context", "field", "manual" };
            for (var index = 0; index < current.Count; index++)
                if (fields.Any(field => !JToken.DeepEquals(previous[index][field], current[index][field]))) return false;
            return true;
        }

        private static JObject LoadDraft(string domain, int limit)
        {
            var draft = Files.Read(domain, "draft");
            var id = (string)draft?["id"];
            if (!Guid.TryParseExact(id, "N", out _) || !(draft["edits"] is JArray edits) || edits.Count < 1 || edits.Count > limit)
                throw new InvalidOperationException("没有可用草稿，或草稿格式无效。");
            if ((string)Files.Read(domain, "receipt")?["id"] == id)
                throw new InvalidOperationException("这份草稿已有处理回执，请在网页查看。");
            return draft;
        }

        private static void Receipt(string domain, JObject draft, string state, string detail)
        {
            Files.Write(domain, "receipt", new JObject { ["id"] = (string)draft["id"], ["state"] = state, ["detail"] = detail });
        }

        private static string ApplyLocal()
        {
            var draft = LoadDraft("f12", 100);
            var snapshotFile = Files.Read("f12", "snapshot");
            if ((string)snapshotFile?["revision"] != (string)draft["revision"])
                throw new InvalidOperationException("草稿来自旧快照，请重新核对。");
            var layer = (string)draft["layer"];
            TranslationLayer parsed;
            switch (layer)
            {
                case "manual": parsed = TranslationLayer.Manual; break;
                case "ai": parsed = TranslationLayer.AI; break;
                case "machine": parsed = TranslationLayer.Machine; break;
                default: throw new InvalidOperationException("不支持该译文层。");
            }
            var edits = (JArray)draft["edits"];
            var rows = edits.OfType<JObject>().ToArray();
            if (rows.Length != edits.Count) throw new InvalidOperationException("草稿含无效条目。");
            var snapshots = JsoncLocalization.GetSnapshots().ToDictionary(item => item.Guid, StringComparer.Ordinal);
            var batches = new List<(string Guid, List<TranslationWorkItem> Items, Dictionary<string, string> Expected,
                Dictionary<string, string> Values)>();
            foreach (var group in rows.GroupBy(row => (string)row["group"] ?? (string)draft["group"], StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(group.Key) || !snapshots.TryGetValue(group.Key, out var snapshot))
                    throw new InvalidOperationException("当前游戏没有草稿对应的插件设置，请重新导出。");
                var current = TranslationWorkItem.Index(snapshot.CreateWorkItems());
                var items = new List<TranslationWorkItem>();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var expected = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in group)
                {
                    var id = (string)row["id"];
                    if (string.IsNullOrEmpty(id) || !current.TryGetValue(id, out var item) || item.Source != (string)row["source"] ||
                        item.Context != (string)row["context"] || row["base"]?.Type != JTokenType.String)
                        throw new InvalidOperationException("原文、语境或草稿已变化，整批未应用。请重新导出并核对。");
                    if (values.ContainsKey(id)) throw new InvalidOperationException("草稿含重复条目。");
                    values.Add(id, ValidateText(row, item, parsed == TranslationLayer.Manual));
                    items.Add(item);
                    expected.Add(id, (string)row["base"]);
                }
                JsoncLocalization.CheckWebLayer(group.Key, parsed, items, expected);
                batches.Add((group.Key, items, expected, values));
            }
            var saved = 0;
            string failure = null;
            foreach (var batch in batches)
            {
                try
                {
                    JsoncLocalization.SaveWebLayer(batch.Guid, parsed, batch.Items, batch.Expected, batch.Values);
                    saved += batch.Items.Count;
                }
                catch (Exception ex) when (saved > 0)
                {
                    failure = batch.Guid + " 保存失败：" + ex.GetBaseException().Message;
                    break;
                }
            }
            TranslationPanel.WebTranslationsSaved();
            var detail = "已保存 " + saved + " 条并请求刷新 F12。";
            if (failure != null) detail += " 还有 " + (rows.Length - saved) + " 条待处理。" + failure;
            Receipt("f12", draft, failure == null ? "saved" : "partial", detail);
            try { ExportLocal(); }
            catch (Exception ex) { return detail + " 网页快照刷新失败：" + ex.GetBaseException().Message; }
            return detail;
        }

        internal static string ValidateText(JObject row, TranslationWorkItem item, bool allowOriginal)
        {
            if (row["text"]?.Type != JTokenType.String) throw new InvalidOperationException("译文必须是文本。");
            var value = (string)row["text"];
            if (value.Length > 4000) throw new InvalidOperationException("单条译文最多 4000 字符。");
            if (allowOriginal) value = TranslationSafety.PreserveOuterFormatting(item.Source, value);
            return string.IsNullOrWhiteSpace(value) ? "" : TranslationSafety.Validate(value, item, allowOriginal);
        }

        private static void CheckServerDraft(JObject draft)
        {
            if (_serverCatalog == null || !ServerWorkbenchClient.SupportsWebContributions || !ServerWorkbenchClient.CanUseWorkbench ||
                !ServerTranslationFeature.IsCurrentConnection(_generation) || (string)draft["connection"] != _connectionId ||
                (string)draft["revision"] != _serverExport)
                throw new InvalidOperationException("草稿不属于当前服务器会话，请重新导出并核对。");
        }

        private static string PreviewServer()
        {
            _preview = null;
            var draft = LoadDraft("server", 30);
            CheckServerDraft(draft);
            ServerEntries(draft);
            _preview = draft;
            _scroll = Vector2.zero;
            return "已载入投稿预览。";
        }

        private static JArray ServerEntries(JObject draft)
        {
            var current = _serverCatalog.Entries.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var result = new JArray();
            foreach (JObject row in (JArray)draft["edits"])
            {
                var id = (string)row["id"];
                if (string.IsNullOrEmpty(id) || !ids.Add(id) || !current.TryGetValue(id, out var entry) ||
                    entry.English != (string)row["source"])
                    throw new InvalidOperationException("投稿包含重复、未知或变化的原文。");
                var item = TranslationWorkItem.ForServerText(id, entry.English, entry.Domain);
                var value = ValidateText(row, item, false);
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("投稿不能清空服务端译文。");
                result.Add(new JObject { ["id"] = id, ["source"] = entry.English, ["translation"] = value });
            }
            return result;
        }

        private static async Task<string> SubmitServerAsync(JObject preview)
        {
            var draft = LoadDraft("server", 30);
            if (!JToken.DeepEquals(preview, draft)) throw new InvalidOperationException("草稿已变化，请重新预览。");
            CheckServerDraft(draft);
            var entries = ServerEntries(draft);
            Receipt("server", draft, "unconfirmed", "已发起投稿，最终结果尚未确认。请勿直接重试。");
            JObject result;
            try
            {
                result = await ServerWorkbenchClient.SubmitWebContributionAsync(_serverCatalog.Revision, entries,
                    (string)draft["id"]).ConfigureAwait(false);
            }
            catch (ServerWorkbenchException)
            {
                Receipt("server", draft, "rejected", "服务端拒绝投稿。请检查游戏内提示、会话和投稿冷却。");
                throw;
            }
            return SaveServerReceipt(draft, result);
        }

        private static async Task RefreshServerReceiptAsync(int generation, string connection, ManualLogSource log)
        {
            try
            {
                var receipt = Files.Read("server", "receipt");
                var state = (string)receipt?["state"];
                if (state != "review" && state != "unconfirmed") return;
                var draft = Files.Read("server", "draft");
                var id = (string)draft?["id"];
                if ((string)draft?["connection"] != connection || (string)receipt["id"] != id) return;
                var result = await ServerWorkbenchClient.GetContributionReceiptAsync(id).ConfigureAwait(false);
                if ((bool?)result["found"] == false) return;
                await MainThreadDispatcher.InvokeAsync(() =>
                {
                    if (ServerTranslationFeature.IsCurrentConnection(generation) && ServerWorkbenchClient.CanUseWorkbench &&
                        (string)Files.Read("server", "draft")?["id"] == id &&
                        JToken.DeepEquals(receipt, Files.Read("server", "receipt")))
                        SaveServerReceipt(draft, result);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log?.LogDebug("读取网页投稿回执未完成：" + ex.GetBaseException().Message); }
        }

        private static string SaveServerReceipt(JObject draft, JObject result)
        {
            var summary = ServerWorkbenchClient.DescribeContributionReceipt(result, out var detail, out var pending);
            var previous = Files.Read("server", "receipt");
            if ((string)previous?["id"] != (string)draft["id"] || (string)previous?["detail"] != detail)
                Receipt("server", draft, pending ? "review" : "reviewed", detail);
            return summary;
        }
    }
}
