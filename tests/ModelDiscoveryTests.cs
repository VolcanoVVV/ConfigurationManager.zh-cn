using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static async Task CheckModelDiscovery()
    {
        var routes = new List<string>();
        Func<string, Task<string>> absent = route => { routes.Add(route); return Task.FromResult("{\"ordinary-locale\":\"文本\"}"); };
        Check(await ServerTranslationFeature.DiscoverAsync(absent, () => true) == null && routes.Count == 1 && routes[0] == "/client/locale/ch",
            "missing optional mod never calls its unhandled capabilities route");
        routes.Clear();
        Func<string, Task<string>> present = route =>
        {
            routes.Add(route);
            return Task.FromResult(route == ServerTranslationFeature.DiscoveryRoute
                ? "{\"__SimpleTranslatorCS_WorkbenchProtocol\":\"SimpleTranslatorCS:2\"}" : "capabilities");
        };
        Check(await ServerTranslationFeature.DiscoverAsync(present, () => false) == null && routes.Count == 1,
            "connection switch suppresses optional capability request");
        routes.Clear();
        Check(await ServerTranslationFeature.DiscoverAsync(present, () => true) == "capabilities" && routes.Count == 2,
            "advertised server performs the capability handshake");
        Check(!ServerTranslationFeature.IsAdvertised("{\"__SimpleTranslatorCS_WorkbenchProtocol\":\"SimpleTranslatorCS:1\"}"), "old/incompatible marker remains disabled");
        routes.Clear();
        Func<string, Task<string>> separateLocaleTables = route =>
        {
            routes.Add(route);
            if (route == "/client/menu/locale/ch") return Task.FromResult("{\"err\":0,\"data\":{\"menu\":\"菜单文本\"}}");
            if (route == "/client/locale/ch") return Task.FromResult("{\"err\":0,\"data\":{\"__SimpleTranslatorCS_WorkbenchProtocol\":\"SimpleTranslatorCS:2\",\"item\":\"物品\"}}");
            if (route == ServerTranslationFeature.CapabilitiesRoute) return Task.FromResult("capabilities");
            throw new Exception("unexpected discovery route");
        };
        Check(await ServerTranslationFeature.DiscoverAsync(separateLocaleTables, () => true) == "capabilities",
            "native global locale envelope advertises the server while the separate menu table has no marker");
        Check(routes.Count == 2 && routes[0] == "/client/locale/ch" && !routes.Contains("/client/menu/locale/ch"),
            "discovery reads the actual table that contains the server transformer marker");
        Check(ServerTranslationFeature.IsAdvertised("{\"err\":0,\"data\":{\"__SimpleTranslatorCS_WorkbenchProtocol\":\"SimpleTranslatorCS:2\"}}"),
            "native SPT response envelope is supported");
        var current = true;
        Check(await ServerTranslationFeature.DiscoverAsync(route =>
        {
            if (route == ServerTranslationFeature.CapabilitiesRoute) current = false;
            return present(route);
        }, () => current) == null, "connection switch during capability response discards the old handshake");
        using (var server = new MockServer())
        {
            server.Reply = _ => (200, "{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"z-model\"},{\"id\":\"bad\\nmodel\"}]}");
            var endpoint = server.Url.TrimEnd('/') + "/v1/chat/completions";
            var ids = await TranslationClients.GetModelsAsync(new TranslationProviderDefinition { Name = "fixture" }, endpoint, "", CancellationToken.None);
            Check(ids.Length == 2 && ids[0] == "a-model" && ids[1] == "z-model", "model listing normalizes IDs and rejects malformed metadata");
            Check(server.Requests.Count == 1 && server.Requests[0].Headers.StartsWith("GET /translate/v1/models ") && server.Requests[0].Body == "",
                "model refresh is a metadata GET without translation input");
            server.Reply = _ => (500, "should not query cached metadata");
            Check((await TranslationClients.GetModelsAsync(new TranslationProviderDefinition { Name = "fixture" }, endpoint, "", CancellationToken.None)).Length == 2,
                "successful metadata cached independently from translation output");
        }
        Reject(() => TranslationFoundation.AiModelCatalog.ModelsUri("http://remote.invalid/v1/chat/completions"), "untrusted HTTP metadata endpoint");
        var root = Path.Combine(Path.GetTempPath(), "discovery-log-" + Guid.NewGuid().ToString("N"));
        Check(TranslationFoundation.TranslationLogPath.ForProject(Path.Combine(root, "BepInEx", "plugins"), "ConfigurationManager.zh-cn") ==
            Path.Combine(root, "API翻译日志", "ConfigurationManager.zh-cn"), "client logs resolve to game root");
    }
}
