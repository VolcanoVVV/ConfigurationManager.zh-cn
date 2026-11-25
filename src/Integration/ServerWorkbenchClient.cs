using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal sealed class ServerWorkbenchEntry
    {
        internal string Id;
        internal string Domain;
        internal string Origin;
        internal string SourceGroup;
        internal string SourceGroupName;
        internal string English;
        internal string Chinese;
    }

    internal sealed class ServerWorkbenchSourceGroup
    {
        internal string Id;
        internal string Name;
        internal string Kind;
        internal int EntryCount;
    }

    internal sealed class ServerWorkbenchFile
    {
        internal string Path;
        internal string Kind;
        internal bool Enabled;
        internal int EntryCount;
        internal bool BaselineKnown;
        internal int ChangedCount;
        internal bool RequiresRestart;
    }

    internal sealed class ServerWorkbenchSnapshot
    {
        internal string Revision;
        internal string GeneratedAt;
        internal bool RestartRequired;
        internal readonly List<ServerWorkbenchEntry> Entries = new List<ServerWorkbenchEntry>();
        internal readonly List<ServerWorkbenchFile> Files = new List<ServerWorkbenchFile>();
        internal readonly List<ServerWorkbenchSourceGroup> SourceGroups = new List<ServerWorkbenchSourceGroup>();
        internal readonly Dictionary<string, int> EntryCountsBySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal int CountEntries(IEnumerable<string> sourceGroups)
        {
            var count = 0;
            foreach (var group in sourceGroups)
                if (EntryCountsBySource.TryGetValue(group, out var size)) count += size;
            return count;
        }

        internal int CountOrigin(string origin)
        {
            return Entries.Count(entry => string.Equals(entry.Origin, origin, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class ServerSaveResult
    {
        internal int Saved;
        internal bool LiveApplied;
        internal bool RestartRequired;
    }

    internal sealed class ServerProviderState
    {
        internal string AiName, AiModel, AiEndpointHost, MachineName, MachineEndpointHost, ConfigFile, SecretsFile, GlossarySource;
        internal bool AiConfigured, MachineConfigured, AiKeyRequired, MachineKeyRequired, MachineAppIdRequired;
        internal JObject AiSettings, MachineSettings;
        internal JArray Profiles, MachineProfiles;
        internal string Revision;
    }

    internal sealed class ServerContributionEntry
    {
        internal string SubmissionId, Mode, Id, Source, Translation, Player, SubmittedAt;
        internal bool Stale;
    }

    internal sealed class ServerJobState
    {
        internal string JobId, State, Mode, Error;
        internal bool Contribution;
        internal int Total, Completed, Saved, Rejected;
        internal JObject Diagnostics;
    }

    internal sealed class ServerReviewEntry
    {
        internal string Mode, Id, Domain, Source, PreviousSource, SourceGroup, Translation, SavedAt;
        internal bool Stale;
    }

    internal sealed class ServerReviewPage
    {
        internal int Page = 1, PageSize = 40, Total;
        internal string[] Groups = Array.Empty<string>();
        internal readonly List<ServerReviewEntry> Entries = new List<ServerReviewEntry>();
    }

    internal sealed class ServerWorkbenchException : InvalidOperationException
    {
        internal bool Unauthorized { get; private set; }
        internal bool JobMissing { get; private set; }

        internal ServerWorkbenchException(string message, bool unauthorized, bool jobMissing = false) : base(message)
        {
            Unauthorized = unauthorized;
            JobMissing = jobMissing;
        }
    }

    internal static class ServerWorkbenchClient
    {
        internal const string CatalogRoute = "/simpletranslator/v1/workbench/catalog";
        internal const string FileSelectionRoute = "/simpletranslator/v1/workbench/files";
        internal const string BaselineRoute = "/simpletranslator/v1/workbench/baseline";
        internal const string SaveRoute = "/simpletranslator/v1/workbench/save";
        internal const string ReloadRoute = "/simpletranslator/v1/workbench/reload";
        internal const string PullRoute = "/simpletranslator/v1/workbench/pull";
        internal const string AccessRoute = "/simpletranslator/v1/workbench/access";
        internal const string ProviderStatusRoute = "/simpletranslator/v1/workbench/provider-status";
        internal const string ProviderTestRoute = "/simpletranslator/v1/workbench/provider-test";
        internal const string ContributionCatalogRoute = "/simpletranslator/v1/workbench/contribution-catalog";
        internal const string ContributionStartRoute = "/simpletranslator/v1/workbench/contribution-start";
        internal const string ContributionPendingRoute = "/simpletranslator/v1/workbench/contribution-pending";
        internal const string ContributionDecisionRoute = "/simpletranslator/v1/workbench/contribution-decision";
        internal const string JobStartRoute = "/simpletranslator/v1/workbench/job-start";
        internal const string JobStatusRoute = "/simpletranslator/v1/workbench/job-status";
        internal const string JobCancelRoute = "/simpletranslator/v1/workbench/job-cancel";
        internal const string ReviewRoute = "/simpletranslator/v1/workbench/review";
        internal const string ReviewSaveRoute = "/simpletranslator/v1/workbench/review-save";
        private static string _adminKey = string.Empty;
        private static int _adminKeyRevision;
        internal static string AdminKey
        {
            get { lock (Sync) return _adminKey; }
            set
            {
                var key = value ?? string.Empty;
                lock (Sync)
                {
                    if (_adminKey == key) return;
                    _adminKey = key;
                    _adminKeyRevision++;
                    if (_isAdmin) _snapshot = null;
                    _isAdmin = false;
                    _verifiedKey = null;
                }
            }
        }
        private static volatile string _jobId = string.Empty;
        internal static bool HasJobId => !string.IsNullOrWhiteSpace(_jobId);

        private static string ServerJobPrefix() => string.IsNullOrEmpty(ServerTranslationFeature.ConnectionIdentity)
            ? null : TranslationSafety.Hash(ServerTranslationFeature.ConnectionIdentity) + ":";

        internal static string RecoverableJobId()
        {
            var prefix = ServerJobPrefix();
            var value = ConfigurationManagerZhCnPlugin.Instance?.PendingServerJob?.Value;
            if (prefix == null || value?.StartsWith(prefix, StringComparison.Ordinal) != true) return null;
            var id = value.Substring(prefix.Length);
            return Guid.TryParseExact(id, "N", out _) ? id : null;
        }

        internal static void ClearRecoverableJob()
        {
            var entry = ConfigurationManagerZhCnPlugin.Instance?.PendingServerJob;
            if (entry != null && RecoverableJobId() == _jobId) entry.Value = string.Empty;
        }

        private static readonly object Sync = new object();
        private static readonly AsyncLocal<Operation> ActiveOperation = new AsyncLocal<Operation>();
        private static int _resetVersion;
        private static CancellationTokenSource _sessionCancellation = new CancellationTokenSource();
        internal static int RequestTimeoutMilliseconds = 15000;

        private sealed class Operation : IDisposable
        {
            internal readonly int Generation = ServerTranslationFeature.Generation;
            private readonly string Connection = ServerTranslationFeature.ConnectionIdentity;
            internal readonly int ResetVersion = _resetVersion;
            internal readonly CancellationTokenSource Cancellation;
            internal Operation(CancellationToken token)
            {
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _sessionCancellation.Token);
            }
            internal bool IsCurrent => !Cancellation.IsCancellationRequested && ResetVersion == _resetVersion &&
                Connection == ServerTranslationFeature.ConnectionIdentity && ServerTranslationFeature.IsCurrentConnection(Generation);
            internal void Check()
            {
                if (!IsCurrent) throw new OperationCanceledException("服务端操作已取消或连接已切换，已忽略迟到结果。", Cancellation.Token);
            }
            public void Dispose() { ActiveOperation.Value = null; Cancellation.Dispose(); }
        }

        private sealed class NestedOperation : IDisposable { public void Dispose() { } }
        internal static IDisposable BeginOperation(CancellationToken token = default(CancellationToken))
        {
            lock (Sync)
            {
                if (ActiveOperation.Value != null) { ActiveOperation.Value.Check(); return new NestedOperation(); }
                var operation = new Operation(token);
                ActiveOperation.Value = operation;
                try { operation.Check(); return operation; }
                catch { operation.Dispose(); throw; }
            }
        }

        internal static void CheckOperation() { ActiveOperation.Value?.Check(); }
        internal static void PublishOperationState(Action publish)
        {
            lock (Sync) { CheckOperation(); publish(); }
        }

        private static async Task<string> AwaitResponseAsync(Task<string> pending)
        {
            var operation = ActiveOperation.Value;
            operation.Check();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation.Token))
            {
                var delay = Task.Delay(RequestTimeoutMilliseconds, deadline.Token);
                if (await Task.WhenAny(pending, delay).ConfigureAwait(false) != pending)
                {
                    _ = pending.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    operation.Check();
                    throw new TimeoutException("服务端请求等待超时；写入或付费操作的执行结果可能未知，请核对服务端记录，勿直接重复提交。");
                }
                deadline.Cancel();
                var response = await pending.ConfigureAwait(false);
                operation.Check();
                return response;
            }
        }
        private static ServerWorkbenchSnapshot _snapshot;
        private static bool _refreshing;
        private static string _error;
        private static string _verifiedKey;
        private static bool _isAdmin;
        private static DateTime _nextPullUtc;
        internal static string AdminValidationState { get { lock (Sync) return string.IsNullOrWhiteSpace(AdminKey) ? "empty" : _verifiedKey != AdminKey ? "unverified" : _isAdmin ? "verified" : "rejected"; } }
        internal static bool IsAdmin { get { lock (Sync) return _isAdmin && _verifiedKey == AdminKey; } }
        internal static bool CanUseWorkbench =>
            ServerTranslationFeature.Capabilities?.FikaDetected != true ||
            (ServerTranslationFeature.HasIdentifiableConnection && !string.IsNullOrWhiteSpace(AdminKey) && IsAdmin);
        internal const string WorkbenchLockedMessage = "当前服务器已启用 Fika，请在 F12 验证服务端管理员密钥后使用服务端文本工作台。";
        internal static int PullRemainingSeconds { get { lock (Sync) return IsAdmin ? 0 : Math.Max(0, (int)Math.Ceiling((_nextPullUtc - DateTime.UtcNow).TotalSeconds)); } }

        internal static ServerWorkbenchSnapshot Snapshot
        {
            get { lock (Sync) return _snapshot; }
        }

        internal static bool Refreshing
        {
            get { lock (Sync) return _refreshing; }
        }

        internal static string Error
        {
            get { lock (Sync) return _error; }
        }

        internal static void Reset()
        {
            CancellationTokenSource previous;
            lock (Sync)
            {
                _resetVersion++;
                previous = _sessionCancellation;
                _sessionCancellation = new CancellationTokenSource();
                _snapshot = null;
                _refreshing = false;
                _error = null;
                _verifiedKey = null;
                _isAdmin = false;
                _nextPullUtc = DateTime.MinValue;
                _jobId = string.Empty;
            }
            previous.Cancel();
            previous.Dispose();
        }

        internal static async Task<ServerWorkbenchSnapshot> RefreshAsync()
        {
            using (BeginOperation())
            {
                lock (Sync)
                {
                    if (_refreshing) return _snapshot;
                    _refreshing = true;
                    _error = null;
                }
                try
                {
                    await RefreshAccessAsync().ConfigureAwait(false);
                    if (!CanUseWorkbench)
                    {
                        lock (Sync) { CheckOperation(); _snapshot = null; }
                        return null;
                    }
                    if (!IsAdmin)
                    {
                        if (!ServerTranslationFeature.Capabilities.SupportsFeature("contribution-review"))
                        {
                            lock (Sync) { CheckOperation(); _snapshot = null; }
                            return null;
                        }
                        var contribution = await PostCheckedAsync(ContributionCatalogRoute, new JObject()).ConfigureAwait(false);
                        var playerSnapshot = ParseSnapshot(contribution.ToString(Formatting.None));
                        lock (Sync) { CheckOperation(); _snapshot = playerSnapshot; }
                        return playerSnapshot;
                    }
                    var modern = ServerTranslationFeature.Capabilities.SupportsFeature("admin-access");
                    var json = modern
                        ? (await PostCheckedAsync(CatalogRoute, Authorized(new JObject())).ConfigureAwait(false)).ToString(Formatting.None)
                        : await AwaitResponseAsync(ServerTranslationFeature.GetJsonAsync(CatalogRoute)).ConfigureAwait(false);
                    var snapshot = ParseSnapshot(json);
                    lock (Sync) { CheckOperation(); _snapshot = snapshot; }
                    return snapshot;
                }
                catch (Exception ex)
                {
                    lock (Sync) { if (ActiveOperation.Value.IsCurrent) _error = ex.Message; }
                    throw;
                }
                finally
                {
                    lock (Sync) { if (ActiveOperation.Value.ResetVersion == _resetVersion) _refreshing = false; }
                }

            }
        }

        private static async Task RefreshAccessAsync()
        {
            using (BeginOperation())
            {
                string key;
                int keyRevision;
                lock (Sync) { key = _adminKey; keyRevision = _adminKeyRevision; }
                var capabilities = ServerTranslationFeature.Capabilities;
                var admin = !capabilities.WriteAuthRequired && !capabilities.FikaDetected;
                if (capabilities.SupportsFeature("admin-access") && (!capabilities.FikaDetected ||
                    (!string.IsNullOrWhiteSpace(key) && ServerTranslationFeature.HasIdentifiableConnection)))
                {
                    var response = await PostCheckedAsync(AccessRoute, new JObject { ["adminKey"] = key ?? string.Empty }).ConfigureAwait(false);
                    admin = (bool?)response["isAdmin"] == true;
                }
                lock (Sync)
                {
                    CheckOperation();
                    if (keyRevision != _adminKeyRevision) return;
                    _verifiedKey = key;
                    _isAdmin = admin;
                }

            }
        }

        internal static async Task<string> SetFileEnabledAsync(string path, bool enabled)
        {
            using (BeginOperation())
            {
                var request = Authorized(new JObject { ["path"] = path, ["enabled"] = enabled });
                var response = await PostCheckedAsync(FileSelectionRoute, request).ConfigureAwait(false);
                var message = (bool?)response["restartRequired"] == true
                    ? "文件开关已保存；当前运行时无法完整热更新，请重启服务端。"
                    : (bool?)response["changed"] == true ? "文件开关已保存。" : "文件开关未发生变化。";
                var sync = ServerTranslationFeature.Capabilities.SupportsFeature("pull-sync") ? (Func<Task<string>>)PullAsync : null;
                return await WithFollowupAsync(message, RefreshAsync, sync).ConfigureAwait(false);

            }
        }

        internal static async Task<string> UpdateBaselineAsync()
        {
            using (BeginOperation())
            {
                var response = await PostCheckedAsync(BaselineRoute, Authorized(new JObject())).ConfigureAwait(false);
                await RefreshAsync().ConfigureAwait(false);
                return "已更新 " + ((int?)response["files"] ?? 0) + " 个手动文件的英文原文基准。";

            }
        }

        internal static async Task<string[]> GetModelsAsync(string apiKey, string profileId, string revision, bool refresh = false, JObject options = null)
        {
            using (BeginOperation())
            {
                if (!ServerTranslationFeature.Capabilities.SupportsFeature("model-discovery"))
                    throw new InvalidOperationException("服务端未提供模型查询，请手动输入管理员批准的模型 ID。");
                var response = await PostCheckedAsync("/simpletranslator/v1/workbench/models", Authorized(new JObject
                    { ["apiKey"] = apiKey ?? "", ["profileId"] = profileId, ["providerRevision"] = revision, ["refreshModels"] = refresh, ["options"] = options })).ConfigureAwait(false);
                return (response["models"] as JArray ?? new JArray()).Values<string>().ToArray();

            }
        }

        internal static async Task<ServerProviderState> GetProviderStatusAsync()
        {
            using (BeginOperation())
            {
                var response = await PostCheckedAsync(ProviderStatusRoute, Authorized(new JObject())).ConfigureAwait(false);
                return new ServerProviderState
                {
                    AiName = (string)response["ai"]?["provider"] ?? string.Empty,
                    AiSettings = response["ai"]?["settings"] as JObject,
                    Revision = (string)response["providerRevision"] ?? string.Empty,
                    Profiles = response["profiles"] as JArray ?? new JArray(),
                    MachineProfiles = response["machineProfiles"] as JArray ?? new JArray(),
                    MachineSettings = response["machine"]?["settings"] as JObject,
                    AiModel = (string)response["ai"]?["model"] ?? string.Empty,
                    AiEndpointHost = (string)response["ai"]?["endpointHost"] ?? string.Empty,
                    AiConfigured = (bool?)response["ai"]?["configured"] == true,
                    AiKeyRequired = (bool?)response["ai"]?["keyRequired"] != false,
                    MachineName = (string)response["machine"]?["provider"] ?? string.Empty,
                    MachineEndpointHost = (string)response["machine"]?["endpointHost"] ?? string.Empty,
                    MachineConfigured = (bool?)response["machine"]?["configured"] == true,
                    MachineKeyRequired = (bool?)response["machine"]?["keyRequired"] != false,
                    MachineAppIdRequired = (bool?)response["machine"]?["appIdRequired"] == true,
                    ConfigFile = (string)response["configFile"] ?? string.Empty,
                    SecretsFile = (string)response["secretsFile"] ?? string.Empty,
                    GlossarySource = (string)response["glossarySource"] ?? "server"
                };

            }
        }

        internal static async Task<ServerJobState> StartJobAsync(string mode, string revision, IEnumerable<string> sourceGroups,
            bool test, string apiKey, string appId, string profileId = "default", JObject options = null, IEnumerable<string> entryIds = null, string providerRevision = "", JObject machineOptions = null)
        {
            using (BeginOperation())
            {
                if (!string.IsNullOrWhiteSpace(AdminKey) && !IsAdmin)
                    throw new InvalidOperationException("管理员密钥尚未通过验证。请校验正确密钥，或清空后以玩家身份提交待审译文。");
                var request = Authorized(new JObject { ["mode"] = mode, ["revision"] = revision ?? string.Empty,
                    ["sourceGroups"] = new JArray(sourceGroups ?? Enumerable.Empty<string>()),
                    ["apiKey"] = apiKey ?? string.Empty, ["appId"] = appId ?? string.Empty });
                if (ServerTranslationFeature.Capabilities.SupportsFeature("ai-task-options"))
                {
                    request["profileId"] = profileId;
                    request["providerRevision"] = providerRevision;
                    request["options"] = options;
                    if (ServerTranslationFeature.Capabilities.SupportsFeature("provider-catalog")) request["machineOptions"] = machineOptions;
                    request["entryIds"] = new JArray(entryIds ?? Enumerable.Empty<string>());
                }
                var route = test ? ProviderTestRoute : IsAdmin ? JobStartRoute : ContributionStartRoute;
                lock (Sync)
                {
                    CheckOperation();
                    _jobId = string.Empty;
                    if (ServerTranslationFeature.Capabilities.SupportsFeature("job-request-id"))
                    {
                        _jobId = Guid.NewGuid().ToString("N");
                        request["requestId"] = _jobId;
                        var entry = ConfigurationManagerZhCnPlugin.Instance?.PendingServerJob;
                        var prefix = ServerJobPrefix();
                        if (prefix != null && entry != null) entry.Value = prefix + _jobId;
                    }
                }
                var started = await PostCheckedAsync(route, request).ConfigureAwait(false);
                lock (Sync) { CheckOperation(); _jobId = (string)started["jobId"] ?? string.Empty; }
                return await GetJobStatusAsync().ConfigureAwait(false);

            }
        }

        internal static async Task<ServerJobState> GetJobStatusAsync(string recoverId = null)
        {
            using (BeginOperation())
            {
                var requested = recoverId ?? _jobId;
                var response = await PostCheckedAsync(JobStatusRoute, Authorized(new JObject { ["jobId"] = requested })).ConfigureAwait(false);
                if (recoverId != null)
                {
                    if ((string)response["jobId"] != recoverId) throw new InvalidOperationException("服务端找不到此任务。");
                    lock (Sync) { CheckOperation(); _jobId = recoverId; }
                }
                return ParseJob(response);

            }
        }

        private static ServerJobState ParseJob(JObject response) => new ServerJobState
        {
            JobId = (string)response["jobId"] ?? string.Empty,
            State = (string)response["state"] ?? "idle",
            Mode = (string)response["mode"] ?? string.Empty,
            Contribution = (bool?)response["contribution"] ?? !IsAdmin,
            Error = (string)response["error"] ?? string.Empty,
            Total = (int?)response["total"] ?? 0,
            Completed = (int?)response["completed"] ?? 0,
            Saved = (int?)response["saved"] ?? 0,
            Rejected = (int?)response["rejected"] ?? 0,
            Diagnostics = response["diagnostics"] as JObject
        };

        internal static async Task<List<ServerJobState>> GetRecentJobsAsync()
        {
            var result = await PostCheckedAsync(JobStatusRoute, Authorized(new JObject { ["history"] = true })).ConfigureAwait(false);
            return ((JArray)result["entries"] ?? new JArray()).OfType<JObject>().Select(ParseJob).ToList();
        }

        internal static async Task<string> CancelJobAsync(string recoverId = null)
        {
            using (BeginOperation())
            {
                var id = recoverId ?? _jobId;
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("等待服务端任务 ID。");
                await PostCheckedAsync(JobCancelRoute, Authorized(new JObject { ["jobId"] = id,
                    ["stopPending"] = ServerTranslationFeature.Capabilities.SupportsFeature("job-request-id") })).ConfigureAwait(false);
                if (recoverId != null) lock (Sync) { CheckOperation(); _jobId = recoverId; }
                return "已向服务端发送取消请求。";

            }
        }

        internal static async Task<List<ServerContributionEntry>> GetPendingContributionsAsync()
        {
            using (BeginOperation())
            {
                var response = await PostCheckedAsync(ContributionPendingRoute, Authorized(new JObject())).ConfigureAwait(false);
                var entries = new List<ServerContributionEntry>();
                var rows = response["entries"] as JArray;
                if (rows != null)
                    foreach (var row in rows.OfType<JObject>())
                        entries.Add(new ServerContributionEntry
                        {
                            SubmissionId = (string)row["submissionId"] ?? string.Empty,
                            Mode = (string)row["mode"] ?? string.Empty,
                            Id = (string)row["id"] ?? string.Empty,
                            Source = (string)row["source"] ?? string.Empty,
                            Translation = (string)row["translation"] ?? string.Empty,
                            Player = (string)row["player"] ?? string.Empty,
                            SubmittedAt = (string)row["submittedAt"] ?? string.Empty,
                            Stale = (bool?)row["stale"] == true
                        });
                return entries;

            }
        }

        internal static async Task<string> DecideContributionAsync(ServerContributionEntry entry,
            bool approve, string translation)
        {
            using (BeginOperation())
            {
                await PostCheckedAsync(ContributionDecisionRoute, Authorized(new JObject
                {
                    ["submissionId"] = entry.SubmissionId, ["approve"] = approve,
                    ["translation"] = translation ?? string.Empty
                })).ConfigureAwait(false);
                return await WithFollowupAsync(approve ? "服务端已批准候选译文。" : "服务端已拒绝候选译文。",
                    RefreshAsync, approve ? (Func<Task<string>>)PullAsync : null).ConfigureAwait(false);

            }
        }

        internal static async Task<ServerReviewPage> GetReviewAsync(int page = 1, string query = "", string layer = "",
            string sourceGroup = "", bool changedOnly = false)
        {
            using (BeginOperation())
            {
                var request = new JObject();
                if (ServerTranslationFeature.Capabilities.SupportsFeature("review-catalog"))
                {
                    request["page"] = page;
                    request["query"] = query;
                    request["layer"] = layer;
                    request["sourceGroup"] = sourceGroup;
                    request["changedOnly"] = changedOnly;
                }
                var response = await PostCheckedAsync(ReviewRoute, Authorized(request)).ConfigureAwait(false);
                var rows = response["entries"] as JArray;
                var result = new ServerReviewPage
                {
                    Page = (int?)response["page"] ?? 1, PageSize = (int?)response["pageSize"] ?? Math.Max(1, rows?.Count ?? 0),
                    Total = (int?)response["total"] ?? rows?.Count ?? 0,
                    Groups = (response["groups"] as JArray ?? new JArray()).Values<string>().ToArray()
                };
                if (rows != null)
                    foreach (var row in rows.OfType<JObject>())
                        result.Entries.Add(new ServerReviewEntry { Mode = (string)row["mode"], Id = (string)row["id"],
                            Domain = (string)row["domain"], Source = (string)row["source"],
                            PreviousSource = (string)row["previousSource"], SourceGroup = (string)row["sourceGroup"],
                            Translation = (string)row["translation"], Stale = (bool?)row["stale"] == true,
                            SavedAt = (string)row["savedAt"] });
                return result;

            }
        }

        internal static async Task<string> SaveReviewAsync(ServerReviewEntry entry, string translation)
        {
            using (BeginOperation())
            {
                var result = await PostCheckedAsync(ReviewSaveRoute, Authorized(new JObject { ["mode"] = entry.Mode,
                    ["id"] = entry.Id, ["source"] = entry.Source, ["translation"] = translation,
                    ["expectedTranslation"] = entry.Translation })).ConfigureAwait(false);
                var message = (bool?)result["restartRequired"] == true ? "服务端已保存校对译文，部分文本需重启生效。" : "服务端已保存校对译文。";
                return await WithFollowupAsync(message, null, PullAsync).ConfigureAwait(false);

            }
        }

        internal static async Task<ServerSaveResult> SaveBatchAsync(string mode, string revision, string profile,
            IList<ServerWorkbenchEntry> entries, IDictionary<string, string> translations)
        {
            using (BeginOperation())
            {
                var values = new JArray();
                foreach (var entry in entries)
                {
                    string translation;
                    if (!translations.TryGetValue(entry.Id, out translation))
                        throw new InvalidOperationException("翻译结果缺少服务端词条：" + entry.Id);
                    values.Add(new JObject
                    {
                        ["id"] = entry.Id,
                        ["source"] = entry.English,
                        ["translation"] = translation
                    });
                }
                var response = await PostCheckedAsync(SaveRoute, Authorized(new JObject
                {
                    ["mode"] = mode,
                    ["revision"] = revision,
                    ["profile"] = profile ?? string.Empty,
                    ["entries"] = values
                })).ConfigureAwait(false);
                return new ServerSaveResult
                {
                    Saved = (int?)response["saved"] ?? 0,
                    LiveApplied = (bool?)response["liveApplied"] == true,
                    RestartRequired = (bool?)response["restartRequired"] == true
                };

            }
        }

        internal static async Task<string> SubmitManualAsync(TranslationExchange pack, Dictionary<string, string> values, bool keepOriginal)
        {
            using (BeginOperation())
            {
                var entries = new JArray(pack.Server.Where(item => values.ContainsKey(item.Id)).Select(item => new JObject
                { ["id"] = item.Id, ["source"] = item.English, ["translation"] = values[item.Id],
                    ["keepOriginal"] = keepOriginal && string.Equals(item.English.Trim(), values[item.Id], StringComparison.OrdinalIgnoreCase) }));
                var admin = IsAdmin;
                var request = Authorized(new JObject { ["mode"] = "manual", ["revision"] = pack.Revision, ["entries"] = entries });
                if (!admin && ServerTranslationFeature.Capabilities.SupportsFeature("contribution-receipts"))
                    request["submissionId"] = Guid.NewGuid().ToString("N");
                var result = await PostCheckedAsync("/simpletranslator/v1/workbench/manual-submit", request).ConfigureAwait(false);
                var message = (admin ? "服务端已保存人工译文 " : "已提交待审候选 ") + ((int?)result["saved"] ?? 0) + " 条。";
                if (!admin && result["results"] is JArray) message = DescribeContributionReceipt(result, out _, out _);
                return await WithFollowupAsync(message, RefreshAsync, admin ? (Func<Task<string>>)PullAsync : null).ConfigureAwait(false);

            }
        }

        internal static async Task<string> ReloadAsync()
        {
            using (BeginOperation())
            {
                var response = await PostCheckedAsync(ReloadRoute, Authorized(new JObject())).ConfigureAwait(false);
                return await WithFollowupAsync("服务端已重新读取，已应用 " + ((int?)response["applied"] ?? 0) + " 个键值。",
                    RefreshAsync, () => ApplyResponseAsync(response)).ConfigureAwait(false);

            }
        }

        internal static async Task<string> PullAsync()
        {
            using (BeginOperation())
            {
                var response = await PostCheckedAsync(PullRoute, Authorized(new JObject())).ConfigureAwait(false);
                lock (Sync) { CheckOperation(); _nextPullUtc = DateTime.UtcNow.AddSeconds((int?)response["retryAfterSeconds"] ?? 0); }
                return await ApplyResponseAsync(response).ConfigureAwait(false);

            }
        }

        private static async Task<string> ApplyResponseAsync(JObject response)
        {
            using (BeginOperation())
            {
                var operation = ActiveOperation.Value;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var obj = response["values"] as JObject;
                if (obj != null)
                    foreach (var property in obj.Properties())
                        if (property.Value.Type == JTokenType.String)
                            values[property.Name] = (string)property.Value;
                var removed = response["removedKeys"] as JArray;
                return (await ClientLocaleRefresher.ApplyCheckedAsync(values,
                    removed == null ? Enumerable.Empty<string>() : removed.Values<string>(),
                    (string)response["locale"] ?? "ch", () => operation.IsCurrent).ConfigureAwait(false)).Message;

            }
        }

        private static JObject Authorized(JObject request)
        {
            request["adminKey"] = AdminKey ?? string.Empty;
            return request;
        }

        internal static bool SupportsWebContributions
        {
            get
            {
                var capabilities = ServerTranslationFeature.Capabilities;
                return capabilities != null && ServerTranslationFeature.HasIdentifiableConnection &&
                    capabilities.SupportsFeature("known-profile-gateway") &&
                    capabilities.SupportsFeature("contribution-review") && capabilities.SupportsFeature("web-player-submit");
            }
        }

        internal static async Task<ServerWorkbenchSnapshot> ReadWebContributionCatalogAsync()
        {
            if (!SupportsWebContributions) throw new InvalidOperationException("当前连接不支持受保护的玩家投稿。");
            var result = await PostCheckedAsync(ContributionCatalogRoute, WebRequest(new JObject())).ConfigureAwait(false);
            return ParseSnapshot(result.ToString(Formatting.None));
        }

        internal static async Task<int> SubmitWebContributionAsync(string revision, JArray entries)
        {
            return (int?) (await SubmitWebContributionAsync(revision, entries, null).ConfigureAwait(false))["saved"] ?? 0;
        }

        internal static async Task<JObject> SubmitWebContributionAsync(string revision, JArray entries, string submissionId)
        {
            if (!SupportsWebContributions) throw new InvalidOperationException("当前连接不支持受保护的玩家投稿。");
            using (BeginOperation())
            {
                // The verified key stays in the game's server request; this route always queues drafts.
                if (ServerTranslationFeature.Capabilities.FikaDetected)
                {
                    RequireWorkbenchAccess();
                    await RefreshAccessAsync().ConfigureAwait(false);
                }
                var payload = new JObject { ["revision"] = revision, ["entries"] = entries };
                if (submissionId != null && ServerTranslationFeature.Capabilities.SupportsFeature("contribution-receipts"))
                    payload["submissionId"] = submissionId;
                return await PostCheckedAsync("/simpletranslator/v1/workbench/web-player-submit", WebRequest(payload)).ConfigureAwait(false);
            }
        }

        internal static Task<JObject> GetContributionReceiptAsync(string submissionId)
        {
            return PostCheckedAsync("/simpletranslator/v1/workbench/contribution-receipt",
                WebRequest(new JObject { ["submissionId"] = submissionId }));
        }

        internal static string DescribeContributionReceipt(JObject result, out string detail, out bool awaitingReview)
        {
            var states = result["results"] as JArray;
            if (states == null)
            {
                detail = "服务端接收 " + ((int?)result["saved"] ?? 0) + " 条候选译文，等待审核。";
                awaitingReview = true;
                return detail;
            }
            var pending = states.Count(row => (string)row["state"] == "pending");
            var approved = states.Count(row => (string)row["state"] == "approved");
            var rejected = states.Count(row => (string)row["state"] == "rejected");
            awaitingReview = pending > 0;
            var summary = "待审 " + pending + " 条 · 已批准 " + approved + " 条 · 已拒绝 " + rejected +
                " 条 · 未入队或已失效 " + (states.Count - pending - approved - rejected) + " 条。";
            detail = summary + "\n" + string.Join("\n", states.Select(row =>
            {
                var status = (string)row["state"];
                var label = status == "pending" ? "待审" : status == "approved" ? "已批准" :
                    status == "rejected" ? "已拒绝" : status == "stale" ? "已失效" : "未入队";
                var reason = (string)row["reason"];
                return (string)row["id"] + " · " + label + (string.IsNullOrEmpty(reason) ? "" : " · " + reason);
            }));
            return summary;
        }

        private static JObject WebRequest(JObject request)
        {
            var capabilities = ServerTranslationFeature.Capabilities;
            return IsAdmin && capabilities?.SupportsFeature("fika-workbench-policy") == true ? Authorized(request) : request;
        }

        internal static async Task<string> WithFollowupAsync(string committed, Func<Task> refresh, Func<Task<string>> sync = null)
        {
            try
            {
                if (sync != null) committed += " " + await sync().ConfigureAwait(false);
                if (refresh != null) await refresh().ConfigureAwait(false);
            }
            catch (Exception ex) { return committed + " 客户端刷新或同步未完成：" + ex.GetBaseException().Message; }
            return committed;
        }

        private static void RequireWorkbenchAccess()
        {
            if (!CanUseWorkbench) throw new ServerWorkbenchException(WorkbenchLockedMessage, true);
        }

        private static async Task<JObject> PostCheckedAsync(string route, JObject request)
        {
            using (BeginOperation())
            {
                if (route != AccessRoute) RequireWorkbenchAccess();
                var json = await AwaitResponseAsync(ServerTranslationFeature.PostJsonAsync(route, request.ToString(Formatting.None))).ConfigureAwait(false);
                var response = JObject.Parse(json);
                if ((bool?)response["rateLimited"] == true)
                {
                    var remaining = Math.Max(0, (int?)response["retryAfterSeconds"] ?? 0);
                    if (route == PullRoute && remaining > 0)
                    {
                        lock (Sync) { CheckOperation(); _nextPullUtc = DateTime.UtcNow.AddSeconds(remaining); }
                        throw new ServerWorkbenchException("拉取仍在冷却中，请等待 " + remaining + " 秒。", false);
                    }
                    throw new ServerWorkbenchException((string)response["error"] ?? "请求过于频繁，请稍后重试。", false);
                }
                // Catalog is a plain snapshot; other operations return an explicit ok flag.
                if (route == CatalogRoute && response["revision"] != null && response["ok"] == null) return response;
                if ((bool?)response["ok"] != true)
                {
                    if ((bool?)response["unauthorized"] == true)
                        lock (Sync) { CheckOperation(); _isAdmin = false; _snapshot = null; }
                    throw new ServerWorkbenchException((string)response["error"] ?? "SimpleTranslatorCS 拒绝了请求。",
                        (bool?)response["unauthorized"] == true, (bool?)response["jobMissing"] == true);
                }
                return response;

            }
        }

        internal static ServerWorkbenchSnapshot ParseSnapshot(string json)
        {
            var root = JObject.Parse(json);
            if ((bool?)root["ok"] == false)
                throw new InvalidOperationException((string)root["error"] ?? "服务端目录读取失败。");
            var revision = (string)root["revision"];
            if (string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("服务端目录缺少修订号。");
            var snapshot = new ServerWorkbenchSnapshot
            {
                Revision = revision,
                GeneratedAt = (string)root["generatedAt"] ?? string.Empty,
                RestartRequired = (bool?)root["restartRequired"] == true
            };
            var entries = root["entries"] as JArray;
            if (entries == null) throw new InvalidOperationException("服务端目录缺少 entries。");
            foreach (var token in entries.OfType<JObject>())
            {
                var id = (string)token["id"];
                var english = (string)token["english"];
                var origin = (string)token["origin"];
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(english)
                    || (origin != "base" && origin != "mod"))
                    continue;
                var group = (string)token["sourceGroup"] ?? (origin == "base" ? "base-game" : "unassigned-mod");
                snapshot.Entries.Add(new ServerWorkbenchEntry
                {
                    Id = id,
                    Domain = (string)token["domain"] ?? "unknown",
                    Origin = origin,
                    SourceGroup = group,
                    SourceGroupName = (string)token["sourceGroupName"] ?? (origin == "base" ? "原版游戏 / 未归属" : "未归属 Mod"),
                    English = english,
                    Chinese = (string)token["chinese"] ?? string.Empty
                });
                snapshot.EntryCountsBySource.TryGetValue(group, out var count);
                snapshot.EntryCountsBySource[group] = count + 1;
            }
            var groups = root["sourceGroups"] as JArray;
            if (groups != null)
                foreach (var token in groups.OfType<JObject>())
                {
                    var id = (string)token["id"];
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    snapshot.SourceGroups.Add(new ServerWorkbenchSourceGroup
                    {
                        Id = id,
                        Name = (string)token["name"] ?? id,
                        Kind = (string)token["kind"] ?? "mod",
                        EntryCount = (int?)token["entryCount"] ?? 0
                    });
                }
            if (snapshot.SourceGroups.Count == 0)
                snapshot.SourceGroups.AddRange(snapshot.Entries.GroupBy(entry => entry.SourceGroup)
                    .Select(group => new ServerWorkbenchSourceGroup
                    {
                        Id = group.Key, Name = group.First().SourceGroupName,
                        Kind = group.First().Origin, EntryCount = group.Count()
                    }));
            var files = root["files"] as JArray;
            if (files != null)
                foreach (var token in files.OfType<JObject>())
                    snapshot.Files.Add(new ServerWorkbenchFile
                    {
                        Path = (string)token["path"] ?? string.Empty,
                        Kind = (string)token["kind"] ?? "manual",
                        Enabled = (bool?)token["enabled"] == true,
                        EntryCount = (int?)token["entryCount"] ?? 0,
                        BaselineKnown = (bool?)token["baselineKnown"] == true,
                        ChangedCount = (int?)token["changedCount"] ?? 0,
                        RequiresRestart = (bool?)token["requiresRestart"] == true
                    });
            return snapshot;
        }
    }
}
