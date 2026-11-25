using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TranslationFoundation
{
    #pragma warning disable CS8600, CS8618, CS8625 // Portable C# 7.3 optional request/cache fields.
    // Read-only metadata, never a completion request. Keys remain in request memory only.
    internal static class AiModelCatalog
    {
        private sealed class Entry { internal DateTime Expires, Created = DateTime.UtcNow; internal string[] Models; internal string Error; }
        private sealed class QueryException : Exception { internal QueryException(string message) : base(message) { } }
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };

        internal static Uri ModelsUri(string endpoint)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("模型查询需要 HTTPS 接口或本机 HTTP 接口。");
            var suffix = "/chat/completions";
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法推导模型查询地址，请手动输入模型 ID。");
            return new UriBuilder(uri) { Path = path.Substring(0, path.Length - suffix.Length) + "/models", Query = "", Fragment = "" }.Uri;
        }

        internal static string Context(string endpoint, string key, bool anthropic)
        {
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(
                Encoding.UTF8.GetBytes(endpoint + "\n" + (key ?? "").Trim() + "\n" + anthropic)));
        }

        internal static string[] Normalize(IEnumerable<string> ids, bool gemini)
        {
            return ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
                .Select(id => gemini && id.StartsWith("models/", StringComparison.Ordinal) ? id.Substring(7) : id)
                .Where(id => id.Length <= 256 && !id.Any(char.IsWhiteSpace) && !id.Any(char.IsControl))
                .Distinct(StringComparer.Ordinal).Take(500).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static async Task<string[]> GetAsync(string endpoint, string key, bool anthropic,
            Func<string, IEnumerable<string>> parse, CancellationToken token,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = null, bool refresh = false)
        {
            var uri = ModelsUri(endpoint);
            var context = Context(endpoint, key, anthropic);
            if (!await Gate.WaitAsync(0, token).ConfigureAwait(false))
                throw new InvalidOperationException("正在查询模型，请稍后刷新；仍可手动输入模型 ID。");
            try
            {
                Entry entry;
                if (Cache.TryGetValue(context, out entry) && entry.Expires > DateTime.UtcNow && (!refresh || DateTime.UtcNow.Subtract(entry.Created).TotalSeconds < 5))
                {
                    if (entry.Error != null) throw new InvalidOperationException(entry.Error);
                    return (string[])entry.Models.Clone();
                }
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                        if (anthropic)
                        {
                            request.Headers.TryAddWithoutValidation("x-api-key", key.Trim());
                            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                        }
                    }
                    try
                    {
                        using (var response = await (send == null ? Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                            : send(request, timeout.Token)).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode) throw new QueryException("模型查询返回 HTTP " + (int)response.StatusCode + "，可继续手动输入模型 ID。");
                            var body = await BoundedHttpContent.ReadAsync(response.Content, timeout.Token).ConfigureAwait(false);
                            var models = Normalize(parse(body), uri.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase));
                            if (models.Length == 0) throw new QueryException("接口未返回有效模型列表，可继续手动输入模型 ID。");
                            Put(context, new Entry { Models = models, Expires = DateTime.UtcNow.AddMinutes(10) });
                            return (string[])models.Clone();
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
                    {
                        var message = ex is QueryException ? ex.Message : "模型查询失败（" + ex.GetType().Name + "），可继续手动输入模型 ID。";
                        Put(context, new Entry { Error = message, Expires = DateTime.UtcNow.AddMinutes(1) });
                        throw new InvalidOperationException(message);
                    }
                }
            }
            finally { Gate.Release(); }
        }

        private static void Put(string context, Entry entry)
        {
            if (Cache.Count >= 64) Cache.Clear();
            Cache[context] = entry;
        }
    }
}
