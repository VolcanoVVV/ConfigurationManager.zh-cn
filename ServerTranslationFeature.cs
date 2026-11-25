using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    /// <summary>
    /// Detects the optional SimpleTranslatorCS server capability without taking a hard dependency
    /// on EFT or spt-common. This keeps ConfigurationManager.zh-cn usable in other games: when
    /// EFT/SPT or the compatible server component is absent, the server-text domain is not exposed
    /// and all existing F12 localization continues to work.
    /// </summary>
    internal static class ServerTranslationFeature
    {
        internal const string CapabilitiesRoute = "/simpletranslator/v1/capabilities";
        // The marker is in LocaleTable.Global, not LocaleTable.Menu.
        internal const string DiscoveryRoute = "/client/locale/ch";
        internal const string DiscoveryKey = "__SimpleTranslatorCS_WorkbenchProtocol";
        internal const string DomainTooltip =
            "服务端游戏文本：翻译 SPT 提供的物品名称、短名称与说明，任务名称、目标与说明，以及服务端声明支持的成就、商人等文本。\n" +
            "F12 设置汉化则处理 BepInEx 插件名称、分类、配置项名称、说明、枚举和自定义控件。\n" +
            "此板块仅在检测到兼容的 SimpleTranslatorCS 服务端组件时显示。";

        private static ManualLogSource _log;
        private static int _probeStarted;
        private static volatile ServerTranslationCapabilities _capabilities;
        private static bool _initialized;
        private static int _generation;
        private static readonly string ProbeChannel = "volcanofficial.server-text.v1";
        private static MethodInfo _getJsonAsync;
        private static MethodInfo _postJsonAsync;
        private static Func<string> _connectionIdentity;
        private static string _boundConnection;
        private static DateTime _nextConnectionCheck;
        private static DateTime _nextProbeRetry;
        private static int _probeFailures;
        private static bool _discoverySupported;
        internal static string DiscoveryStatus = "等待服务端连接";
        internal static bool CanDiscover { get { return _initialized && _discoverySupported; } }
        internal static bool DiscoveryRunning { get { return Volatile.Read(ref _probeStarted) == 1; } }

        internal static int Generation { get { return _generation; } }
        internal static bool IsAvailable { get { return _capabilities != null; } }
        internal static ServerTranslationCapabilities Capabilities { get { return _capabilities; } }
        internal static string ConnectionIdentity { get { return _boundConnection; } }
        internal static bool HasIdentifiableConnection
        {
            get
            {
                var identity = _boundConnection;
                if (string.IsNullOrEmpty(identity) || _connectionIdentity == null) return false;
                var parts = JArray.Parse(identity);
                return parts.Count == 2 && parts.All(part => part.Type != JTokenType.Null &&
                    !string.IsNullOrWhiteSpace(part.ToString())) && IsCurrentConnection(_generation);
            }
        }
        internal static bool IsCurrentConnection(int generation)
        {
            return _initialized && generation == _generation &&
                (_connectionIdentity == null || _boundConnection == _connectionIdentity());
        }

        internal static void Initialize(ManualLogSource log)
        {
            if (_initialized) return;
            _initialized = true;
            _probeFailures = 0;
            _nextProbeRetry = DateTime.MaxValue;
            DiscoveryStatus = "等待服务端连接";
            Interlocked.Increment(ref _generation);
            _log = log;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            TryStartProbe();
        }

        internal static void Dispose()
        {
            if (!_initialized) return;
            _initialized = false;
            Interlocked.Increment(ref _generation);
            ServerWorkbenchClient.Reset();
            TranslationPanel.ClearServerCredentials();
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            _initialized = false;
            _capabilities = null;
            _getJsonAsync = null;
            _postJsonAsync = null;
            _connectionIdentity = null;
            _boundConnection = null;
            _discoverySupported = false;
            Interlocked.Exchange(ref _probeStarted, 0);
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetType("SPT.Common.Http.RequestHandler", false) != null ||
                args.LoadedAssembly.GetType("SPTushonka.Common.Http.RequestHandler", false) != null ||
                args.LoadedAssembly.GetType("EFT.GameWorld", false) != null)
                TryStartProbe();
        }

        private static void TryStartProbe()
        {
            // Assembly-CSharp exists in many Unity games. Require an EFT type first so other games
            // never initialize SPT RequestHandler or emit irrelevant network traffic.
            if (FindLoadedType("EFT.GameWorld") == null) return;
            var requestType = ResolveRequestHandler(FindLoadedType);
            if (requestType == null) return;
            var getJsonAsync = requestType.GetMethod("GetJsonAsync", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            var postJsonAsync = requestType.GetMethod("PostJsonAsync", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(string) }, null);
            if (getJsonAsync == null || postJsonAsync == null) return;
            _discoverySupported = true;
            if (Interlocked.CompareExchange(ref _probeStarted, 1, 0) != 0) return;
            DiscoveryStatus = "正在检测服务端游戏文本组件…";
            var fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var host = requestType.GetField("Host", fields);
            var session = requestType.GetField("SessionId", fields);
            if (host != null && session != null)
            {
                _connectionIdentity = () => new JArray(host.GetValue(null), session.GetValue(null)).ToString(Newtonsoft.Json.Formatting.None);
                _boundConnection = _connectionIdentity();
            }
            var generation = _generation;
            _ = Task.Run(() => ProbeAsync(getJsonAsync, postJsonAsync, generation));
        }

        private static async Task ProbeAsync(MethodInfo getJsonAsync, MethodInfo postJsonAsync, int generation)
        {
            try
            {
                if (!IsCurrentConnection(generation)) return;
                var json = await DiscoverAsync(route => getJsonAsync.Invoke(null, new object[] { route }) as Task<string>,
                    () => IsCurrentConnection(generation)).ConfigureAwait(false);
                if (!IsCurrentConnection(generation)) return;
                if (json == null)
                {
                    _probeFailures = 0;
                    _nextProbeRetry = DateTime.MaxValue;
                    DiscoveryStatus = "当前服务器的游戏中文语言表未声明 SimpleTranslatorCS 工作台；请核对连接及服务端安装。";
                    _log?.LogInfo("SimpleTranslatorCS marker absent from /client/locale/ch; only the F12 workbench is enabled.");
                    return;
                }
                ServerTranslationCapabilities parsed;
                if (!ServerTranslationCapabilities.TryParse(json, out parsed) || !parsed.SupportsWorkbenchUi)
                {
                    _probeFailures = 0;
                    _nextProbeRetry = DateTime.MaxValue;
                    DiscoveryStatus = "当前服务端缺少兼容的 SimpleTranslatorCS 工作台目录或权限接口。更新服务端后可重新检测。";
                    _log?.LogDebug("SimpleTranslatorCS capabilities route was not compatible; server-text workbench is hidden.");
                    return;
                }
                if (!IsCurrentConnection(generation))
                    return;
                parsed.SourceChannel = ProbeChannel;
                _getJsonAsync = getJsonAsync;
                _postJsonAsync = postJsonAsync;
                _capabilities = parsed;
                DiscoveryStatus = "服务端文本工作台已连接";
                _probeFailures = 0;
                _log?.LogInfo("SimpleTranslatorCS server-text workbench enabled (protocol " +
                    parsed.ProtocolVersion + ", domains: " + string.Join(", ", parsed.Domains.ToArray()) + ").");
            }
            catch (Exception ex)
            {
                var root = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                if (!_initialized || generation != _generation) return;
                if (_capabilities == null)
                {
                    DiscoveryStatus = "服务端检测暂未完成（" + root.GetType().Name + "），可点击重新检测。";
                    _probeFailures++;
                    _nextProbeRetry = DateTime.UtcNow.AddSeconds(15);
                    _log?.LogInfo("SimpleTranslatorCS discovery temporarily unavailable: " + root.GetType().Name);
                }
                else _log?.LogDebug("SimpleTranslatorCS catalog refresh failed: " + root.GetType().Name);
            }
            finally
            {
                if (_initialized && generation == _generation) Interlocked.Exchange(ref _probeStarted, 2);
            }
        }

        internal static void RetryDiscovery()
        {
            if (!CanDiscover || IsAvailable || DiscoveryRunning) return;
            _probeFailures = 0;
            Interlocked.CompareExchange(ref _probeStarted, 0, 2);
            TryStartProbe();
        }

        internal static bool IsAdvertised(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf(DiscoveryKey, StringComparison.Ordinal) < 0) return false;
            try
            {
                var root = JObject.Parse(json);
                return (string)(root["data"]?[DiscoveryKey] ?? root[DiscoveryKey]) == "SimpleTranslatorCS:2";
            }
            catch { return false; }
        }

        internal static async Task<string> DiscoverAsync(Func<string, Task<string>> request, Func<bool> currentConnection)
        {
            var json = await AwaitProbeAsync(request(DiscoveryRoute)).ConfigureAwait(false);
            if (!IsAdvertised(json) || !currentConnection()) return null;
            var capabilities = await AwaitProbeAsync(request(CapabilitiesRoute)).ConfigureAwait(false);
            return currentConnection() ? capabilities : null;
        }

        private static async Task<string> AwaitProbeAsync(Task<string> pending)
        {
            if (pending == null) throw new InvalidOperationException("SPT 探测通道返回了未知任务类型。");
            using (var deadline = new CancellationTokenSource())
            {
                if (await Task.WhenAny(pending, Task.Delay(15000, deadline.Token)).ConfigureAwait(false) != pending)
                {
                    _ = pending.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException("服务端组件探测超时。");
                }
                deadline.Cancel();
            }
            return await pending.ConfigureAwait(false);
        }

        internal static Task<string> GetJsonAsync(string route)
        {
            var method = _getJsonAsync;
            if (!_initialized || method == null)
                throw new InvalidOperationException("SimpleTranslatorCS 请求通道尚未就绪。");
            var task = method.Invoke(null, new object[] { route }) as Task<string>;
            if (task == null) throw new InvalidOperationException("SPT GetJsonAsync 返回了未知任务类型。");
            return task;
        }

        internal static Task<string> PostJsonAsync(string route, string json)
        {
            if (_connectionIdentity != null && _boundConnection != _connectionIdentity())
            {
                TranslationPanel.ClearServerCredentials();
                var log = _log;
                Dispose();
                Initialize(log);
                throw new InvalidOperationException("服务器或玩家会话已切换，凭据已清空，请重新打开工作台连接。");
            }
            var method = _postJsonAsync;
            if (_connectionIdentity == null && _capabilities != null && _capabilities.SupportsFeature("known-profile-gateway"))
            {
                var payload = JObject.Parse(json);
                if (!string.IsNullOrWhiteSpace((string)payload["adminKey"]) || !string.IsNullOrWhiteSpace((string)payload["apiKey"]))
                    throw new InvalidOperationException("当前 SPT 请求通道无法识别服务器身份，已阻止发送凭据；请更新适配器或使用网页回填。");
            }
            if (!_initialized || method == null)
                throw new InvalidOperationException("SimpleTranslatorCS 请求通道尚未就绪。");
            var task = method.Invoke(null, new object[] { route, json }) as Task<string>;
            if (task == null) throw new InvalidOperationException("SPT PostJsonAsync 返回了未知任务类型。");
            return task;
        }

        internal static Type ResolveRequestHandler(Func<string, Type> resolver) =>
            resolver("SPT.Common.Http.RequestHandler") ?? resolver("SPTushonka.Common.Http.RequestHandler");

        internal static void CheckConnection()
        {
            if (!_initialized || DateTime.UtcNow < _nextConnectionCheck) return;
            _nextConnectionCheck = DateTime.UtcNow.AddSeconds(1);
            if (_connectionIdentity != null && _boundConnection != _connectionIdentity())
            {
                var log = _log;
                Dispose(); Initialize(log);
                return;
            }
            // Retry transient startup/network failures at most twice. A successful
            // locale response without the marker stays quiet until a manual retry.
            if (!IsAvailable && _probeFailures > 0 && _probeFailures < 3 && DateTime.UtcNow >= _nextProbeRetry)
            {
                Interlocked.CompareExchange(ref _probeStarted, 0, 2);
                TryStartProbe();
            }
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch (Exception)
                {
                    // Partially loaded third-party assemblies must not break the F12 workbench.
                }
            }
            return null;
        }
    }

    internal sealed class ServerTranslationCapabilities
    {
        internal string ServerVersion;
        internal int ProtocolVersion;
        internal bool FikaDetected;
        internal bool WriteAuthRequired;
        internal int PullCooldownSeconds;
        internal string SourceChannel;
        internal readonly HashSet<string> Domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool Supports(string domain) { return Domains.Contains(domain); }
        internal bool SupportsFeature(string feature) { return Features.Contains(feature); }
        internal bool SupportsWorkbenchUi => ProtocolVersion == 2 && SupportsFeature("catalog") &&
            (FikaDetected ? SupportsFeature("admin-access") :
                (!WriteAuthRequired || SupportsFeature("admin-access") || SupportsFeature("contribution-review")));

        internal static bool TryParse(string json, out ServerTranslationCapabilities capabilities)
        {
            capabilities = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                var root = JObject.Parse(json);
                if (!string.Equals((string)root["service"], "SimpleTranslatorCS", StringComparison.Ordinal) ||
                    (bool?)root["enabled"] != true)
                    return false;
                var protocol = (int?)root["protocolVersion"] ?? 0;
                if (protocol < 1 || protocol > 2) return false;
                var domains = root["domains"] as JArray;
                if (domains == null) return false;
                var parsed = new ServerTranslationCapabilities
                {
                    ProtocolVersion = protocol,
                    ServerVersion = (string)root["serverVersion"] ?? string.Empty,
                    FikaDetected = (bool?)root["fikaDetected"] == true,
                    WriteAuthRequired = (bool?)root["writeAuthRequired"] == true,
                    PullCooldownSeconds = (int?)root["pullCooldownSeconds"] ?? 180
                };
                foreach (var token in domains)
                    if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token))
                        parsed.Domains.Add((string)token);
                var features = root["features"] as JArray;
                if (features != null)
                    foreach (var token in features)
                        if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token))
                            parsed.Features.Add((string)token);
                // A server that cannot supply either promised scope must not expose an empty panel.
                if (!parsed.Supports("item") && !parsed.Supports("quest")) return false;
                capabilities = parsed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
