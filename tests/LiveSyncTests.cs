using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Configuration;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static readonly List<JObject> LiveBodies = new List<JObject>();
    private static bool _liveFixtureUnchanged;
    private static bool _liveFixtureFailure;
    private static TaskCompletionSource<string> _lateLive;

    private static void CheckAutomaticLiveSync()
    {
        string observed = null;
        Check(!ServerLocaleSynchronizer.ObserveRevision(ref observed, "baseline") &&
            !ServerLocaleSynchronizer.ObserveRevision(ref observed, "baseline") &&
            ServerLocaleSynchronizer.ObserveRevision(ref observed, "published") &&
            !ServerLocaleSynchronizer.ObserveRevision(ref observed, "published"),
            "initial subscription and duplicate revisions never request the live route");
        const BindingFlags flags=BindingFlags.NonPublic | BindingFlags.Static;
        MainThreadDispatcher.Initialize();
        var feature=typeof(ServerTranslationFeature);
        var sync=typeof(ServerLocaleSynchronizer);
        var capabilities=new ServerTranslationCapabilities { WriteAuthRequired=true };
        capabilities.Features.Add("live-sync");
        feature.GetField("_initialized",flags).SetValue(null,true);
        feature.GetField("_capabilities",flags).SetValue(null,capabilities);
        feature.GetField("_postJsonAsync",flags).SetValue(null,typeof(Program).GetMethod("LivePost",flags));
        var run=sync.GetMethod("RunAsync",flags);
        LiveBodies.Clear();
        var manager=EFT.LocalizationManager._instance;
        ServerWorkbenchClient.AdminKey="secret";
        try
        {
            var generation=ServerTranslationFeature.Generation;
            sync.GetField("_revision",flags).SetValue(null,"");
            manager._locales["ch"]["withdrawn"]="旧译文";
            manager._locales["en"]["automatic"]="English";
            ((Task)run.Invoke(null,new object[] { generation })).GetAwaiter().GetResult();
            Check(manager._locales["ch"]["automatic"]=="自动同步" && !manager._locales["ch"].ContainsKey("withdrawn"),
                "automatic synchronization applies formal values and withdrawals");
            Check(LiveBodies.Count==1 && LiveBodies[0].Count==1 && LiveBodies[0]["adminKey"]==null && LiveBodies[0]["apiKey"]==null,
                "read-only live route sends neither administrator secret nor API credential");
            Check((string)sync.GetField("_revision",flags).GetValue(null)=="live-1", "cursor advances after successful main-thread apply");
            var notifications=manager._onLocaleUpdatedEvent.Count;
            _liveFixtureUnchanged=true;
            ((Task)run.Invoke(null,new object[] { generation })).GetAwaiter().GetResult();
            Check((string)LiveBodies[1]["liveRevision"]=="live-1" && manager._onLocaleUpdatedEvent.Count==notifications,
                "unchanged version avoids a dictionary merge and UI notification");
            _liveFixtureFailure=true;
            ((Task)run.Invoke(null,new object[] { generation })).GetAwaiter().GetResult();
            Check(ServerLocaleSynchronizer.Status.Contains("暂不可用"), "transport failure updates the synchronization status");
            _liveFixtureFailure=false;
            ((Task)run.Invoke(null,new object[] { generation })).GetAwaiter().GetResult();
            Check(ServerLocaleSynchronizer.Status.Contains("已恢复") && manager._onLocaleUpdatedEvent.Count==notifications,
                "recovery with an unchanged version clears the error without merging again");
            _liveFixtureUnchanged=false;
            sync.GetField("_revision",flags).SetValue(null,"retry");
            EFT.LocalizationManager._instance=null;
            ((Task)run.Invoke(null,new object[] { generation })).GetAwaiter().GetResult();
            Check((string)sync.GetField("_revision",flags).GetValue(null)=="retry", "uninitialized locale does not consume the synchronization cursor");
            EFT.LocalizationManager._instance=manager;
            manager.Culture="en";
            ((Task)run.Invoke(null,new object[] { generation })).GetAwaiter().GetResult();
            Check(manager._locales["en"]["automatic"]=="English" && manager._onLocaleUpdatedEvent.Count==notifications,
                "automatic sync preserves the active English language");
            manager.Culture="ch";
            _lateLive=new TaskCompletionSource<string>();
            var late=(Task)run.Invoke(null,new object[] { generation });
            feature.GetField("_generation",flags).SetValue(null,generation+1);
            manager._locales["ch"]["automatic"]="新服务器";
            _lateLive.SetResult(LiveReply());
            late.GetAwaiter().GetResult();
            Check(manager._locales["ch"]["automatic"]=="新服务器", "late response from another connection is ignored");

            var bound = new JArray("fixture-server", "fixture-player").ToString();
            var identity = bound;
            feature.GetField("_boundConnection", flags).SetValue(null, bound);
            feature.GetField("_connectionIdentity", flags).SetValue(null, (Func<string>)(() => identity));
            _lateLive = new TaskCompletionSource<string>();
            var lateBeforeTick = (Task)run.Invoke(null, new object[] { generation + 1 });
            identity = new JArray("new-fixture-server", "fixture-player").ToString();
            _lateLive.SetResult(LiveReply());
            lateBeforeTick.GetAwaiter().GetResult();
            Check(manager._locales["ch"]["automatic"] == "新服务器", "connection changes reject late locale responses before the periodic discovery tick");
            capabilities.Features.UnionWith(new[] { "known-profile-gateway", "contribution-review", "web-player-submit" });
            Check(!ServerWorkbenchClient.SupportsWebContributions, "web submission is disabled while an identity change is pending");
            identity = bound;
            feature.GetField("_postJsonAsync", flags).SetValue(null, typeof(Program).GetMethod(nameof(WebPost), flags));
            Check(ServerWorkbenchClient.SubmitWebContributionAsync("r", new JArray()).GetAwaiter().GetResult() == 1,
                "web submission reports acceptance without inheriting the game admin key");
            Reject(() => ClientLocaleRefresher.ApplyCheckedAsync(new Dictionary<string,string> { ["automatic"]="旧服务器" }, new string[0], "ch", () => false).GetAwaiter().GetResult(),
                "main-thread connection guard rejects stale queued locale writes");
            var rejected=ClientLocaleRefresher.ApplyCheckedAsync(new Dictionary<string,string> { ["automatic"]="错误语言" },new string[0],"en",()=>true).GetAwaiter().GetResult();
            Check(!rejected.Applied && manager._locales["en"]["automatic"]=="English", "unsafe locale payload cannot advance a successful apply result");

            var config = new ConfigFile(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            ConfigurationManagerZhCnPlugin.EnableServerLiveSync = config.Bind("sync", "enabled", true);
            feature.GetField("_postJsonAsync", flags).SetValue(null, typeof(Program).GetMethod(nameof(LivePost), flags));
            sync.GetField("_generation", flags).SetValue(null, ServerTranslationFeature.Generation);
            _lateLive = null;
            _liveFixtureUnchanged = true;
            var before = LiveBodies.Count;
            ServerLocaleSynchronizer.Tick();
            ServerLocaleSynchronizer.Tick();
            Check(LiveBodies.Count == before, "old servers never receive an idle live request");
            feature.GetField("_connectionIdentity", flags).SetValue(null, null);
            capabilities.Features.Add("live-notify");
            ServerLocaleSynchronizer.Tick();
            Check(LiveBodies.Count == before, "enabling event notifications does not poll the live route");
            sync.GetField("_requestedRevision", flags).SetValue(null, "published");
            ServerLocaleSynchronizer.Tick();
            ServerLocaleSynchronizer.Tick();
            Check(LiveBodies.Count == before + 1, "a single push requests exactly one live update");
        }
        finally
        {
            EFT.LocalizationManager._instance=manager; manager.Culture="ch";
            _lateLive=null; _liveFixtureUnchanged=_liveFixtureFailure=false;
            ConfigurationManagerZhCnPlugin.EnableServerLiveSync = null;
            ServerTranslationFeature.Dispose(); ServerWorkbenchClient.AdminKey="";
            MainThreadDispatcher.Shutdown();
        }
    }
    private static Task<string> LivePost(string route,string body)
    {
        Check(route=="/simpletranslator/v1/workbench/live", "automatic synchronization uses the dedicated read-only route");
        LiveBodies.Add(JObject.Parse(body));
        if (_liveFixtureFailure) return Task.FromException<string>(new InvalidOperationException("fixture transport failure"));
        return _lateLive==null ? Task.FromResult(LiveReply()) : _lateLive.Task;
    }
    private static Task<string> WebPost(string route, string body)
    {
        var payload = JObject.Parse(body);
        Check(route == "/simpletranslator/v1/workbench/web-player-submit" && payload["adminKey"] == null && payload["apiKey"] == null,
            "web submissions use the player-only route and omit credentials");
        return Task.FromResult("{\"ok\":true,\"saved\":1}");
    }
    private static string LiveReply() => _liveFixtureUnchanged
        ? "{\"ok\":true,\"changed\":false,\"liveRevision\":\"live-1\"}"
        : new JObject { ["ok"]=true,["changed"]=true,["liveRevision"]="live-1",["locale"]="ch",
            ["values"]=new JObject { ["automatic"]="自动同步" },["removedKeys"]=new JArray("withdrawn") }.ToString();
}
