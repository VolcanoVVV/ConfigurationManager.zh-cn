using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;

internal static partial class Program
{
    private static readonly List<string> DiscoveryRoutes = new List<string>();
    private static bool _discoveryFixtureFailure, _discoveryFixtureAdvertised, _discoveryFixtureOld;

    private static void CheckDiscoveryFailureAndRecovery()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var feature = typeof(ServerTranslationFeature);
        feature.GetField("_initialized", flags).SetValue(null, true);
        feature.GetField("_discoverySupported", flags).SetValue(null, true);
        feature.GetField("_capabilities", flags).SetValue(null, null);
        feature.GetField("_probeStarted", flags).SetValue(null, 1);
        feature.GetField("_probeFailures", flags).SetValue(null, 0);
        var get = typeof(Program).GetMethod("DiscoveryGet", flags);
        var post = typeof(Program).GetMethod("DiscoveryPost", flags);
        var probe = feature.GetMethod("ProbeAsync", flags);
        var generation = ServerTranslationFeature.Generation;
        DiscoveryRoutes.Clear();
        try
        {
            _discoveryFixtureFailure = true;
            ((Task)probe.Invoke(null, new object[] { get, post, generation })).GetAwaiter().GetResult();
            Check(!ServerTranslationFeature.IsAvailable && !ServerTranslationFeature.DiscoveryRunning &&
                ServerTranslationFeature.DiscoveryStatus.Contains("暂未完成"), "transient probe failure exposes a recoverable detection status");
            Check((int)feature.GetField("_probeFailures", flags).GetValue(null) == 1 && DiscoveryRoutes.Count == 1,
                "native locale failure schedules a bounded retry without calling optional capabilities");
            _discoveryFixtureFailure = false;
            feature.GetField("_probeStarted", flags).SetValue(null, 1);
            ((Task)probe.Invoke(null, new object[] { get, post, generation })).GetAwaiter().GetResult();
            Check(!ServerTranslationFeature.IsAvailable && ServerTranslationFeature.DiscoveryStatus.Contains("未声明"),
                "ordinary server without marker explains why the server text domain is unavailable");
            Check((int)feature.GetField("_probeFailures", flags).GetValue(null) == 0 &&
                (DateTime)feature.GetField("_nextProbeRetry", flags).GetValue(null) == DateTime.MaxValue,
                "successful absence after a transient failure cancels automatic retries");
            Check(DiscoveryRoutes.Count == 2 && !DiscoveryRoutes.Contains(ServerTranslationFeature.CapabilitiesRoute),
                "component absence never sends an unhandled capabilities request");
            _discoveryFixtureAdvertised = true;
            _discoveryFixtureOld = true;
            feature.GetField("_probeStarted", flags).SetValue(null, 1);
            ((Task)probe.Invoke(null, new object[] { get, post, generation })).GetAwaiter().GetResult();
            Check(!ServerTranslationFeature.IsAvailable && ServerTranslationFeature.DiscoveryStatus.Contains("缺少兼容"),
                "legacy handshake without a catalog does not expose the server tab");
            _discoveryFixtureOld = false;
            feature.GetField("_probeStarted", flags).SetValue(null, 1);
            ((Task)probe.Invoke(null, new object[] { get, post, generation })).GetAwaiter().GetResult();
            Check(ServerTranslationFeature.IsAvailable && ServerTranslationFeature.DiscoveryStatus.Contains("已连接") &&
                !ServerTranslationFeature.DiscoveryRunning, "a later successful probe exposes the server text workbench");
            Check(DiscoveryRoutes.Count == 6 && DiscoveryRoutes[4] == "/client/locale/ch" &&
                DiscoveryRoutes[5] == ServerTranslationFeature.CapabilitiesRoute && ServerWorkbenchClient.Snapshot == null,
                "recovery confirms the marker without automatically scanning the full catalog");
        }
        finally
        {
            _discoveryFixtureFailure = _discoveryFixtureAdvertised = _discoveryFixtureOld = false;
            ServerTranslationFeature.Dispose();
        }
    }
    private static Task<string> DiscoveryGet(string route)
    {
        DiscoveryRoutes.Add(route);
        if (_discoveryFixtureFailure) return Task.FromException<string>(new InvalidOperationException("fixture startup failure"));
        if (route == "/client/locale/ch") return Task.FromResult(_discoveryFixtureAdvertised
            ? "{\"err\":0,\"data\":{\"__SimpleTranslatorCS_WorkbenchProtocol\":\"SimpleTranslatorCS:2\"}}"
            : "{\"err\":0,\"data\":{\"item\":\"物品\"}}");
        if (route == ServerTranslationFeature.CapabilitiesRoute)
            return Task.FromResult("{\"service\":\"SimpleTranslatorCS\",\"enabled\":true,\"protocolVersion\":2,\"domains\":[\"item\",\"quest\"],\"features\":[" +
                (_discoveryFixtureOld ? "" : "\"catalog\"") + "]}");
        throw new InvalidOperationException("unexpected discovery route");
    }
    private static Task<string> DiscoveryPost(string route, string body) => throw new InvalidOperationException("probe must not submit credentials");
}
