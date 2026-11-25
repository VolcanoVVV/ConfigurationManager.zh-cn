using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static async Task CheckDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "translation-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var items = new[] { Item(key: "Saved"), Item(key: "Failed"), Item(key: "Interrupted"), Item(key: "Waiting") };
            var secret = "key-\"with\\escapes";
            var journal = new TranslationDiagnostics("test", items, new[] { secret }, root);
            journal.Observe(new TranslationActivity { Items = items.Take(3).ToArray(), RequestStarted = true,
                Phase = "waiting", InputCharacters = 500 });
            journal.Observe(new TranslationActivity { Items = items.Take(3).ToArray(), InputTokens = 100, OutputTokens = 20 });
            journal.Result(items.Take(1), "saved");
            journal.Result(items.Skip(1).Take(1), "failed", "受保护的占位符发生变化：echo " + secret);
            journal.Finish("cancelled");
            var raw = File.ReadAllText(journal.FilePath);
            var inputLog = File.ReadAllText(journal.InputPath);
            var overview = File.ReadAllText(journal.ReportPath);
            Check(journal.FilePath.StartsWith(Path.Combine(root, "API翻译日志", "ConfigurationManager.zh-cn")) && inputLog.Contains("Enable feature"),
                "translation input log is separated under the game-root log folder");
            var records = File.ReadAllLines(journal.FilePath).Select(JObject.Parse).ToArray();
            var last = records.Last()["data"];
            Check(Path.GetFileName(journal.FilePath).StartsWith("翻译过程-") &&
                Path.GetFileName(journal.InputPath).StartsWith("翻译输入-") && Path.GetFileName(journal.ReportPath).StartsWith("翻译总览-") &&
                (string)records[0]["event"] == "overview" && records.All(row => (string)row["taskId"] == (string)records[0]["taskId"]),
                "Chinese log names and task ID connect the overview, progress, and input");
            Check(overview.Contains("计划 4 条 · 已保存 1 条 · 失败 1 条 · 未完成 2 条") &&
                overview.Contains("受保护标记不一致 · 1 条") && overview.Contains("已取消") &&
                overview.Contains("已隐藏") && !overview.Contains(secret) && !overview.Contains("Enable feature"),
                "the text overview shows counts and reasons while excluding original inputs and credentials");
            Check(journal.WriteError == null && journal.Requests == 1 && journal.InputTokens == 100 && journal.OutputTokens == 20,
                "diagnostic counters use actual requests and usage: " + journal.WriteError + " " + journal.Requests + "/" + journal.InputTokens + "/" + journal.OutputTokens);
            Check(!raw.Contains(secret) && !raw.Contains("Enable feature") && raw.Contains("已隐藏"),
                "journal excludes source bodies and redacts credentials before JSON escaping");
            Check(((JArray)last["unfinished"]).Count == 2 && (string)last["unfinished"][0]["state"] == "in_progress"
                && (string)last["unfinished"][1]["state"] == "not_started", "cancel report separates interrupted and unstarted settings");
            Check(records.Where(row => (string)row["event"] == "result").Count() == 2, "saved and failed results are both journaled");
            var onlyError = JObject.Parse(File.ReadAllLines(TranslationDiagnostics.DailyLog(Path.GetDirectoryName(journal.FilePath), true)).Single());
            Check(!onlyError.ToString().Contains(secret) && (string)onlyError["taskId"] == (string)records[0]["taskId"],
                "daily error log preserves credential redaction and identifies the task");
            var plan = new TranslationBatchPlan();
            var longItems = new[] { Item(new string('a', 600), "First"), Item(new string('b', 600), "Second") };
            plan.Targets.Add(new TranslationBatchTarget { Guid = "test.plugin", Name = "Test plugin", Items = longItems });
            var checkpoints = 0;
            var report = await TranslationBatchRunner.RunAsync(plan, (target, progress, token) =>
            {
                if (++checkpoints == 2) throw new InvalidOperationException("later batch failed");
                var result = new TranslationResult();
                result.Values[target.Items[0].Id] = "首批译文";
                return Task.FromResult(result);
            }, (target, result) => { }, null, CancellationToken.None, 40, null, 1000);
            Check(checkpoints == 2 && report.SavedItems == 1 && report.Failures.Count == 1,
                "character-sized checkpoints persist successes before a later failure");

            using (var server = new MockServer())
            {
                TranslationClients.ClearAiRequestCache();
                server.Reply = _ => (200, "{\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":45},\"choices\":[{\"message\":{\"content\":\"{\\\"t0001\\\":\\\"译文\\\"}\"}}]}");
                var apiJournal = new TranslationDiagnostics("usage-test", items.Take(1), rootOverride: root);
                await TranslationClients.TranslateAiAsync(items.Take(1).ToArray(), new TranslationProviderDefinition { Name = "test" },
                    server.Url, "model", "", "zh", "", 40, 12000, 1, 10, null, CancellationToken.None, apiJournal.Observe);
                Check(apiJournal.Requests == 1 && apiJournal.UsageResponses == 1 && apiJournal.InputTokens == 123 && apiJournal.OutputTokens == 45,
                    "HTTP response usage reaches the progress journal");
                TranslationClients.ClearAiRequestCache();
                server.Reply = _ => (200, "{\"usage\":null,\"choices\":[{\"message\":{\"content\":\"{\\\"t0001\\\":\\\"译文\\\"}\"}}]}");
                var noUsage = await TranslationClients.TranslateAiAsync(items.Take(1).ToArray(), new TranslationProviderDefinition { Name = "test" },
                    server.Url, "model", "", "zh", "", 40, 12000, 1, 10, null, CancellationToken.None, apiJournal.Observe);
                Check(noUsage.Values.Count == 1 && apiJournal.UsageResponses == 1, "nullable usage does not reject otherwise valid translations or invent usage");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
