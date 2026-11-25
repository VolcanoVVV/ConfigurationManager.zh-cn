using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static async Task CheckExchangePolicy()
    {
        Check(ServerTranslationFeature.ResolveRequestHandler(name => name == "SPTushonka.Common.Http.RequestHandler" ? typeof(Program) : null) == typeof(Program),
            "5.0 BE SPTushonka transport is discovered without a hard dependency");
        var item = Item("Use {0} with F1", "Exchange");
        var pack = new TranslationExchange(); pack.Local.Add(item);
        Check(pack.Prompt().Contains("t0001") && !pack.Prompt().Contains(item.PluginGuid), "web prompt uses temporary IDs, excludes credentials and local paths");
        Check(pack.Parse("```json\n{\"t0001\":\"使用 {0} 与 F1\"}\n```", false)[item.Id] == "使用 {0} 与 F1", "web JSON round trip validates protected markers");
        Reject(() => pack.Parse("{\"t0001\":\"使用 F1\"}", false), "web import rejects changed placeholders");
        Reject(() => pack.Parse("{\"t0002\":\"译文\"}", false), "web import rejects invented IDs");
        Reject(() => pack.Parse("{\"t0001\":\"甲\",\"t0001\":\"乙\"}", false), "web import rejects duplicate JSON keys");
        Reject(() => pack.Parse("{\"t0001\":\"Use {0} with F1\"}", false), "web echo needs intentional retain-original action");
        Check(pack.Parse("{\"t0001\":\"Use {0} with F1\"}", true).Count == 1, "intentional proper names can retain their original source");
        var options = new AiExecutionOptions { MaxRequests = 1 }; options.Budget.Reserve(1, 2000, 256, "");
        Reject(() => options.Budget.Reserve(1, 2000, 256, ""), "request budget counts every attempted call");
        var ledger = new TranslationFoundation.PaidApiBudget();
        var hold = ledger.Reserve(10, 600, 256, "hello");
        ledger.Complete(hold, null, null, true);
        Reject(() => ledger.Reserve(10, 600, 256, "hello"), "absent usage keeps the reserved budget");
        ledger = new TranslationFoundation.PaidApiBudget();
        hold = ledger.Reserve(10, 600, 256, "hello");
        ledger.Complete(hold, 30, 20, true);
        Check(ledger.Reserve(10, 600, 256, "hello") == hold, "complete usage frees unused reservation");
        var failures = new TranslationFoundation.PaidApiBudget();
        for (var i = 0; i < 4; i++) failures.Complete(failures.Reserve(100, 0, 256, ""), null, null, false);
        Reject(() => failures.Reserve(100, 0, 256, ""), "four unsuccessful sends trip the circuit breaker");
        var type = typeof(TranslationClients).Assembly.GetType("TranslationFoundation.TranslationRequestPolicy");
        var parameters = type.GetMethod("Parameters", BindingFlags.NonPublic | BindingFlags.Static);
        var values = JObject.FromObject(parameters.Invoke(null, new object[] { "https://api.deepseek.com/chat/completions", "deepseek-flash", "auto", "off", 4096, true }));
        Check((string)values["thinking"]?["type"] == "disabled" && (int)values["max_tokens"] == 4096, "known vendor adapter disables reasoning and sets explicit output cap");
        using (var content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]))
        {
            var reader = typeof(TranslationClients).Assembly.GetType("TranslationFoundation.BoundedHttpContent").GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static);
            await RejectAsync(() => (Task<string>)reader.Invoke(null, new object[] { content, CancellationToken.None }), "oversized provider response is bounded before buffering");
        }
        using (var server = new MockServer())
        {
            TranslationClients.ClearAiRequestCache();
            var before = server.Requests.Count;
            var items = new[] { Item("First text", "PolicyA"), Item("Second text", "PolicyB") };
            var limited = new AiExecutionOptions { MaxRequests = 1, RequestDelayMs = 0 };
            var partial = await TranslationClients.TranslateAiAsync(items, new TranslationProviderDefinition { Name = "Fixture", ApiKeyRequired = false }, server.Url,
                "model", "", "zh", "policy-budget", 1, 12000, 0, 10, null, CancellationToken.None, options: limited);
            Check(server.Requests.Count - before == 1 && partial.Values.Count == 1 && partial.Failures.Count == 1 && partial.StopReason != null,
                "AI budget stops before second paid request and preserves first result");
            TranslationClients.ClearAiRequestCache();
            var goodReply = server.Reply;
            server.Reply = request => ((JArray)JObject.Parse((string)JObject.Parse(request.Body)["messages"][1]["content"])["items"]).Count > 1
                ? (413, "too large") : goodReply(request);
            before = server.Requests.Count;
            partial = await TranslationClients.TranslateAiAsync(items, new TranslationProviderDefinition { Name = "Fixture", ApiKeyRequired = false }, server.Url,
                "model", "", "zh", "split-budget", 40, 12000, 0, 10, null, CancellationToken.None,
                options: new AiExecutionOptions { MaxRequests = 2, RequestDelayMs = 0 });
            Check(server.Requests.Count - before == 2 && partial.Values.Count == 1 && partial.Failures.Count == 1,
                "successful split half survives budget stop before the second half");
            TranslationClients.ClearAiRequestCache();
            server.Reply = _ => (200, "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}");
            before = server.Requests.Count;
            var truncated = await TranslationClients.TranslateAiAsync(new[] { item }, new TranslationProviderDefinition { Name = "Fixture", ApiKeyRequired = false }, server.Url,
                "model", "", "zh", "truncated", 40, 12000, 3, 10, null, CancellationToken.None);
            Check(truncated.Failures.Count == 1 && server.Requests.Count - before == 1, "truncated output is surfaced and never billed again automatically");
            TranslationClients.ClearAiRequestCache();
            server.Reply = _ => (200, "{\"choices\":[{\"message\":{\"content\":\"{\\\"t0001\\\":\\\"bad\"}}]}");
            before = server.Requests.Count;
            var malformed = await TranslationClients.TranslateAiAsync(new[] { item }, new TranslationProviderDefinition { Name = "Fixture", ApiKeyRequired = false }, server.Url,
                "model", "", "zh", "malformed", 40, 12000, 3, 10, null, CancellationToken.None);
            Check(malformed.Failures[item.Id].StartsWith("AI 译文 JSON 语法错误") && server.Requests.Count - before == 1,
                "malformed content is distinguished from the response envelope and does not trigger a paid retry");
        }
    }
}
