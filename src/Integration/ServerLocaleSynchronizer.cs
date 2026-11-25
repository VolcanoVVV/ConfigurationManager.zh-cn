using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    // Read-only synchronization. No personal API key or administrator secret is sent.
    internal static class ServerLocaleSynchronizer
    {
        internal const string NotificationPath = "/simpletranslator/v1/workbench/locale-notify";
        internal const string LivePath = "/simpletranslator/v1/workbench/live";
        private static DateTime _nextWatch;
        private static volatile bool _running;
        private static int _generation = -1;
        private static string _revision = "";
        private static string _observedRevision;
        private static string _requestedRevision;
        private static string _lastError;
        private static Task _watch;
        private static CancellationTokenSource _stopWatch;
        internal static string Status = "等待服务端正式译文";

        internal static void Tick()
        {
            var current = ServerTranslationFeature.Generation;
            var enabled = ConfigurationManagerZhCnPlugin.EnableServerLiveSync?.Value == true;
            if (current != _generation)
            {
                StopListening();
                _generation = current;
                _revision = "";
                _observedRevision = null;
                _requestedRevision = null;
                _lastError = null;
                Status = "等待服务端正式译文";
            }
            if (!enabled || ServerTranslationFeature.Capabilities?.SupportsFeature("live-notify") != true)
            {
                if (_stopWatch != null) StopListening();
                if (enabled && ServerTranslationFeature.Capabilities?.SupportsFeature("live-sync") == true)
                    Status = "此版本服务端可手动拉取译文";
                return;
            }

            if ((_watch == null || _watch.IsCompleted) && DateTime.UtcNow >= _nextWatch &&
                TryWatchAddress(out var endpoint, out var session))
            {
                _stopWatch?.Dispose();
                _stopWatch = new CancellationTokenSource();
                _watch = WatchAsync(current, endpoint, session, _stopWatch.Token);
            }

            if (_running) return;
            var requested = Interlocked.Exchange(ref _requestedRevision, null);
            if (requested == null || requested == _revision) return;
            _running = true;
            _ = RunAsync(current);
        }

        internal static void StopListening()
        {
            var watcher = _stopWatch;
            _stopWatch = null;
            if (watcher != null)
            {
                watcher.Cancel();
                watcher.Dispose();
            }
            _watch = null;
            _nextWatch = DateTime.MinValue;
            Interlocked.Exchange(ref _requestedRevision, null);
        }

        private static bool TryWatchAddress(out Uri endpoint, out string session)
        {
            endpoint = null;
            session = null;
            if (!ServerTranslationFeature.HasIdentifiableConnection) return false;
            var identity = JArray.Parse(ServerTranslationFeature.ConnectionIdentity);
            if (!Uri.TryCreate((string)identity[0], UriKind.Absolute, out var host) ||
                (host.Scheme != "https" && host.Scheme != "http") ||
                (!host.IsLoopback && host.Scheme != "https")) return false;
            session = (string)identity[1];
            endpoint = new UriBuilder(host)
            {
                Scheme = host.Scheme == "https" ? "wss" : "ws",
                Path = host.AbsolutePath.TrimEnd('/') + NotificationPath,
                Query = string.Empty, Fragment = string.Empty
            }.Uri;
            return true;
        }

        internal static bool ObserveRevision(ref string previous, string announced)
        {
            if (string.IsNullOrEmpty(announced) || announced.Length > 128 || announced == previous) return false;
            var changed = previous != null;
            previous = announced;
            return changed;
        }

        private static async Task WatchAsync(int generation, Uri endpoint, string session, CancellationToken token)
        {
            try
            {
                using (var socket = new ClientWebSocket())
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    socket.Options.SetRequestHeader("X-SimpleTranslator-Session", session);
                    // The bundled SPT certificate is self-signed. Restrict this exception to
                    // the configured loopback notification socket; remote sockets use system trust.
                    if (endpoint.IsLoopback)
                    {
                        var certificate = socket.Options.GetType().GetProperty("RemoteCertificateValidationCallback");
                        certificate?.SetValue(socket.Options, new RemoteCertificateValidationCallback(
                            (sender, value, chain, errors) => value != null), null);
                    }
                    connect.CancelAfter(TimeSpan.FromSeconds(12));
                    await socket.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
                    if (!ServerTranslationFeature.IsCurrentConnection(generation) || token.IsCancellationRequested) return;
                    Status = "等待服务端发布通知";
                    var buffer = new ArraySegment<byte>(new byte[256]);
                    while (!token.IsCancellationRequested)
                    {
                        var frame = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                        if (frame.MessageType == WebSocketMessageType.Close) break;
                        if (frame.MessageType != WebSocketMessageType.Text || !frame.EndOfMessage)
                            throw new InvalidOperationException("服务端译文通知格式不兼容。");
                        if (!ServerTranslationFeature.IsCurrentConnection(generation) || token.IsCancellationRequested) return;
                        var revision = Encoding.UTF8.GetString(buffer.Array, 0, frame.Count);
                        if (ObserveRevision(ref _observedRevision, revision)) Interlocked.Exchange(ref _requestedRevision, revision);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && ServerTranslationFeature.IsCurrentConnection(generation))
                    Status = "推送连接暂不可用，可手动拉取：" + ex.GetBaseException().GetType().Name;
            }
            finally
            {
                if (!token.IsCancellationRequested && ServerTranslationFeature.IsCurrentConnection(generation))
                    _nextWatch = DateTime.UtcNow.AddSeconds(45);
            }
        }

        private static async Task RunAsync(int generation)
        {
            try
            {
                var pending = ServerTranslationFeature.PostJsonAsync(LivePath,
                    new JObject { ["liveRevision"] = _revision }.ToString(Newtonsoft.Json.Formatting.None));
                // RequestHandler has no portable cancellation overload across supported versions.
                // Bound our wait; late transport responses never reach the locale merge below.
                using (var deadline = new CancellationTokenSource())
                {
                    if (await Task.WhenAny(pending, Task.Delay(15000, deadline.Token)).ConfigureAwait(false) != pending)
                    {
                        _ = pending.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new TimeoutException("服务端同步请求超时。");
                    }
                    deadline.Cancel();
                }
                var json = await pending.ConfigureAwait(false);
                var response = JObject.Parse(json);
                if (!ServerTranslationFeature.IsCurrentConnection(generation)) return;
                if ((bool?)response["ok"] != true) throw new InvalidOperationException((string)response["error"] ?? "自动同步失败");
                if ((bool?)response["changed"] != true)
                {
                    if (_lastError != null) Status = "自动同步已恢复 · 当前译文为最新版本";
                    _lastError = null;
                    return;
                }
                var values = (response["values"] as JObject ?? new JObject()).Properties()
                    .ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);
                var removed = (response["removedKeys"] as JArray ?? new JArray()).Values<string>().ToArray();
                var applied = await ClientLocaleRefresher.ApplyCheckedAsync(values, removed, (string)response["locale"],
                    () => ServerTranslationFeature.IsCurrentConnection(generation)).ConfigureAwait(false);
                if (!ServerTranslationFeature.IsCurrentConnection(generation)) return;
                Status = applied.Message;
                if ((bool?)response["restartRequired"] == true) Status += " 部分服务端文本仍需按重启提示处理。";
                if (applied.Applied) _revision = (string)response["liveRevision"] ?? "";
                if (applied.Applied)
                {
                    _lastError = null;
                    TranslationDiagnostics.Operation("自动同步正式译文", "已同步 " + values.Count + " 条；移除 " + removed.Length + " 条。");
                }
                else
                {
                    if (_lastError != "locale-unavailable") TranslationDiagnostics.Operation("自动同步本地刷新未完成", applied.Message, true);
                    _lastError = "locale-unavailable";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ServerTranslationFeature.IsCurrentConnection(generation))
                {
                    Status = "自动同步暂不可用：" + ex.GetBaseException().Message;
                    var safeError = ex.GetBaseException().GetType().Name;
                    if (_lastError != safeError) TranslationDiagnostics.Operation("自动同步失败", safeError, true);
                    _lastError = safeError;
                }
            }
            finally { _running = false; }
        }
    }
}
