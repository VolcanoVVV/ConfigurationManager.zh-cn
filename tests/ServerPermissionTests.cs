using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static readonly List<string> PermissionRoutes = new List<string>();
    private static bool _fixtureRateLimit;

    private static void CheckServerPermissionWorkflow()
    {
        MainThreadDispatcher.Initialize();
        var feature = typeof(ServerTranslationFeature);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var capabilities = new ServerTranslationCapabilities { WriteAuthRequired = true, PullCooldownSeconds = 180 };
        capabilities.Features.UnionWith(new[] { "admin-access", "pull-sync", "catalog" });
        feature.GetField("_initialized", flags).SetValue(null, true);
        feature.GetField("_capabilities", flags).SetValue(null, capabilities);
        feature.GetField("_postJsonAsync", flags).SetValue(null, typeof(Program).GetMethod("PermissionPost", flags));
        ServerWorkbenchClient.Reset();
        ServerWorkbenchClient.AdminKey = "";
        Check(ServerWorkbenchClient.AdminValidationState == "empty", "empty administrator key has player status");
        PermissionRoutes.Clear();
        try
        {
            Check(ServerWorkbenchClient.RefreshAsync().GetAwaiter().GetResult() == null && !ServerWorkbenchClient.IsAdmin,
                "ordinary player refresh remains outside administrator mode");
            Check(PermissionRoutes.Count == 1 && PermissionRoutes[0] == ServerWorkbenchClient.AccessRoute,
                "capability/access refresh does not automatically pull text or request administrator catalog");
            ServerWorkbenchClient.PullAsync().GetAwaiter().GetResult();
            Check(EFT.LocalizationManager._instance._locales["ch"]["pulled"] == "已同步"
                && ServerWorkbenchClient.PullRemainingSeconds > 0, "manual player pull applies text and starts the UI countdown");
            _fixtureRateLimit = true;
            Reject(() => ServerWorkbenchClient.PullAsync().GetAwaiter().GetResult(), "server cooldown rejection is surfaced");
            Check(ServerWorkbenchClient.PullRemainingSeconds >= 16 && ServerWorkbenchClient.PullRemainingSeconds <= 17,
                "server retryAfter replaces the client countdown");
            _fixtureRateLimit = false;
            ServerWorkbenchClient.AdminKey = "secret";
            Check(!ServerWorkbenchClient.IsAdmin, "entering a key alone does not grant administrator UI privileges");
            Check(ServerWorkbenchClient.AdminValidationState == "unverified", "typed key remains yellow until server verification");
            var paidCount = PermissionRoutes.Count;
            Reject(() => ServerWorkbenchClient.StartJobAsync("ai", "test-revision", new string[0], true, "fixture-key", "").GetAwaiter().GetResult(),
                "unverified administrator key blocks a paid test");
            Check(PermissionRoutes.Count == paidCount, "unverified key is rejected before any paid route or API credential is sent");
            var catalog = ServerWorkbenchClient.RefreshAsync().GetAwaiter().GetResult();
            Check(ServerWorkbenchClient.IsAdmin && catalog != null && catalog.Revision == "test-revision"
                && PermissionRoutes[PermissionRoutes.Count - 1] == ServerWorkbenchClient.CatalogRoute,
                "verified administrator fetches authenticated catalog");
            Check(ServerWorkbenchClient.PullRemainingSeconds == 0, "administrator UI bypasses player cooldown");
            Check(ServerWorkbenchClient.AdminValidationState == "verified", "server-confirmed administrator key has green status");
            ServerWorkbenchClient.ReloadAsync().GetAwaiter().GetResult();
            Check(PermissionRoutes.Contains(ServerWorkbenchClient.ReloadRoute), "administrator update calls reload endpoint");
            ServerWorkbenchClient.AdminKey = "wrong";
            Check(!ServerWorkbenchClient.IsAdmin, "changing a verified key immediately exits administrator UI mode");
            Check(ServerWorkbenchClient.AdminValidationState == "unverified", "editing a verified key resets validation status");
            ServerWorkbenchClient.RefreshAsync().GetAwaiter().GetResult();
            Check(ServerWorkbenchClient.AdminValidationState == "rejected", "invalid administrator key has red status");
            paidCount = PermissionRoutes.Count;
            Reject(() => ServerWorkbenchClient.StartJobAsync("ai", "test-revision", new string[0], false, "fixture-key", "").GetAwaiter().GetResult(),
                "invalid key never falls back to a paid player contribution");
            Check(PermissionRoutes.Count == paidCount, "invalid key is rejected without a network job request");

            capabilities.FikaDetected = true;
            capabilities.Features.UnionWith(new[] { "known-profile-gateway", "contribution-review", "web-player-submit" });
            const string connection = "[\"fixture-server\",\"fixture-session\"]";
            feature.GetField("_connectionIdentity", flags).SetValue(null, (Func<string>)(() => connection));
            feature.GetField("_boundConnection", flags).SetValue(null, connection);
            ServerWorkbenchClient.AdminKey = "";
            var lockedCount = PermissionRoutes.Count;
            Check(!ServerWorkbenchClient.CanUseWorkbench && ServerWorkbenchClient.SupportsWebContributions &&
                ServerWorkbenchClient.RefreshAsync().GetAwaiter().GetResult() == null,
                "Fika reports a supported but locked workbench without a key");
            Reject(() => ServerWorkbenchClient.PullAsync().GetAwaiter().GetResult(), "Fika blocks interactive player pull while locked");
            Reject(() => ServerWorkbenchClient.ReadWebContributionCatalogAsync().GetAwaiter().GetResult(),
                "Fika blocks the web catalog while locked");
            Check(PermissionRoutes.Count == lockedCount, "Fika never fetches the contribution catalog with an empty key");
            ServerWorkbenchClient.AdminKey = "wrong";
            Check(ServerWorkbenchClient.RefreshAsync().GetAwaiter().GetResult() == null && !ServerWorkbenchClient.CanUseWorkbench &&
                ServerWorkbenchClient.AdminValidationState == "rejected" && PermissionRoutes.Count == lockedCount + 1 &&
                PermissionRoutes[lockedCount] == ServerWorkbenchClient.AccessRoute, "Fika keeps both workbenches locked after a rejected key");
            ServerWorkbenchClient.AdminKey = "secret";
            Check(!ServerWorkbenchClient.CanUseWorkbench, "Fika does not unlock on key entry alone");
            Check(ServerWorkbenchClient.RefreshAsync().GetAwaiter().GetResult() != null && ServerWorkbenchClient.CanUseWorkbench,
                "a server-verified key unlocks both Fika workbenches");
            Check(ServerWorkbenchClient.ReadWebContributionCatalogAsync().GetAwaiter().GetResult().Revision == "test-revision" &&
                ServerWorkbenchClient.SubmitWebContributionAsync("test-revision", new JArray()).GetAwaiter().GetResult() == 1,
                "the unlocked web path still submits without passing an administrator key to the player route");
            capabilities.Features.UnionWith(new[] { "fika-workbench-policy", "contribution-receipts" });
            var draftId = Guid.NewGuid().ToString("N");
            var receipt = ServerWorkbenchClient.SubmitWebContributionAsync("test-revision", new JArray(), draftId).GetAwaiter().GetResult();
            Check((string)receipt["submissionId"] == draftId && (bool?)receipt["adminVerified"] == true,
                "the new Fika route receives the key and stable draft identifier through the game connection");
            var committed = ServerWorkbenchClient.WithFollowupAsync("服务端已保存。", null,
                () => Task.FromException<string>(new TimeoutException("同步超时"))).GetAwaiter().GetResult();
            Check(committed.StartsWith("服务端已保存。") && committed.Contains("同步超时"),
                "follow-up failure preserves the confirmed write result");
            ServerWorkbenchClient.AdminKey = "";
            ServerWorkbenchClient.AdminKey = "secret";
            Check(!ServerWorkbenchClient.CanUseWorkbench, "re-entering a cleared key requires verification again");

            var count = PermissionRoutes.Count;
            feature.GetField("_connectionIdentity", flags).SetValue(null, (Func<string>)(() => "new-server"));
            feature.GetField("_boundConnection", flags).SetValue(null, "old-server");
            Reject(() => ServerTranslationFeature.PostJsonAsync(ServerWorkbenchClient.AccessRoute, "{\"adminKey\":\"secret\"}"), "connection switch blocks credentials before sending");
            Check(PermissionRoutes.Count == count && ServerWorkbenchClient.AdminKey == "", "switching backend or session clears credentials and sends no request to the new server");
        }
        finally
        {
            ServerTranslationFeature.Dispose();
            ServerWorkbenchClient.AdminKey = "";
            MainThreadDispatcher.Shutdown();
        }
    }

    private static Task<string> PermissionPost(string route, string body)
    {
        PermissionRoutes.Add(route);
        var payload = JObject.Parse(body);
        var isAdmin = (string)payload["adminKey"] == "secret";
        if (route == ServerWorkbenchClient.AccessRoute)
            return Task.FromResult(new JObject { ["ok"] = true, ["isAdmin"] = isAdmin }.ToString());
        if (route == ServerWorkbenchClient.CatalogRoute && isAdmin)
            return Task.FromResult("{\"revision\":\"test-revision\",\"entries\":[]}");
        if (route == ServerWorkbenchClient.ContributionCatalogRoute && payload["adminKey"] == null)
            return Task.FromResult("{\"ok\":true,\"revision\":\"test-revision\",\"entries\":[]}");
        if (route == "/simpletranslator/v1/workbench/web-player-submit" && (isAdmin || payload["adminKey"] == null))
            return Task.FromResult(new JObject { ["ok"] = true, ["saved"] = 1,
                ["submissionId"] = (string)payload["submissionId"], ["adminVerified"] = isAdmin }.ToString());
        if (route == ServerWorkbenchClient.PullRoute || route == ServerWorkbenchClient.ReloadRoute && isAdmin)
        {
            if (_fixtureRateLimit && !isAdmin)
                return Task.FromResult("{\"ok\":false,\"rateLimited\":true,\"retryAfterSeconds\":17}");
            return Task.FromResult(new JObject { ["ok"] = true, ["locale"] = "ch",
                ["values"] = new JObject { ["pulled"] = "已同步" }, ["removedKeys"] = new JArray(),
                ["retryAfterSeconds"] = isAdmin ? 0 : 180 }.ToString());
        }
        return Task.FromResult("{\"ok\":false,\"unauthorized\":true}");
    }
}
