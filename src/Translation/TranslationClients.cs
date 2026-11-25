using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal static class TranslationClients
    {
        private static readonly HttpClient HttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, string> AiCache = new Dictionary<string, string>(StringComparer.Ordinal);

        internal static Task<string[]> GetModelsAsync(TranslationProviderDefinition provider, string endpoint, string apiKey, CancellationToken token, bool refresh = false)
        {
            if (provider.ApiKeyRequired && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("填写 API 密钥后可自动获取模型，或直接手动输入模型 ID。");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return TranslationFoundation.AiModelCatalog.GetAsync(endpoint, apiKey, provider.AnthropicAuthentication,
                json => ((JObject.Parse(json)["data"] as JArray) ?? new JArray()).OfType<JObject>()
                    .Where(item => item["id"]?.Type == JTokenType.String).Select(item => (string)item["id"]).ToArray(), token, refresh: refresh);
        }

        internal static async Task<TranslationResult> TranslateMachineAsync(
            IList<TranslationWorkItem> items,
            MachineProviderDefinition provider,
            string endpoint,
            string apiKey,
            string appId,
            string region,
            string sourceLanguage,
            string targetLanguage,
            int batchSize,
            int timeoutSeconds,
            Action<int, int> progress,
            CancellationToken cancellationToken)
        {
            ValidateEndpoint(endpoint, apiKey);
            if (provider.ApiKeyRequired && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(provider.Name + " 需要 API 密钥。");
            if (provider.AppIdRequired && string.IsNullOrWhiteSpace(appId))
                throw new InvalidOperationException(provider.Name + " 需要应用 ID。");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var result = new TranslationResult();
            var batches = Batch(items, Math.Max(1, Math.Min(50, batchSize))).ToList();
            var completed = 0;
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IList<string> translated;
                if (provider.Name == "Microsoft Translator")
                    translated = await TranslateMicrosoftAsync(batch, endpoint, apiKey, region, sourceLanguage,
                        targetLanguage, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                else if (provider.Name == "DeepL")
                    translated = await TranslateDeepLAsync(batch, endpoint, apiKey, sourceLanguage,
                        targetLanguage, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                else if (provider.Name == "百度翻译" || provider.Name == "有道翻译")
                    translated = await TranslateChineseProviderAsync(batch, provider.Name, endpoint, appId, apiKey,
                        sourceLanguage, targetLanguage, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                else if (provider.Name == "Google Translate")
                    translated = await TranslateGoogleAsync(batch, endpoint, apiKey, sourceLanguage,
                        targetLanguage, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                else if (provider.Name == "LibreTranslate")
                    translated = await TranslateLibreAsync(batch, endpoint, apiKey, sourceLanguage,
                        targetLanguage, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                else throw new InvalidOperationException("尚未支持的机翻协议：" + provider.Name);

                if (translated.Count != batch.Count)
                    throw new InvalidOperationException("机翻接口返回的译文数量与请求不一致。");
                for (var index = 0; index < batch.Count; index++)
                {
                    try { result.Values[batch[index].Id] = TranslationSafety.Validate(translated[index], batch[index]); }
                    catch (TranslationValidationException ex) { result.Failures[batch[index].Id] = ex.Message; }
                }
                completed += batch.Count;
                if (progress != null)
                    progress(completed, items.Count);
            }
            return result;
        }

        internal static Task<TranslationResult> TranslatePluginTitlesAsync(
            IList<TranslationWorkItem> items, TranslationProviderDefinition provider, string endpoint, string model, string apiKey,
            string targetLanguage, string extraPrompt, int batchSize, int maxInputCharacters, int retryCount, int timeoutSeconds,
            CancellationToken token, Action<TranslationActivity> activity, AiExecutionOptions options)
        {
            // Every plugin stores its title under "plugin-name". Use the GUID for this cross-plugin request.
            var originals = items.ToDictionary(item => item.PluginGuid, StringComparer.Ordinal);
            var requestItems = items.Select(item => item.WithRequestId(item.PluginGuid)).ToList();
            return TranslateAiAsync(requestItems, provider, endpoint, model, apiKey, targetLanguage, extraPrompt, Math.Min(16, batchSize),
                maxInputCharacters, retryCount, timeoutSeconds, null, token, update =>
                {
                    update.Items = update.Items.Select(item => originals[item.Id]).ToList();
                    activity?.Invoke(update);
                }, options);
        }

        internal static Task<TranslationResult> TranslateAiAsync(
            IList<TranslationWorkItem> items,
            TranslationProviderDefinition provider,
            string endpoint,
            string model,
            string apiKey,
            string targetLanguage,
            string extraPrompt,
            int batchSize,
            int timeoutSeconds,
            Action<int, int> progress,
            CancellationToken cancellationToken)
        {
            return TranslateAiAsync(items, provider, endpoint, model, apiKey, targetLanguage, extraPrompt,
                batchSize, 12000, 1, timeoutSeconds, progress, cancellationToken);
        }

        internal static async Task<TranslationResult> TranslateAiAsync(
            IList<TranslationWorkItem> items,
            TranslationProviderDefinition provider,
            string endpoint,
            string model,
            string apiKey,
            string targetLanguage,
            string extraPrompt,
            int batchSize,
            int maxInputCharacters,
            int retryCount,
            int timeoutSeconds,
            Action<int, int> progress,
            CancellationToken cancellationToken,
            Action<TranslationActivity> activity = null,
            AiExecutionOptions options = null)
        {
            options = options ?? new AiExecutionOptions();
            options.Glossary = options.Glossary ?? ClientGlossary.Load();
            options.GlossaryBudget = options.Glossary.Budget(Math.Max(1000, Math.Min(60000, maxInputCharacters)));
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEndpoint(endpoint, apiKey);
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("请填写 AI 模型 ID。");
            if (provider.ApiKeyRequired && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(provider.Name + " 需要 API 密钥。");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var result = new TranslationResult();
            var completed = 0;
            var groups = items.GroupBy(TranslationSafety.ContentKey, StringComparer.Ordinal).ToList();
            var unique = groups.Select(group => group.First()).ToList();
            var copies = groups.ToDictionary(group => group.First().Id, group => group.ToList(), StringComparer.Ordinal);
            var gameText = items.Any(item => item.Kind == "ServerGameText");
            var profile = TranslationSafety.Profile(provider.Name, endpoint, model, targetLanguage, extraPrompt, gameText) + options.ProfileTag;
            var pending = new List<TranslationWorkItem>();
            foreach (var item in unique)
            {
                string cached;
                lock (CacheSync) AiCache.TryGetValue(profile + "|" + TranslationSafety.ContentKey(item) + "|" + options.Glossary.Reference(new[] { item.Source }, targetLanguage, options.GlossaryBudget), out cached);
                if (cached == null) { pending.Add(item); continue; }
                foreach (var copy in copies[item.Id]) { result.Values[copy.Id] = cached; completed++; }
            }
            if (progress != null) progress(completed, items.Count);
            var inputBudget = AiInputBudget(maxInputCharacters, targetLanguage, extraPrompt, gameText);
            if (options.Glossary.Reference(pending.Select(item => item.Source), targetLanguage, options.GlossaryBudget).Length > 0)
                inputBudget = Math.Max(1, inputBudget - options.GlossaryBudget);
            foreach (var batch in BatchByInputSize(pending, Math.Max(1, Math.Min(60, batchSize)),
                inputBudget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batch.Count == 1 && ItemCharacters(batch[0]) > inputBudget)
                {
                    foreach (var copy in copies[batch[0].Id]) result.Failures[copy.Id] = "单条文本超过字符预算，未发送；请调整预算或人工处理。";
                    completed += copies[batch[0].Id].Count;
                    progress?.Invoke(completed, items.Count);
                    continue;
                }
                TranslationResult batchResult;
                try { batchResult = await TranslateAiBatchResilientAsync(batch, provider, endpoint, model, apiKey,
                    targetLanguage, extraPrompt, gameText, timeoutSeconds, Math.Max(0, Math.Min(3, retryCount)),
                    cancellationToken, activity, 0, options).ConfigureAwait(false); }
                catch (TranslationFoundation.PaidApiLimitException ex)
                {
                    RejectBatch(result, items.Where(item => !result.Values.ContainsKey(item.Id)), ex.Message);
                    result.StopReason = ex.Message;
                    break;
                }
                var translated = batchResult.Values;
                foreach (var failure in batchResult.Failures)
                    foreach (var item in copies[failure.Key])
                        result.Failures[item.Id] = failure.Value;
                lock (CacheSync)
                {
                    if (AiCache.Count + batch.Count > 10000) AiCache.Clear();
                    foreach (var item in batch)
                        if (translated.ContainsKey(item.Id))
                            AiCache[profile + "|" + TranslationSafety.ContentKey(item) + "|" + options.Glossary.Reference(new[] { item.Source }, targetLanguage, options.GlossaryBudget)] = translated[item.Id];
                }
                foreach (var pair in translated)
                    foreach (var item in copies[pair.Key])
                    {
                        result.Values[item.Id] = TranslationSafety.Validate(pair.Value, item);
                    }
                if (translated.Count > 0) options.Checkpoint?.Invoke(result);
                completed += batch.Sum(item => copies[item.Id].Count);
                if (progress != null)
                    progress(completed, items.Count);
                if (!string.IsNullOrEmpty(batchResult.StopReason))
                {
                    result.StopReason = batchResult.StopReason;
                    RejectBatch(result, items.Where(item => !result.Values.ContainsKey(item.Id)), batchResult.StopReason);
                    break;
                }
            }
            return result;
        }

        internal static int AiInputBudget(int maxInputCharacters, string language, string extraPrompt, bool gameText = false)
        {
            var promptCharacters = (gameText ? TranslationSafety.BuildGameTextPrompt(language, extraPrompt)
                : TranslationSafety.BuildPrompt(language, extraPrompt)).Length;
            return Math.Max(256, Math.Max(1000, Math.Min(60000, maxInputCharacters)) - promptCharacters - 128);
        }

        private static async Task<TranslationResult> TranslateAiBatchResilientAsync(
            IList<TranslationWorkItem> items,
            TranslationProviderDefinition provider,
            string endpoint,
            string model,
            string apiKey,
            string targetLanguage,
            string extraPrompt,
            bool gameText,
            int timeoutSeconds,
            int retryCount,
            CancellationToken cancellationToken,
            Action<TranslationActivity> activity,
            int splitDepth,
            AiExecutionOptions options)
        {
            var result = new TranslationResult();
            Exception lastError = null;
            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await TranslateAiBatchAsync(items, provider, endpoint, model, apiKey,
                        targetLanguage, extraPrompt, gameText, timeoutSeconds, cancellationToken, activity, options).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (TranslationValidationException ex)
                {
                    lastError = ex;
                    break;
                }
                catch (TranslationHttpException ex)
                {
                    if (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden ||
                        ex.StatusCode == HttpStatusCode.NotFound)
                        throw;
                    if (ex.PayloadTooLarge)
                        return await SplitOrRejectAiBatchAsync(items, provider, endpoint, model, apiKey, targetLanguage,
                            extraPrompt, gameText, timeoutSeconds, retryCount, ex, cancellationToken, activity, splitDepth, options).ConfigureAwait(false);
                    if ((int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500 && !IsTransient(ex.StatusCode))
                        throw;
                    lastError = ex;
                    if (!IsTransient(ex.StatusCode) || attempt == retryCount) break;
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                    break; // The provider may already have billed the original request.
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                    break;
                }
                catch (JsonException)
                {
                    lastError = new InvalidOperationException("AI 接口响应外层 JSON 无法解析。");
                    break;
                }

                activity?.Invoke(new TranslationActivity { Items = items, Phase = "接口限流或服务异常，等待重试 " + (attempt + 1) });
                await Task.Delay(TimeSpan.FromMilliseconds(1000 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }

            RejectBatch(result, items, lastError == null ? "AI 请求失败。" : lastError.Message);
            return result;
        }

        private static async Task<TranslationResult> SplitOrRejectAiBatchAsync(
            IList<TranslationWorkItem> items,
            TranslationProviderDefinition provider,
            string endpoint,
            string model,
            string apiKey,
            string targetLanguage,
            string extraPrompt,
            bool gameText,
            int timeoutSeconds,
            int retryCount,
            Exception error,
            CancellationToken cancellationToken,
            Action<TranslationActivity> activity,
            int splitDepth,
            AiExecutionOptions options)
        {
            if (items.Count <= 1 || splitDepth >= 2)
            {
                var rejected = new TranslationResult();
                RejectBatch(rejected, items, error.Message);
                return rejected;
            }

            var split = Math.Max(1, items.Count / 2);
            var left = await TranslateAiBatchResilientAsync(items.Take(split).ToList(), provider, endpoint, model, apiKey,
                targetLanguage, extraPrompt, gameText, timeoutSeconds, retryCount, cancellationToken, activity, splitDepth + 1, options).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(left.StopReason)) { RejectBatch(left, items.Skip(split), left.StopReason); return left; }
            try
            {
                var right = await TranslateAiBatchResilientAsync(items.Skip(split).ToList(), provider, endpoint, model, apiKey,
                    targetLanguage, extraPrompt, gameText, timeoutSeconds, retryCount, cancellationToken, activity, splitDepth + 1, options).ConfigureAwait(false);
                Merge(left, right);
                left.StopReason = right.StopReason;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                left.StopReason = ex.Message;
                RejectBatch(left, items.Skip(split), ex.Message);
            }
            return left;
        }

        private static void Merge(TranslationResult target, TranslationResult source)
        {
            foreach (var pair in source.Values) target.Values[pair.Key] = pair.Value;
            foreach (var pair in source.Failures) target.Failures[pair.Key] = pair.Value;
        }

        private static void RejectBatch(TranslationResult result, IEnumerable<TranslationWorkItem> items, string message)
        {
            foreach (var item in items) result.Failures[item.Id] = message;
        }

        private static bool IsTransient(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 429 || code >= 500;
        }

        private static bool LooksLikePayloadTooLarge(HttpStatusCode statusCode, string responseBody)
        {
            if (statusCode == HttpStatusCode.RequestEntityTooLarge) return true;
            if (statusCode != HttpStatusCode.BadRequest && (int)statusCode != 422) return false;
            var value = (responseBody ?? string.Empty).ToLowerInvariant();
            return value.Contains("context_length") || value.Contains("maximum context") ||
                value.Contains("too many tokens") || value.Contains("request too large") ||
                value.Contains("payload too large") || value.Contains("content too large") ||
                value.Contains("input is too long");
        }

        internal static void ClearAiRequestCache()
        {
            lock (CacheSync) AiCache.Clear();
        }

        internal static IDictionary<string, string> BaiduForm(string text, string appId, string key, string from, string to, string salt)
        {
            string signature;
            using (var md5 = MD5.Create())
                signature = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(appId + text + salt + key))).Replace("-", "").ToLowerInvariant();
            return new Dictionary<string, string> { ["q"] = text, ["appid"] = appId, ["salt"] = salt,
                ["sign"] = signature, ["from"] = from, ["to"] = to };
        }

        internal static IDictionary<string, string> YoudaoForm(string text, string appId, string key, string from, string to, string salt, string time)
        {
            var input = text.Length <= 20 ? text : text.Substring(0, 10) + text.Length + text.Substring(text.Length - 10);
            return new Dictionary<string, string> { ["q"] = text, ["appKey"] = appId, ["salt"] = salt,
                ["signType"] = "v3", ["curtime"] = time, ["sign"] = TranslationSafety.Hash(appId + input + salt + time + key),
                ["from"] = from, ["to"] = to, ["strict"] = "true" };
        }

        private static async Task<IList<string>> TranslateChineseProviderAsync(IList<TranslationWorkItem> items, string provider,
            string endpoint, string appId, string key, string from, string to, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            foreach (var item in items)
            {
                var baidu = provider == "百度翻译";
                var salt = Guid.NewGuid().ToString("N");
                if (baidu && item.Source.Length > 1000)
                    throw new InvalidOperationException("百度预设按标准版限制处理单条 1000 字符以内的文本；长文本请改用其他平台或网页模式。");
                // Conservative pacing also works with Baidu's standard 1 QPS plan. No paid automatic retries.
                await Task.Delay(1100, cancellationToken).ConfigureAwait(false);
                var fields = baidu ? BaiduForm(item.Source, appId.Trim(), key.Trim(), from, to, salt)
                    : YoudaoForm(item.Source, appId.Trim(), key.Trim(), from, to, salt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    request.Content = new FormUrlEncodedContent(fields);
                    var json = JObject.Parse(await SendAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false));
                    var error = (string)json[baidu ? "error_code" : "errorCode"];
                    if (baidu ? !string.IsNullOrEmpty(error) && error != "52000" : error != "0")
                        throw new InvalidOperationException(provider + "返回错误码 " + error + "，请通过官方文档核对密钥、额度与语言。");
                    result.Add(baidu ? string.Join("\n", ((JArray)json["trans_result"]).Select(row => (string)row["dst"]))
                        : (string)json["translation"]?[0]);
                }
            }
            return result;
        }

        private static async Task<IList<string>> TranslateGoogleAsync(IList<TranslationWorkItem> items, string endpoint,
            string key, string from, string to, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var payload = new JObject { ["q"] = new JArray(items.Select(item => item.Source)), ["target"] = to, ["format"] = "text" };
            if (!string.IsNullOrWhiteSpace(from) && from != "auto") payload["source"] = from;
            using (var request = CreateJsonRequest(endpoint, payload.ToString(Formatting.None)))
            {
                request.Headers.TryAddWithoutValidation("X-goog-api-key", key.Trim());
                var json = JObject.Parse(await SendAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false));
                var translations = json["data"]?["translations"] as JArray;
                if (translations == null) throw new InvalidOperationException("Google 未返回 translations。");
                return translations.Select(row => WebUtility.HtmlDecode((string)row["translatedText"])).ToList();
            }
        }

        private static async Task<IList<string>> TranslateMicrosoftAsync(IList<TranslationWorkItem> items, string endpoint,
            string apiKey, string region, string sourceLanguage, string targetLanguage, int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var body = new JArray(items.Select(item => new JObject { ["Text"] = item.Source }));
            var requestEndpoint = AppendLanguageQuery(endpoint, sourceLanguage, targetLanguage);
            using (var request = CreateJsonRequest(requestEndpoint, body.ToString(Formatting.None)))
            {
                request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", apiKey.Trim());
                if (!string.IsNullOrWhiteSpace(region))
                    request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region.Trim());
                var json = await SendAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                var array = JArray.Parse(json);
                return array.Select(item => (string)item["translations"]?[0]?["text"] ?? string.Empty).ToList();
            }
        }

        private static async Task<IList<string>> TranslateDeepLAsync(IList<TranslationWorkItem> items, string endpoint,
            string apiKey, string sourceLanguage, string targetLanguage, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var values = items.Select(item => new KeyValuePair<string, string>("text", item.Source)).ToList();
            if (!string.IsNullOrWhiteSpace(sourceLanguage) && !string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
                values.Add(new KeyValuePair<string, string>("source_lang", sourceLanguage.Trim().ToUpperInvariant()));
            values.Add(new KeyValuePair<string, string>("target_lang", NormalizeDeepLLanguage(targetLanguage)));
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Content = new FormUrlEncodedContent(values);
                request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey.Trim());
                var json = await SendAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                var array = JObject.Parse(json)["translations"] as JArray;
                return array == null
                    ? new List<string>()
                    : array.Select(item => (string)item["text"] ?? string.Empty).ToList();
            }
        }

        private static async Task<IList<string>> TranslateLibreAsync(IList<TranslationWorkItem> items, string endpoint,
            string apiKey, string sourceLanguage, string targetLanguage, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            foreach (var item in items)
            {
                var payload = new JObject
                {
                    ["q"] = item.Source,
                    ["source"] = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage.Trim(),
                    ["target"] = NormalizeLibreLanguage(targetLanguage),
                    ["format"] = "text"
                };
                if (!string.IsNullOrWhiteSpace(apiKey))
                    payload["api_key"] = apiKey.Trim();
                using (var request = CreateJsonRequest(endpoint, payload.ToString(Formatting.None)))
                {
                    var json = await SendAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                    result.Add((string)JObject.Parse(json)["translatedText"] ?? string.Empty);
                }
            }
            return result;
        }

        private static async Task<TranslationResult> TranslateAiBatchAsync(
            IList<TranslationWorkItem> items,
            TranslationProviderDefinition provider,
            string endpoint,
            string model,
            string apiKey,
            string targetLanguage,
            string extraPrompt,
            bool gameText,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            Action<TranslationActivity> activity,
            AiExecutionOptions options)
        {
            var idMap = new Dictionary<string, TranslationWorkItem>(StringComparer.Ordinal);
            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            var titleOnly = items.All(item => item.Kind == "Plugin");
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.Field == "Description" && item.EntryKey != null)
                    descriptions[item.PluginGuid + "\0" + item.EntryKey] = "t" + (index + 1).ToString("D4");
            }
            var input = new JArray();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var id = "t" + (index + 1).ToString("D4");
                idMap[id] = item;
                var row = new JObject
                {
                    ["id"] = id,
                    ["text"] = item.Source,
                    ["kind"] = item.Kind
                };
                if (!titleOnly)
                {
                    row["context"] = item.Field == "Category" ? "Category" :
                        item.Field + ":" + (item.EntryKey ?? item.EnumMember ?? string.Empty);
                    if (item.PluginGuid != items[0].PluginGuid) row["plugin"] = item.PluginName ?? item.PluginGuid;
                }
                if (!string.IsNullOrWhiteSpace(item.OriginalSection)) row["section"] = item.OriginalSection;
                if (item.Kind == "Plugin" && !string.IsNullOrWhiteSpace(item.BriefDescription))
                    row["description"] = item.BriefDescription;
                if (item.Field == "DispName" && item.EntryKey != null)
                {
                    string descriptionId;
                    if (descriptions.TryGetValue(item.PluginGuid + "\0" + item.EntryKey, out descriptionId)) row["descriptionId"] = descriptionId;
                    else if (!string.IsNullOrWhiteSpace(item.BriefDescription)) row["description"] = item.BriefDescription;
                }
                input.Add(row);
            }

            var systemPrompt = gameText
                ? TranslationSafety.BuildGameTextPrompt(targetLanguage, extraPrompt)
                : titleOnly
                    ? TranslationSafety.BuildPluginTitlePrompt(targetLanguage, extraPrompt)
                    : TranslationSafety.BuildPrompt(targetLanguage, extraPrompt);
            systemPrompt += options.Glossary.Reference(items.Select(item => item.Source), targetLanguage, options.GlossaryBudget);
            var payload = JObject.FromObject(TranslationFoundation.TranslationRequestPolicy.Parameters(endpoint, model.Trim(),
                options.Adapter, options.Reasoning, options.MaxOutputTokens, options.JsonOutput));
            payload["model"] = model.Trim();
            var requestItems = new JObject { ["items"] = input };
            if (!titleOnly) requestItems["plugin"] = items[0].PluginName ?? items[0].PluginGuid;
            payload["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = requestItems.ToString(Formatting.None) }
                };

            var body = payload.ToString(Formatting.None);
            if (options.RequestDelayMs > 0) await Task.Delay(Math.Max(0, Math.Min(10000, options.RequestDelayMs)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var reserved = options.Budget.Reserve(options.MaxRequests, options.MaxReportedTokens, options.MaxOutputTokens, body);
            long? inputTokens = null, outputTokens = null;
            var successful = false;
            try
            {
                activity?.Invoke(new TranslationActivity { Items = items, Phase = "等待 API 返回", RequestStarted = true,
                    InputCharacters = body.Length, SystemPrompt = systemPrompt });
                using (var request = CreateJsonRequest(endpoint, body))
                {
                    ApplyAiAuthentication(request, provider, apiKey);
                    var responseText = await SendAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);
                    var response = JObject.Parse(responseText);
                    var usage = response["usage"] as JObject;
                    inputTokens = ReadTokenUsage(usage?["prompt_tokens"]);
                    outputTokens = ReadTokenUsage(usage?["completion_tokens"]);
                    activity?.Invoke(new TranslationActivity { Items = items, Phase = "校验返回译文",
                        InputTokens = inputTokens, OutputTokens = outputTokens });
                    if ((string)response.SelectToken("choices[0].finish_reason") == "length")
                        throw new TranslationValidationException("输出达到 token 上限，未自动重翻；请缩小批次或调整输出上限。");
                    var content = response.SelectToken("choices[0].message.content");
                    if (content == null || content.Type != JTokenType.String)
                        throw new TranslationValidationException("AI 接口响应缺少文本 content 字段。");
                    var parsed = TranslationSafety.ParsePartialResponse((string)content, idMap, targetLanguage);
                    successful = parsed.Values.Count > 0;
                    return parsed;
                }
            }
            finally { options.Budget.Complete(reserved, inputTokens, outputTokens, successful); }
        }

        private static HttpRequestMessage CreateJsonRequest(string endpoint, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.UserAgent.ParseAdd("ConfigurationManager.zh-cn/2.0");
            return request;
        }

        private static long? ReadTokenUsage(JToken value)
        {
            long parsed;
            return value != null && value.Type == JTokenType.Integer && long.TryParse(value.ToString(), out parsed)
                ? (long?)Math.Max(0, parsed) : null;
        }

        private static void ApplyAiAuthentication(HttpRequestMessage request, TranslationProviderDefinition provider, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            if (provider.AnthropicAuthentication)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
        }

        private static async Task<string> SendAsync(HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, Math.Min(600, timeoutSeconds))));
                HttpResponseMessage response;
                try
                {
                    response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw;
                    throw new TimeoutException("翻译接口请求超时。");
                }

                using (response)
                {
                    var text = await TranslationFoundation.BoundedHttpContent.ReadAsync(response.Content, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        // Provider bodies can echo credentials or inputs. Never copy them into logs/status.
                        throw new TranslationHttpException(response.StatusCode, LooksLikePayloadTooLarge(response.StatusCode, text),
                            "翻译接口返回 HTTP " + (int)response.StatusCode + "，请通过官方文档检查配置与额度。");
                    }
                    return text;
                }
            }
        }

        private static IEnumerable<IList<TranslationWorkItem>> Batch(IList<TranslationWorkItem> items, int batchSize)
        {
            for (var offset = 0; offset < items.Count; offset += batchSize)
                yield return items.Skip(offset).Take(batchSize).ToList();
        }

        internal static IEnumerable<IList<TranslationWorkItem>> BatchByInputSize(
            IList<TranslationWorkItem> items, int batchSize, int maxInputCharacters)
        {
            var current = new List<TranslationWorkItem>();
            var characters = 0;
            foreach (var item in items)
            {
                var cost = ItemCharacters(item);
                if (current.Count > 0 && (current.Count >= batchSize || characters + cost > maxInputCharacters))
                {
                    yield return current;
                    current = new List<TranslationWorkItem>();
                    characters = 0;
                }
                current.Add(item);
                characters += cost;
            }
            if (current.Count > 0) yield return current;
        }

        private static int ItemCharacters(TranslationWorkItem item)
        {
            return (item.Source ?? string.Empty).Length + (item.Field ?? string.Empty).Length +
                (item.Field == "Category" ? 0 : (item.EntryKey ?? item.EnumMember ?? string.Empty).Length) +
                (item.OriginalSection?.Length ?? 0) + (item.BriefDescription?.Length ?? 0) + 80;
        }

        private static string AppendLanguageQuery(string endpoint, string sourceLanguage, string targetLanguage)
        {
            var result = endpoint;
            if (!string.IsNullOrWhiteSpace(sourceLanguage) && !string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) &&
                result.IndexOf("from=", StringComparison.OrdinalIgnoreCase) < 0)
                result = AppendQuery(result, "from", sourceLanguage.Trim());
            if (result.IndexOf("to=", StringComparison.OrdinalIgnoreCase) < 0)
                result = AppendQuery(result, "to", string.IsNullOrWhiteSpace(targetLanguage) ? "zh-Hans" : targetLanguage.Trim());
            return result;
        }

        private static string AppendQuery(string endpoint, string name, string value)
        {
            return endpoint + (endpoint.IndexOf('?') >= 0 ? "&" : "?") + name + "=" + Uri.EscapeDataString(value);
        }

        private static string NormalizeDeepLLanguage(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "ZH-HANS" : value.Trim().Replace('_', '-').ToUpperInvariant();
        }

        private static string NormalizeLibreLanguage(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "zh";
            return value.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : value.Trim();
        }

        private static void ValidateEndpoint(string endpoint, string apiKey)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("翻译接口必须是完整的 HTTP 或 HTTPS 地址。");
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("接口地址不能内嵌凭据。");
            if (!string.IsNullOrWhiteSpace(apiKey) && uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new InvalidOperationException("携带 API 密钥的远程接口必须使用 HTTPS。");
        }

        private sealed class TranslationHttpException : HttpRequestException
        {
#if IL2CPP || WEB_WORKBENCH
            internal new readonly HttpStatusCode StatusCode;
#else
            internal readonly HttpStatusCode StatusCode;
#endif
            internal readonly bool PayloadTooLarge;

            internal TranslationHttpException(HttpStatusCode statusCode, bool payloadTooLarge, string message) : base(message)
            {
                StatusCode = statusCode;
                PayloadTooLarge = payloadTooLarge;
            }
        }
    }
}
