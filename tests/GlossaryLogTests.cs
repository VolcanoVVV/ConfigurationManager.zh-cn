using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;
using TranslationFoundation;

internal static partial class Program
{
    private static async Task CheckGlossaryAndQuietLogs()
    {
        const string route = "/simpletranslator/v1/workbench/live";
        Check(LiveSyncLogFilter.ShouldHide("RequestHandler", LogLevel.Info, "[REQUEST]: " + route, true), "quiet mode hides only the live request-start line");
        Check(LiveSyncLogFilter.ShouldHide("RequestHandler", LogLevel.Info, "[REQUEST SUCCESSFUL] " + route, true), "quiet mode hides live success lines");
        Check(!LiveSyncLogFilter.ShouldHide("RequestHandler", LogLevel.Info, "[REQUEST FAILED] " + route, true), "request failures remain visible");
        Check(!LiveSyncLogFilter.ShouldHide("RequestHandler", LogLevel.Error, "[REQUEST]: " + route, true), "error-level logs always remain visible");
        Check(!LiveSyncLogFilter.ShouldHide("RequestHandler", LogLevel.Info, "[REQUEST]: /client/locale/ch", true), "other request routes remain visible");
        Check(!LiveSyncLogFilter.ShouldHide("Other mod", LogLevel.Info, "[REQUEST]: " + route, true), "other mod sources remain visible");
        Check(!LiveSyncLogFilter.ShouldHide("RequestHandler", LogLevel.Info, "[REQUEST]: " + route, false), "disabling quiet mode restores request logs");
        var glossary = TranslationGlossary.Parse("Ground Zero\t中心区\nGroundZero\t中心区\nhideout\t藏身处\nraid\t战局");
        Check(glossary.Reference(new[] { "GroundZero" }, "zh").Contains("中心区"), "compact map alias matches");
        Check(glossary.Reference(new[] { "GROUND_ZERO" }, "简体中文").Contains("中心区"), "case and underscore map aliases match");
        Check(glossary.Reference(new[] { "raider" }, "zh") == "", "word boundaries avoid unrelated matches");
        Check(glossary.Reference(new[] { "GroundZero" }, "en") == "", "Chinese reference is not injected for another target language");
        Check(glossary.Reference(new[] { "GroundZero" }, "zh", 10) == "", "a tiny glossary budget emits no partial reference");
        Reject(() => TranslationGlossary.Parse("raid\t战局\nRAID\t冲突"), "conflicting glossary duplicates are rejected before spending");
        Reject(() => TranslationGlossary.Parse("invalid line"), "malformed glossary data is rejected");
        Directory.CreateDirectory(ClientGlossary.DirectoryPath);
        File.WriteAllText(ClientGlossary.FilePath, "GroundZero\t中心区\nhideout\t藏身处");
        var sample = Item("GroundZero", "Map", "glossary.plugin");
        using (var server = new MockServer())
        {
            TranslationClients.ClearAiRequestCache();
            var provider = new TranslationProviderDefinition { Name="fixture", ApiKeyRequired=false };
            var diagnostics = new TranslationDiagnostics("glossary", new[] { sample });
            Func<Task<TranslationResult>> translate = () => TranslationClients.TranslateAiAsync(new[] { sample }, provider, server.Url,
                "fixture-model", "", "zh", "", 20, 12000, 0, 10, null, CancellationToken.None,
                activity:diagnostics.Observe, options:new AiExecutionOptions { RequestDelayMs=0 });
            await translate();
            var system = (string)JObject.Parse(server.Requests[0].Body)["messages"][0]["content"];
            Check(system.Contains("中心区") && !system.Contains("藏身处"), "actual client AI payload includes only matching references");
            var inputLog = File.ReadAllText(diagnostics.InputPath);
            Check(inputLog.Contains("中心区") && !inputLog.Contains("藏身处"), "input logs record the actual matched glossary prompt without unrelated entries");
            var snapshot = ClientGlossary.Load();
            File.WriteAllText(ClientGlossary.FilePath, "GroundZero\t中心区\nhideout\t用户新译名");
            await translate();
            Check(server.Requests.Count == 1, "unrelated glossary edit preserves same-session request cache");
            File.WriteAllText(ClientGlossary.FilePath, "GroundZero\t用户地图名称");
            await translate();
            Check(server.Requests.Count == 2, "relevant glossary edit invalidates only the relevant request cache entry");
            Check(snapshot.Reference(new[] { "GroundZero" }, "zh").Contains("中心区"), "running jobs retain their immutable glossary snapshot");
            var exchange = new TranslationExchange(); exchange.Local.Add(sample);
            Check(exchange.Prompt().Contains("用户地图名称"), "website clipboard exchange carries local matching reference data");
            File.WriteAllText(ClientGlossary.FilePath, "# intentionally empty");
            Check(ClientGlossary.Load().Reference(new[] { "GroundZero" }, "zh") == "", "deleted user entries stay deleted without hidden defaults");
            File.WriteAllText(ClientGlossary.FilePath, "invalid line");
            await RejectAsync(translate, "bad glossary blocks a paid request");
            Check(server.Requests.Count == 2, "invalid reference data triggers no additional HTTP calls");
            File.WriteAllText(ClientGlossary.FilePath, "# intentionally empty");
        }
    }
}
