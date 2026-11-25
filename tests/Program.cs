using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using ConfigurationManagerZhCn;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static int _assertions;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        _assertions++;
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception) { _assertions++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); } catch (Exception) { _assertions++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static TranslationWorkItem Item(string source = "Enable feature", string key = "Enabled", string guid = "test.plugin")
    {
        var item = TranslationWorkItem.ForSetting(guid, key, "Description", source, "配置项说明；设置键：" + key);
        item.PluginName = "Test plugin";
        item.PluginVersion = "1.0";
        return item;
    }
    private static Dictionary<string, string> Values(TranslationWorkItem item, string value)
    {
        return new Dictionary<string, string> { [item.Id] = value };
    }
    private static void SetCurrent(params TranslationWorkItem[] items)
    {
        var field = typeof(JsoncLocalization).GetField("CurrentItems", BindingFlags.Static | BindingFlags.NonPublic);
        var all = (Dictionary<string, Dictionary<string, TranslationWorkItem>>)field.GetValue(null);
        foreach (var group in items.GroupBy(item => item.PluginGuid)) all[group.Key] = group.ToDictionary(item => item.Id);
    }
    private static void CheckCaptureRouting()
    {
        var panel = typeof(TranslationPanel);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var captured = Item("Captured setting", "Captured", "capture.plugin");
        panel.GetField("_domain", flags).SetValue(null, WorkbenchDomain.ServerGameText);
        panel.GetField("_captureActive", flags).SetValue(null, true);
        panel.GetMethod("SelectCapturedItem", flags).Invoke(null, new object[] { captured, "captured" });
        Check((WorkbenchDomain)panel.GetField("_domain", flags).GetValue(null) == WorkbenchDomain.F12Settings &&
            !(bool)panel.GetField("_captureActive", flags).GetValue(null) &&
            (int)panel.GetField("_requestedTab", flags).GetValue(null) == 0 &&
            ReferenceEquals(panel.GetField("_pendingCapture", flags).GetValue(null), captured),
            "mouse capture switches from server domain to the matching F12 manual editor");
        panel.GetField("_pendingCapture", flags).SetValue(null, null);
        panel.GetField("_capturedItem", flags).SetValue(null, null);
    }
    public static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--check-scale")
        {
            CheckScalePatches();
            Console.WriteLine("PASS: dropdown geometry and native ConfigurationManager instruction checks.");
            return;
        }
        if (args.Length == 2 && args[0] == "--check-better-bepinex")
        {
            CheckBetterMenuCompatibility(args[1]);
            Console.WriteLine("PASS: installed BetterBepInEx, changed version metadata, and optional and required hook changes.");
            return;
        }
        if (args.Length == 2 && args[0] == "--migrate-storage")
        {
            typeof(Paths).GetProperty("PluginPath", BindingFlags.Public | BindingFlags.Static).SetValue(null, Path.GetFullPath(args[1]));
            JsoncLocalization.MigrateAutomaticFiles();
            Console.WriteLine("Client storage migration completed.");
            return;
        }
        CheckLocaleMainThreadAndRestoration();
        CheckServerPermissionWorkflow();
        CheckAutomaticLiveSync();
        CheckDiscoveryFailureAndRecovery();
        CheckNativeLocaleAdapter();
        Check(TranslationPanel.PrepareRichTextPreview("<size=16px><b>Sense</b></size>") ==
            "<size=16><b>Sense</b></size>", "manual rich-text preview adapts TextMeshPro px size tags for IMGUI");
        Check(TranslationPanel.PrepareRichTextPreview("<color=#ff0000>红色</color>\n<i>斜体</i>") ==
            "<color=#ff0000>红色</color>\n<i>斜体</i>", "manual rich-text preview preserves supported tags and line breaks");
        var reload = args.Length == 2 && args[0] == "--reload";
        var root = reload ? args[1] : Path.Combine(Path.GetTempPath(), "cm-translation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        typeof(Paths).GetProperty("PluginPath", BindingFlags.Public | BindingFlags.Static).SetValue(null, root);
        JsoncLocalization.Initialize(null, LocalizationDisplayMode.Chinese, true, true, true);
        CheckSettingCollectionResilience();
        CheckScalePatches();
        await CheckTextInputTranslations();
        CheckGlossaryEditor();
        await CheckGlossaryAndQuietLogs();
        await CheckExchangePolicy();
        await CheckModelDiscovery();
        var credentialRoot = Path.Combine(root, "credentials");
        TranslationCredentialStore.Initialize(null, credentialRoot);
        var aiCredentials = new Dictionary<string, string> { ["DeepSeek"] = "sk-volcano-test-secret" };
        var machineCredentials = new Dictionary<string, string> { ["Microsoft Translator"] = "machine-test-secret" };
        var appIds = new Dictionary<string, string> { ["百度翻译"] = "app-test-id" };
        if (TranslationCredentialStore.TrySave(aiCredentials, machineCredentials, appIds))
        {
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(TranslationCredentialStore.FilePath)).Contains("sk-volcano-test-secret"),
                "credential file contains no plaintext API key");
            TranslationCredentialSnapshot loadedCredentials;
            Check(TranslationCredentialStore.TryLoad(out loadedCredentials) && loadedCredentials.AiKeys["DeepSeek"] == "sk-volcano-test-secret"
                && loadedCredentials.MachineKeys["Microsoft Translator"] == "machine-test-secret"
                && loadedCredentials.MachineAppIds["百度翻译"] == "app-test-id", "DPAPI credentials restore for the same Windows user");
            Check(TranslationCredentialStore.TryClear() && !File.Exists(TranslationCredentialStore.FilePath), "disabling credential memory deletes its file");
        }
        else
            Check(!File.Exists(TranslationCredentialStore.FilePath) && TranslationCredentialStore.LastError.Contains("内存中有效"),
                "DPAPI-unavailable account fails closed without plaintext fallback");
        CheckUiJson(root);
        if (reload)
        {
            var reloaded = Item("Disable feature"); SetCurrent(reloaded);
            var reloadedProfile = TranslationSafety.Profile("test", "https://local.invalid", "model", "zh", "");
            Check(JsoncLocalization.GetDisplayValue(reloaded) == "禁用人工功能", "manual translation survives process restart");
            Check(JsoncLocalization.PendingAiItems(new[] { reloaded }, reloadedProfile, false).Count == 0, "API fingerprint survives process restart");
            Check(JsoncLocalization.GetStoredValue(Item(guid: "temporary.restart"), TranslationLayer.Machine) == "", "temporary machine absent in fresh process");
            Console.WriteLine("PASS: fresh process persistence checks");
            return;
        }
        Check(TranslationProviderCatalog.MachineProviders.Length == 6, "six official machine providers");
        Check(ServerTranslationFeature.DomainTooltip.Contains("物品") &&
            ServerTranslationFeature.DomainTooltip.Contains("任务") &&
            ServerTranslationFeature.DomainTooltip.Contains("说明") &&
            ServerTranslationFeature.DomainTooltip.Contains("成就") &&
            ServerTranslationFeature.DomainTooltip.Contains("商人") &&
            ServerTranslationFeature.DomainTooltip.Contains("F12"), "server-domain tooltip explains game and F12 scopes");
        ServerTranslationCapabilities serverCapabilities;
        Check(ServerTranslationCapabilities.TryParse(
            "{\"service\":\"SimpleTranslatorCS\",\"enabled\":true,\"serverVersion\":\"1.6.0\",\"protocolVersion\":2,\"fikaDetected\":true,\"writeAuthRequired\":true,\"domains\":[\"item\",\"quest\",\"achievement\",\"trader\"],\"features\":[\"catalog\",\"save-batch\",\"source-groups\",\"hot-reload\"]}",
            out serverCapabilities) && serverCapabilities.Supports("ITEM") && serverCapabilities.Supports("quest")
            && serverCapabilities.SupportsFeature("CATALOG") && serverCapabilities.SupportsFeature("hot-reload")
            && serverCapabilities.FikaDetected && serverCapabilities.WriteAuthRequired,
            "compatible SimpleTranslator capabilities enable server-text domain");
        var serverSnapshot = ServerWorkbenchClient.ParseSnapshot(
            "{\"revision\":\"r1\",\"generatedAt\":\"now\",\"restartRequired\":true," +
            "\"entries\":[{\"id\":\"item Name\",\"domain\":\"item\",\"origin\":\"base\",\"sourceGroup\":\"base-game\",\"sourceGroupName\":\"原版游戏 / 未归属\",\"english\":\"Item\",\"chinese\":\"\"}," +
            "{\"id\":\"quest name\",\"domain\":\"quest\",\"origin\":\"mod\",\"sourceGroup\":\"ExampleMod\",\"sourceGroupName\":\"ExampleMod\",\"english\":\"Quest\",\"chinese\":\"Quest\"}]," +
            "\"sourceGroups\":[{\"id\":\"base-game\",\"name\":\"原版游戏 / 未归属\",\"kind\":\"base\",\"entryCount\":1},{\"id\":\"ExampleMod\",\"name\":\"ExampleMod\",\"kind\":\"mod\",\"entryCount\":1}]," +
            "\"files\":[{\"path\":\"manual.jsonc\",\"kind\":\"manual\",\"enabled\":false,\"entryCount\":2,\"baselineKnown\":true,\"changedCount\":1,\"requiresRestart\":true}]}" );
        Check(serverSnapshot.Entries.Count == 2 && serverSnapshot.CountOrigin("base") == 1
            && serverSnapshot.CountOrigin("mod") == 1 && serverSnapshot.Files.Count == 1
            && !serverSnapshot.Files[0].Enabled && serverSnapshot.Files[0].ChangedCount == 1 && serverSnapshot.Files[0].RequiresRestart
            && serverSnapshot.RestartRequired && serverSnapshot.SourceGroups.Count == 2
            && serverSnapshot.Entries[1].SourceGroup == "ExampleMod", "server workbench snapshot preserves source groups, files and baseline state");
        Check(TranslationSafety.BuildGameTextPrompt("简体中文", "").Contains("游戏文本")
            && !TranslationSafety.BuildGameTextPrompt("简体中文", "").Contains("配置界面"),
            "server game-text prompt is separate from F12 configuration prompt");
        Check(serverSnapshot.CountEntries(new[] { "examplemod" }) == 1 &&
            serverSnapshot.CountEntries(serverSnapshot.SourceGroups.Select(group => group.Id)) == 2,
            "server source count uses case-insensitive indexed groups");
        Check(TranslationPanel.ClampWorkbenchTab(5, WorkbenchDomain.ServerGameText, false) == 2 &&
            TranslationPanel.ClampWorkbenchTab(4, WorkbenchDomain.ServerGameText, false) == 2 &&
            TranslationPanel.ClampWorkbenchTab(5, WorkbenchDomain.ServerGameText, true) == 5 &&
            TranslationPanel.ClampWorkbenchTab(5, WorkbenchDomain.F12Settings, false) == 5,
            "server player tab clamps the requested tab without changing admin and F12 ranges");
        Check(!ServerTranslationCapabilities.TryParse(
            "{\"service\":\"Other\",\"protocolVersion\":1,\"domains\":[\"item\"]}", out serverCapabilities),
            "unrelated service cannot expose server-text domain");
        Check(!ServerTranslationCapabilities.TryParse(
            "{\"service\":\"SimpleTranslatorCS\",\"protocolVersion\":1,\"domains\":[\"dialogue\"]}", out serverCapabilities),
            "capabilities without item or quest stay hidden");
        Check(!ServerTranslationCapabilities.TryParse(
            "{\"service\":\"SimpleTranslatorCS\",\"enabled\":false,\"protocolVersion\":1,\"domains\":[\"item\"]}", out serverCapabilities),
            "disabled server capability stays hidden");
        Check(!ServerTranslationCapabilities.TryParse(
            "{\"service\":\"SimpleTranslatorCS\",\"enabled\":true,\"protocolVersion\":3,\"domains\":[\"item\"]}", out serverCapabilities),
            "unsupported protocol version stays hidden");
        Check(TranslationProviderCatalog.MachineProviders.Count(p => p.WebsiteUrl != null) == 3, "three web providers");
        Check(TranslationProviderCatalog.AiProviders.All(p => Uri.IsWellFormedUriString(p.DocumentationUrl, UriKind.Absolute)), "all AI docs available");
        Check(TranslationProviderCatalog.MachineProviders.All(p => Uri.IsWellFormedUriString(p.DocumentationUrl, UriKind.Absolute)), "all machine docs available");
        Check(TranslationSafety.BuildPrompt("简体中文", "").Contains("Escape from Tarkov / SPT"), "game-specific prompt");
        Check(TranslationSafety.BuildPrompt("简体中文", "").Contains("UIFixes译为UI修复"), "plugin-title prompt requires a Chinese semantic name");
        Check(TranslationSafety.BuildPrompt("简体中文", "词表") == TranslationSafety.BuildPrompt("简体中文", "词表"), "stable prompt prefix");
        var profile = TranslationSafety.Profile("test", "https://local.invalid", "model", "zh", "");
        Check(profile != TranslationSafety.Profile("test", "https://local.invalid", "other", "zh", ""), "model invalidates reuse");
        Check(profile != TranslationSafety.Profile("test", "https://local.invalid", "model", "zh", "extra"), "prompt invalidates reuse");
        Check(profile != TranslationSafety.Profile("test", "https://other.invalid", "model", "zh", ""), "endpoint invalidates reuse");
        Check(JsoncLocalization.FormatPluginTitle("UIFixes", "UI修复") == "UIFixes UI修复", "plugin title keeps English name before Chinese translation");
        Check(JsoncLocalization.FormatPluginTitle("UIFixes", "UIFixes UI修复") == "UIFixes UI修复", "plugin title does not duplicate an existing English prefix");
        CheckCaptureRouting();
        Check(TranslationSafety.Validate("受到的伤害（%）", Item("% Damage received")) == "受到的伤害（%）", "literal percent label is not printf % D");
        Check(TranslationSafety.Validate("允许肢体降至 0 生命值", Item("Allow 0hp on limbs")).Contains("0"), "zero HP translated while numeric zero preserved");
        Reject(() => TranslationSafety.Validate("数值 %d", Item("Value %s")), "actual printf directives remain protected");
        var protectedItem = Item("Press F1: <b>{0}</b> at 50%\nPath C:\\mods\\file.txt");
        Check(TranslationSafety.Validate("按 F1：<b>{0}</b>，50%\n路径 C:\\mods\\file.txt", protectedItem).StartsWith("按"), "valid structured translation");
        Reject(() => TranslationSafety.Validate("按 F2：<b>{0}</b>，50%\n路径 C:\\mods\\file.txt", protectedItem), "shortcut drift");
        Reject(() => TranslationSafety.Validate("按 F1：<b>{1}</b>，50%\n路径 C:\\mods\\file.txt", protectedItem), "placeholder drift");
        Reject(() => TranslationSafety.Validate("按 F1：<b>{0}</b>，60%\n路径 C:\\mods\\file.txt", protectedItem), "numeric drift");
        Reject(() => TranslationSafety.Validate("按 F1：<b>{0}</b>，50% 路径 C:\\mods\\file.txt", protectedItem), "newline drift");
        Reject(() => TranslationSafety.Validate("按 F1：<b>{0}</b>，50\n路径 C:\\mods\\file.txt", protectedItem), "percent drift");
        Reject(() => TranslationSafety.Validate("按 F1：</b>{0}<b>，50%\n路径 C:\\mods\\file.txt", protectedItem), "tag order drift");
        Reject(() => TranslationSafety.Validate("游戏模组", Item("SPT mod")), "game identifier drift");
        Check(TranslationSafety.Validate("未按住 Shift 时限制为鼠标左键拖动。",
            Item("Constrain dragging to the left mouse, when shift is not down." )).Contains("Shift"),
            "shortcut names are case insensitive");
        Check(TranslationSafety.Validate("按住 Ctrl 单击物品。", Item("Control-click your item." )).Contains("Ctrl"),
            "Control and Ctrl are equivalent shortcut names");
        Reject(() => TranslationSafety.Validate("单击物品。", Item("Control-click your item.")), "missing shortcut still rejected");
        Check(TranslationSafety.Validate("显示拾荒者标记", Item("Show Scav Markers")) == "显示拾荒者标记" &&
            TranslationSafety.Validate("显示Scav标记", Item("Show Scav Markers")) == "显示Scav标记", "Scav can follow the glossary or be kept");
        Check(TranslationSafety.Validate("地图页签和速览视图的缩放比例（0 至 1）",
            Item("Zoom for the Map tab and Peek view (0 to 1)")).Contains("地图页签"), "Map tab is an interface label");
        Check(TranslationSafety.Validate("启用归零偏移", Item("Enable Zero Shift")).Contains("偏移") &&
            TranslationSafety.Validate("用 Shift-JIS 编码", Item("Use Shift-JIS encoding")).Contains("编码") &&
            TranslationSafety.Validate("从检视窗口管理此选项", Item("Control this from an inspect window")).Contains("管理"),
            "non-key Shift and Control can be translated in real configuration names and descriptions");
        Check(TranslationSafety.Validate("按住Shift键并单击以多选", Item("Enable multiselect via Shift-click")).Contains("Shift") &&
            TranslationSafety.Validate("用Alt+Tab切换", Item("You must alt-tab out of the game")).Contains("Alt+Tab") &&
            TranslationSafety.Validate("在SPT中使用BepInEx", Item("Use BepInEx in SPT")).Contains("在SPT中"),
            "shortcuts and product names can be adjacent to Chinese");
        Check(TranslationSafety.Validate("按Tab键进入地图页签", Item("Press Tab to open the Map tab")).Contains("Tab"),
            "a key and a navigation tab in one sentence are distinguished");
        Reject(() => TranslationSafety.Validate("打开地图页签", Item("Press Tab to open the Map tab")), "actual Tab key cannot disappear");
        Check(TranslationSafety.Validate("用Shift＋Insert切换", Item("Shift + Insert will toggle the feature")).Contains("Insert") &&
            TranslationSafety.Validate("默认是左Alt+Q", Item("(Default: Q + Left alt)")).Contains("Alt"),
            "combination keys allow full-width separators, reordered keys and localized side names");
        Reject(() => TranslationSafety.Validate("用Shift+Delete切换", Item("Shift + Insert will toggle the feature")), "changed second binding rejected");
        Reject(() => TranslationSafety.Validate("默认是右Alt+Q", Item("(Default: Q + Left alt)")), "changed key side rejected");
        Check(TranslationSafety.Validate("使用鼠标左键（Mouse0）", Item("Use Mouse0 (left mouse button)")).Contains("Mouse0"),
            "full mouse key identifier is preserved");
        Reject(() => TranslationSafety.Validate("使用鼠标左键", Item("Use Mouse0 (left mouse button)")), "mouse button identifier cannot disappear");
        Reject(() => TranslationSafety.Validate("使用Button0", Item("Use Mouse0 (left mouse button)")), "same numeral does not replace a mouse key");
        foreach (var name in new[] { "HBAO", "ACES", "ACESS", "SMAA", "NVG" })
            Check(TranslationSafety.Validate(name, TranslationWorkItem.ForSetting("graphics", name, "DispName", name, name)) == name,
                "known graphics label can retain its source: " + name);
        foreach (var name in new[] { "Shift", "Control", "Alt" })
            Check(TranslationSafety.Validate(name, TranslationWorkItem.ForEnum("uifixes", "UIFixes.ModifierKey", name, name, name)) == name,
                "typed modifier enum can retain its source: " + name);
        Reject(() => TranslationSafety.Validate("Control", TranslationWorkItem.ForEnum("fixture", "Example.DisplayMode", "Control", "Control", "label")),
            "unrelated enum echoes still need translation");
        var serverText = TranslationWorkItem.ForServerText("fixture", "A scav waits here", "Server fixture");
        Check(TranslationSafety.Validate("这里有一名 Scav 等候", serverText).Contains("Scav"), "server draft keeps role names under existing server validation");
        Reject(() => TranslationSafety.Validate("这里有一名拾荒者等候", serverText),
            "configuration glossary flexibility does not create incompatible server drafts");
        var pluginTitle = TranslationWorkItem.ForPlugin("title.plugin", "Tyfon.UIFixes");
        pluginTitle.PluginName = "Tyfon.UIFixes";
        pluginTitle.PluginVersion = "1.0";
        Reject(() => TranslationSafety.Validate("Tyfon.UIFixes", pluginTitle), "unchanged English plugin title remains pending");
        Check(TranslationSafety.Validate("UI修复", pluginTitle) == "UI修复", "Chinese plugin title accepted");
        var nameIds = new Dictionary<string, TranslationWorkItem> { ["t0001"] = pluginTitle };
        Check(TranslationSafety.ParsePartialResponse("{\"t0001\":\"Tyfon UI Fixes\"}", nameIds, "zh-CN").Failures[pluginTitle.Id]
            .Contains("插件标题未生成中文"), "rephrased English title rejected for Chinese API jobs");
        Check(TranslationSafety.ParsePartialResponse("{\"t0001\":\"Korrekturen für die Oberfläche\"}", nameIds, "de").Values.ContainsKey(pluginTitle.Id),
            "non-Chinese target keeps its existing language behavior");
        var richControl = TranslationWorkItem.ForCustom("uifixes", "Modify Equipped Armor Plates In Raid",
            "<color=grey>Disabled (Incompatible with Fika)</color>", "custom");
        var plain = "关闭（与Fika不兼容）";
        Check(TranslationSafety.Validate(TranslationSafety.PreserveOuterFormatting(richControl.Source, plain), richControl, true) ==
            "<color=grey>关闭（与Fika不兼容）</color>", "manual custom text restores its original outer color");
        Check(TranslationSafety.PreserveOuterFormatting("<b><i>Example</i></b>", "示例") == "<b><i>示例</i></b>",
            "nested outer formatting is restored in order");
        Check(TranslationSafety.PreserveOuterFormatting("<b>On</b> or <i>Off</i>", "开启或关闭") == "开启或关闭",
            "partial formatting is not guessed");
        Reject(() => TranslationSafety.Validate(plain, richControl), "API output still has to retain formatting");
        JsoncLocalization.SaveLayer(richControl.PluginGuid, TranslationLayer.Manual, new[] { richControl }, Values(richControl, plain));
        Check(JsoncLocalization.GetStoredValue(richControl, TranslationLayer.Manual) ==
            "<color=grey>关闭（与Fika不兼容）</color>", "saving plain manual text persists the original color wrapper");
        var titleSidecar = new JObject();
        TranslationTracking.Record(titleSidecar, "AI", pluginTitle, "Tyfon.UIFixes", profile);
        Check(!TranslationTracking.CanReuse(titleSidecar, pluginTitle, "Tyfon.UIFixes", profile),
            "unchanged English plugin title is not reused from AI cache");
        var item = Item();
        var ids = new Dictionary<string, TranslationWorkItem> { ["t0001"] = item };
        Check(TranslationSafety.ParseResponse("{\"t0001\":\"启用功能\"}", ids)[item.Id] == "启用功能", "strict JSON accepted");
        Check(TranslationSafety.ParseResponse("```json\n{\"t0001\":\"启用功能\"}\n```", ids).Count == 1, "single JSON fence accepted");
        Reject(() => TranslationSafety.ParseResponse("解释：{\"t0001\":\"启用功能\"}", ids), "prose rejected");
        Reject(() => TranslationSafety.ParseResponse("{\"t0001\":\"甲\",\"t0001\":\"乙\"}", ids), "duplicate IDs rejected");
        Reject(() => TranslationSafety.ParseResponse("{\"t0001\":\"甲\",\"extra\":\"乙\"}", ids), "extra IDs rejected");
        Reject(() => TranslationSafety.ParseResponse("{}", ids), "missing IDs rejected");
        Reject(() => TranslationSafety.ParseResponse("{\"t0001\":123}", ids), "non-string rejected");
        var entries = new Dictionary<string, TranslationWorkItem>
        {
            ["t0001"] = item, ["t0002"] = Item(key: "Duplicate"), ["t0003"] = Item(key: "Missing"),
            ["t0004"] = Item(key: "NonString")
        };
        var partialReply = TranslationSafety.ParsePartialResponse(
            "{\"t0001\":\"启用功能\",\"t0002\":\"甲\",\"t0002\":\"乙\",\"t0004\":123}", entries);
        Check(partialReply.Values[item.Id] == "启用功能" && partialReply.Failures[entries["t0002"].Id].Contains("重复 ID") &&
            partialReply.Failures[entries["t0003"].Id].Contains("缺少 ID") &&
            partialReply.Failures[entries["t0004"].Id].Contains("不是字符串"), "partial JSON saves valid IDs and reports exact field failures");
        Reject(() => TranslationSafety.ParsePartialResponse("{\"t0001\":\"启用功能\"", ids), "truncated JSON cannot be saved");
        Reject(() => TranslationSafety.ParsePartialResponse("{\"t0001\":\"启用功能\"}{}", ids), "trailing JSON content cannot be saved");

        var baidu = TranslationClients.BaiduForm("apple", "2015063000000001", "12345678", "en", "zh", "1435660288");
        Check(baidu["sign"] == "f89f9594663708c1605f3d736d01d2d4", "Baidu published signature fixture");
        var youdao = TranslationClients.YoudaoForm("abcdefghijklmnopqrstuvwxy", "id", "secret", "en", "zh-CHS", "salt", "1");
        Check(youdao["sign"] == TranslationSafety.Hash("idabcdefghij25pqrstuvwxysalt1secret"), "Youdao long input fixture");
        Check(youdao["signType"] == "v3" && youdao["curtime"] == "1" && youdao["q"] == "abcdefghijklmnopqrstuvwxy", "Youdao v3 fields");

        SetCurrent(item);
        JsoncLocalization.SaveLayer(item.PluginGuid, TranslationLayer.Machine, new[] { item }, Values(item, "临时译文"));
        Check(File.Exists(JsoncLocalization.GetTranslationPath(item.PluginGuid, TranslationLayer.Machine)),
            "machine translation stored under MT folder");
        Check(JsoncLocalization.GetDisplayValue(item) == "临时译文", "durable machine displayed");
        var changed = Item("Disable feature"); SetCurrent(changed);
        Check(JsoncLocalization.GetDisplayValue(changed) == changed.Source, "temporary result invalidated by source change");
        Check(JsoncLocalization.GetStoredValue(changed, TranslationLayer.Machine) == string.Empty,
            "stale durable machine value is hidden from review and missing scans");
        SetCurrent(item);
        JsoncLocalization.ClearTemporary(item.PluginGuid);
        Check(JsoncLocalization.GetDisplayValue(item) == "临时译文", "clearing web temporary layer keeps durable MT file");
        JsoncLocalization.SaveLayer(item.PluginGuid, TranslationLayer.AI, new[] { item }, Values(item, "启用功能"), profile);
        var path = JsoncLocalization.GetTranslationPath(item.PluginGuid);
        var sidecar = JObject.Parse(File.ReadAllText(path));
        var api = JObject.Parse(File.ReadAllText(JsoncLocalization.GetTranslationPath(item.PluginGuid, TranslationLayer.AI)));
        Check(sidecar["Machine"] == null && sidecar["AI"] == null && sidecar["Tracking"] != null && api["AI"] != null,
            "API translations classified away from tracking sidecar");
        Check(JsoncLocalization.PendingAiItems(new[] { item }, profile, false).Count == 0, "repeat AI work skipped");
        Check(JsoncLocalization.PendingAiItems(new[] { item }, "other", false).Count == 1, "different model/profile requested");
        Check(JsoncLocalization.PendingAiItems(new[] { item }, profile, true).Count == 1, "force ignores durable cache");
        JsoncLocalization.SaveLayer(item.PluginGuid, TranslationLayer.Manual, new[] { item }, Values(item, "人工译文"));
        Check(JsoncLocalization.GetDisplayValue(item) == "人工译文", "manual priority");
        Check(Directory.GetFiles(JsoncLocalization.GetHistoryFolder(item.PluginGuid), "*.jsonc").Length >= 1, "previous sidecar archived");
        SetCurrent(changed);
        Check(JsoncLocalization.GetDisplayValue(changed) == changed.Source, "stale manual and AI suppressed");
        sidecar = JObject.Parse(File.ReadAllText(path));
        Check(TranslationTracking.Compare(sidecar, new[] { changed }).Single().State == "已修改", "source changes detected");
        var added = Item("New option", "New");
        Check(TranslationTracking.Compare(sidecar, new[] { item, added }).Single().State == "新增", "added option detected");
        Check(TranslationTracking.Compare(sidecar, new TranslationWorkItem[0]).Single().State == "已移除", "removed option detected");
        JsoncLocalization.SaveLayer(changed.PluginGuid, TranslationLayer.AI, new[] { changed }, Values(changed, "禁用功能"), profile);
        Check(JsoncLocalization.GetDisplayValue(changed) == "禁用功能", "repaired AI used while manual remains stale");
        sidecar = JObject.Parse(File.ReadAllText(path));
        Check(TranslationTracking.Compare(sidecar, new[] { changed }).Count == 1, "AI repair does not approve stale manual");
        JsoncLocalization.SaveLayer(changed.PluginGuid, TranslationLayer.Manual, new[] { changed }, Values(changed, "禁用人工功能"));
        sidecar = JObject.Parse(File.ReadAllText(path));
        Check(TranslationTracking.Compare(sidecar, new[] { changed }).Count == 0, "manual review resolves final diff");
        var originalFile = File.ReadAllText(path);
        File.WriteAllText(path, "{ invalid jsonc");
        Reject(() => JsoncLocalization.SaveLayer(item.PluginGuid, TranslationLayer.Manual, new[] { item }, Values(item, "甲")), "malformed files not overwritten");
        Check(File.ReadAllText(path) == "{ invalid jsonc", "corrupt original retained");
        File.WriteAllText(path, originalFile);

        var legacy = Item(guid: "legacy.plugin"); SetCurrent(legacy);
        var legacyPath = Path.Combine(root, "zh-cn", "legacy.plugin.jsonc");
        var legacyText = "// preserve this comment\n{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"旧人工译文\"}}}";
        File.WriteAllText(legacyPath, legacyText);
        Check(JsoncLocalization.GetLegacyTranslationPath(legacy.PluginGuid) == legacyPath,
            "legacy manual file exposes an open-button path");
        JsoncLocalization.SaveLayer(legacy.PluginGuid, TranslationLayer.AI, new[] { legacy }, Values(legacy, "AI译文"), profile);
        Check(File.ReadAllText(legacyPath) == legacyText, "legacy comments never rewritten");
        var legacyChanged = Item("Disable feature", guid: legacy.PluginGuid); SetCurrent(legacyChanged);
        JsoncLocalization.SaveLayer(legacy.PluginGuid, TranslationLayer.AI, new[] { legacyChanged }, Values(legacyChanged, "禁用功能"), profile);
        Check(JsoncLocalization.GetDisplayValue(legacyChanged) == "禁用功能", "stale legacy blocked after AI repair");
        JsoncLocalization.SaveLayer(legacy.PluginGuid, TranslationLayer.Manual, new[] { legacyChanged }, Values(legacyChanged, "新人工译文"));
        JsoncLocalization.SaveLayer(legacy.PluginGuid, TranslationLayer.Manual, new[] { legacyChanged }, Values(legacyChanged, ""));
        Check(JsoncLocalization.GetDisplayValue(legacyChanged) == "禁用功能", "clearing override cannot revive stale legacy");
        Reject(() => JsoncLocalization.GetTranslationPath("../escape"), "path traversal rejected");

        var migration = Item(guid: "migration.plugin"); SetCurrent(migration);
        var migrationPath = JsoncLocalization.GetTranslationPath(migration.PluginGuid);
        File.WriteAllText(migrationPath, "// old machine data\n{\"Machine\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"旧机翻\"}}}}");
        typeof(JsoncLocalization).GetMethod("MigrateAutomaticFiles", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        Check(JObject.Parse(File.ReadAllText(migrationPath))["Machine"] == null
            && JObject.Parse(File.ReadAllText(JsoncLocalization.GetTranslationPath(migration.PluginGuid, TranslationLayer.Machine)))["Machine"] != null,
            "legacy machine layer classified into MT folder");
        Check(JsoncLocalization.GetDisplayValue(migration) == "旧机翻", "migrated machine translation remains available");
        JsoncLocalization.SaveLayer(migration.PluginGuid, TranslationLayer.Manual, new[] { migration }, Values(migration, "新译文"));
        Check(JObject.Parse(File.ReadAllText(migrationPath))["Machine"] == null, "active machine layer removed on durable save");
        Check(File.ReadAllText(JsoncLocalization.GetTranslationPath(migration.PluginGuid, TranslationLayer.Machine)).Contains("旧机翻"),
            "legacy machine retained in classified MT file");
        var temporary = Item(guid: "temporary.restart");
        JsoncLocalization.StoreTemporary(temporary.PluginGuid, new[] { temporary }, Values(temporary, "临时"));
        var processInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        processInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        processInfo.ArgumentList.Add("--reload"); processInfo.ArgumentList.Add(root);
        using (var process = Process.Start(processInfo))
        {
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "fresh process checks: " + error);
            Check(output.Contains("PASS"), "fresh process confirms durability and cache separation");
        }

        var qualified = Item(guid: "qualified.plugin");
        var qualifiedSnapshot = new LocalizationSnapshot { Guid = qualified.PluginGuid, PluginName = "Qualified", PluginVersion = "1.0" };
        qualifiedSnapshot.Settings.Add(new SettingSnapshot { StorageKey = "Enabled", Category = "General", DispName = "Enabled", Description = qualified.Source });
        var snapshots = (Dictionary<string, LocalizationSnapshot>)typeof(JsoncLocalization).GetField("Snapshots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        snapshots[qualified.PluginGuid] = qualifiedSnapshot;
        SetCurrent(qualified);
        File.WriteAllText(Path.Combine(root, "zh-cn", "qualified.plugin.jsonc"), "{\"ConfigEntries\":{\"[General]Enabled\":{\"Description\":\"分类键人工译文\"}}}");
        JsoncLocalization.EstablishBaseline(qualifiedSnapshot);
        Check(JsoncLocalization.GetStoredValue(qualified, TranslationLayer.Manual) == "分类键人工译文", "qualified legacy key editable");
        var qualifiedChanged = Item("Disable feature", guid: qualified.PluginGuid); SetCurrent(qualifiedChanged);
        JsoncLocalization.SaveLayer(qualified.PluginGuid, TranslationLayer.AI, new[] { qualifiedChanged }, Values(qualifiedChanged, "禁用功能"), profile);
        Check(JsoncLocalization.GetDisplayValue(qualifiedChanged) == "禁用功能", "qualified legacy key also protected against stale fallback");

        await CheckBatch(root, profile);
        CheckAppearance(root);
        await CheckDiagnostics();
        await CheckPluginTitleBatch();
        CheckStorageLayout();
        await CheckHttp();
        CheckPluginSelection();
        var webItem = Item(guid: "web-conflict.plugin");
        SetCurrent(webItem);
        var priorWebText = new Dictionary<string, string> { [webItem.Id] = "" };
        var initialWebText = new Dictionary<string, string> { [webItem.Id] = "启用功能" };
        JsoncLocalization.SaveWebLayer(webItem.PluginGuid, TranslationLayer.Manual, new[] { webItem }, priorWebText, initialWebText);
        Reject(() => JsoncLocalization.SaveWebLayer(webItem.PluginGuid, TranslationLayer.Manual, new[] { webItem },
            priorWebText, new Dictionary<string, string> { [webItem.Id] = "过期译文" }), "web draft cannot overwrite a newer manual translation");
        Check(JsoncLocalization.GetStoredValue(webItem, TranslationLayer.Manual) == "启用功能", "web conflict preserves the saved translation");
        CheckAuditFixes();
        _assertions += _auditAssertions;
        Console.WriteLine("PASS: " + _assertions + " regression assertions; real DLL, temporary files, local HTTP fixtures only.");
        Console.WriteLine("Test artifacts: " + root);
    }

    private static void CheckUiJson(string root)
    {
        var folder = Path.Combine(root, "zh-cn");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "UI.json"), @"{
  ""Close"": ""关闭"",
  ""$PluginTitles"": { ""title.fixture"": { ""ExamplePlugin"": ""示例插件"" } },
  ""$Settings"": {
    ""com.bepis.bepinex.configurationmanager"": {
      ""[General]Show config manager"": {
        ""Category"": ""常规"",
        ""DispName"": ""打开配置管理器"",
        ""Description"": ""快捷键说明""
      }
    },
    ""BepInEx"": {
      ""[Logging.Console]Enabled"": {
        ""Category"": ""控制台日志"",
        ""DispName"": ""启用控制台""
      }
    }
  }
}");
        Check(UiLocalization.Get("Close") == "关闭", "flat UI text loaded from UI.json");
        var fixture = new LocalizationSnapshot { Guid = "title.fixture", PluginName = "ExamplePlugin" };
        var title = fixture.CreateWorkItems().Single();
        Check(JsoncLocalization.GetDisplayValue(title) == "ExamplePlugin" &&
            !JsoncLocalization.GetInventory(fixture).ValidIds.Contains(title.Id),
            "retired UI title entries do not affect plugin names or translation inventory");
        JsoncLocalization.SaveLayer(fixture.Guid, TranslationLayer.Manual, new[] { title }, Values(title, "个人标题"));
        Check(JsoncLocalization.GetDisplayValue(title) == "ExamplePlugin 个人标题", "manual title comes from the plugin dictionary");
        JsoncLocalization.SaveLayer(fixture.Guid, TranslationLayer.Manual, new[] { title }, Values(title, ""));
        Check(UiLocalization.GetSettingValue("com.bepis.bepinex.configurationmanager", "General", "Show config manager", "DispName") == "打开配置管理器",
            "ConfigurationManager hotkey label loaded from scoped UI.json entry");
        Check(UiLocalization.GetSettingValue("BepInEx", "Logging.Console", "Enabled", "Category") == "控制台日志",
            "BepInEx core setting loaded from scoped UI.json entry");
        Check(UiLocalization.GetSettingValue("another.plugin", "Logging.Console", "Enabled", "DispName") == null,
            "scoped UI settings do not translate matching fields in unrelated plugins");
        JsoncLocalization.SetDisplayMode(LocalizationDisplayMode.English);
        Check(UiLocalization.Get("Close") == "Close", "UI.json respects English display mode");
        JsoncLocalization.SetDisplayMode(LocalizationDisplayMode.Chinese);
    }

    private static void CheckAppearance(string root)
    {
        var file = Path.Combine(root, "appearance.cfg");
        var appearance = new WorkbenchAppearance(new BepInEx.Configuration.ConfigFile(file, true));
        Check(appearance.GuiScale.Value == 1f && appearance.GuiScale.Description.AcceptableValues is BepInEx.Configuration.AcceptableValueRange<float>, "GUI scale exposes a native range slider with default 100 percent");
        Check(WorkbenchAppearance.DefaultScale(2560, 1440) == 1f &&
            WorkbenchAppearance.DefaultScale(3840, 2160) == 2f, "4K defaults to 200 percent");
        appearance.GuiScale.Value = 2f;
        Check(new WorkbenchAppearance(new BepInEx.Configuration.ConfigFile(file, true)).GuiScale.Value == 2f, "GUI scale persists across restart");
        appearance.GuiScale.Value = 1f;
        Check(new WorkbenchAppearance(new BepInEx.Configuration.ConfigFile(file, true), 2f).GuiScale.Value == 1f,
            "automatic 4K default preserves an explicitly saved 100 percent scale");
        appearance.GuiScale.Value = 99f;
        Check(appearance.GuiScale.Value == 3f, "native scale range clamps manual values");
        Check(ConfigurationManagerScale.FitScale(2f, 3840, 2160) == 2f && ConfigurationManagerScale.FitScale(3f, 3840, 2160) == 3f, "4K supports 200 and 300 percent");
        Check(ConfigurationManagerScale.FitScale(3f, 1920, 1080) == 2f && ConfigurationManagerScale.FitScale(3f, 640, 480) == 1f, "scale retains minimum usable logical screen space");
        Check(ConfigurationManagerScale.FitScale(float.NaN, 3840, 2160) == 1f, "invalid scale cannot poison GUI matrix");
        var longStatus = "部分失败：已完成 1 / 21 个插件。\n" + new string('错', 100000);
        Check(TranslationPanel.JobStatusSummary(longStatus) == "部分失败：已完成 1 / 21 个插件。", "status summary hides multiline errors");
        Check(TranslationPanel.JobStatusSummary(new string('错', 100000)).Length == 101 && TranslationPanel.JobStatusDetails(longStatus).Length < 8100,
            "unbroken and huge provider errors are bounded in the drawer");
        Check(appearance.ToolbarExpanded.Value && appearance.Preset.Value == WorkbenchAppearance.Presets[0], "first launch expands toolbar and keeps existing dark theme");
        appearance.ToolbarExpanded.Value = false;
        appearance.Preset.Value = WorkbenchAppearance.Presets[1];
        Check(appearance.GetPalette().SequenceEqual(WorkbenchAppearance.ClassicColors), "classic preset uses neutral original-style palette");
        var copy = appearance.GetPalette(); copy[0] = "#FFFFFF";
        Check(appearance.GetPalette()[0] != copy[0], "editing draft cannot mutate saved preset");
        var reloaded = new WorkbenchAppearance(new BepInEx.Configuration.ConfigFile(file, true));
        Check(!reloaded.ToolbarExpanded.Value && reloaded.Preset.Value == WorkbenchAppearance.Presets[1], "collapsed toolbar and selected preset survive reload");
        var custom = appearance.GetPalette(); custom[3] = "#abcdef";
        Check(reloaded.ApplyCustom(custom) && reloaded.GetPalette()[3] == "#ABCDEF", "valid custom palette normalized and applied");
        var revision = reloaded.Revision;
        custom[0] = "#112233"; custom[6] = "invalid";
        Check(!reloaded.ApplyCustom(custom) && reloaded.Revision == revision && reloaded.GetPalette()[0] != "#112233", "invalid color rejects entire draft without partial saving");
        var loadedCustom = new WorkbenchAppearance(new BepInEx.Configuration.ConfigFile(file, true));
        Check(loadedCustom.Preset.Value == WorkbenchAppearance.Presets[2] && loadedCustom.GetPalette()[3] == "#ABCDEF", "custom colors survive reload");
        loadedCustom.Colors[0].Value = "broken external cfg";
        Check(loadedCustom.GetPalette()[0] == WorkbenchAppearance.DarkColors[0], "invalid externally edited color has safe fallback");
        UnityEngine.Color parsed;
        Check(WorkbenchAppearance.TryParse("#000000", out parsed) && WorkbenchAppearance.ToHex(parsed) == "#000000", "black color round trips");
        Check(WorkbenchAppearance.TryParse(" FFFFFF ", out parsed) && WorkbenchAppearance.ToHex(parsed) == "#FFFFFF", "white color and optional prefix round trip");
        Check(!WorkbenchAppearance.TryParse("##123456", out parsed) && !WorkbenchAppearance.TryParse("#12345G", out parsed), "malformed HEX colors rejected");
        loadedCustom.ToolbarExpanded.Value = true;
        Check(new WorkbenchAppearance(new BepInEx.Configuration.ConfigFile(file, true)).ToolbarExpanded.Value, "manually re-expanded toolbar also persists");
    }

    private static async Task CheckBatch(string root, string profile)
    {
        var offline = new LocalizationSnapshot { Guid = "offline.fixture", PluginName = "Offline", PluginVersion = "1.0" };
        offline.Settings.Add(new SettingSnapshot { StorageKey = "Fade", Category = "Map", DispName = "Fade", Description = "Marker opacity" });
        offline.EnumValues.Add(new EnumValueSnapshot { TypeName = "Fixture.PlaybackMode", MemberName = "Fast" });
        var offlineItems = offline.CreateWorkItems();
        SetCurrent(offlineItems.ToArray());
        var imported = new JObject { ["AI"] = new JObject
        {
            ["ConfigEntries"] = new JObject { ["[Map]Fade"] = new JObject { ["DispName"] = "图标淡出" } },
            ["Enum"] = new JObject { ["PlaybackMode"] = new JObject { ["Fast"] = "快速" } }
        } };
        var offlineDirectory = Path.Combine(root, "zh-cn", "translations");
        Directory.CreateDirectory(offlineDirectory);
        File.WriteAllText(Path.Combine(offlineDirectory, offline.Guid + "_API.jsonc"), imported.ToString());
        Check(JsoncLocalization.GetStoredValue(offlineItems.Single(item => item.Field == "DispName"), TranslationLayer.AI) == "图标淡出" &&
            JsoncLocalization.GetStoredValue(offlineItems.Single(item => item.Kind == "Enum"), TranslationLayer.AI) == "快速" &&
            JsoncLocalization.GetInventory(offline).Ai == 2, "CFG dictionary is read and counted as valid by the live F12 inventory");
        var importedName = offlineItems.Single(item => item.Field == "DispName");
        JsoncLocalization.SaveLayer(offline.Guid, TranslationLayer.AI, new[] { importedName }, Values(importedName, ""), profile);
        Check(JsoncLocalization.GetStoredValue(importedName, TranslationLayer.AI) == "", "clearing an imported CFG translation clears its qualified key");

        var first = new LocalizationSnapshot { Guid = "batch.first", PluginName = "First", PluginVersion = "1.0" };
        var second = new LocalizationSnapshot { Guid = "batch.second", PluginName = "Second", PluginVersion = "1.0" };
        foreach (var snapshot in new[] { first, second })
        {
            snapshot.Settings.Add(new SettingSnapshot { StorageKey = "Enabled", Category = "General", DispName = "Enabled", Description = "Enable feature" });
            SetCurrent(snapshot.CreateWorkItems().ToArray());
        }
        Check(!JsoncLocalization.GetInventory(first).HasFiles, "missing JSONC marked untranslated");
        JsoncLocalization.EstablishBaseline(first);
        var inventory = JsoncLocalization.GetInventory(first);
        Check(inventory.HasFiles && inventory.Translated == 0, "baseline-only JSONC is not fully translated");
        var items = first.CreateWorkItems();
        var description = items.Single(item => item.Field == "Description");
        JsoncLocalization.SaveLayer(first.Guid, TranslationLayer.Manual, new[] { description }, Values(description, "人工说明"));
        inventory = JsoncLocalization.GetInventory(first);
        Check(inventory.Translated == 1 && inventory.Total == 4 && inventory.Manual == 1, "partial JSONC coverage counts fields");
        JsoncLocalization.SaveLayer(first.Guid, TranslationLayer.AI, new[] { description }, Values(description, "AI说明"), profile);
        inventory = JsoncLocalization.GetInventory(first);
        Check(inventory.Overlap == 1 && inventory.Translated == 1, "overlap counted once in coverage");
        var selected = new HashSet<string> { first.Guid, second.Guid };
        var snapshots = new[] { first, second };
        var scope = new TranslationScope { Descriptions = true };
        var plan = TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, true, true);
        Check(plan.SelectedPlugins == 2 && plan.Targets.Count == 1 && plan.Targets[0].Guid == second.Guid && plan.TotalItems == 1 && plan.SkippedItems == 1,
            "default protection wins over force and same IDs across plugins stay separate");
        JsoncLocalization.SetEnabledLayers(false, false, false);
        Check(JsoncLocalization.GetDisplayValue(description) == description.Source, "all module switches off restore original");
        Check(TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, true, true).SkippedItems == 1,
            "disabled module does not make persisted translations missing");
        JsoncLocalization.StoreTemporary(first.Guid, new[] { description }, Values(description, "机翻说明"));
        JsoncLocalization.SetEnabledLayers(false, false, true);
        Check(JsoncLocalization.GetDisplayValue(description) == "机翻说明", "machine module switch takes effect");
        JsoncLocalization.SetEnabledLayers(false, true, true);
        Check(JsoncLocalization.GetDisplayValue(description) == "AI说明", "AI module switch takes priority over machine");
        JsoncLocalization.SetEnabledLayers(true, true, true);
        Check(JsoncLocalization.GetDisplayValue(description) == "人工说明", "manual module switch takes priority over AI");

        plan = TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, true, false);
        Check(plan.Targets.Count == 2 && plan.AiOverwrites == 1 && plan.ManualOverlaps == 1, "explicit update warns of AI replacement and manual overlap");
        Check(TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, false, false).TotalItems == 1, "same API still reused without force");
        Check(TranslationBatchPlan.Create(snapshots, selected, scope, false, profile, false, true).TotalItems == 1, "machine batch also protects durable translations");
        selected.Clear();
        Check(plan.Targets.Count == 2 && TranslationBatchPlan.Create(snapshots, selected, scope, false, profile, false, false).TotalItems == 0,
            "empty selection does not fall back to focus and existing job plan is frozen");
        selected.UnionWith(snapshots.Select(snapshot => snapshot.Guid));
        Check(TranslationBatchPlan.Create(snapshots, selected, new TranslationScope(), true, profile, true, false).TotalItems == 0, "empty field scope has no batch work");
        JsoncLocalization.SaveLayer(first.Guid, TranslationLayer.Manual, items, items.ToDictionary(item => item.Id, item => item.Source));
        inventory = JsoncLocalization.GetInventory(first);
        Check(inventory.Translated == 4 && inventory.Label == "已汉化", "same-as-source accepted translations still count as complete");
        first.Settings[0].Description = "Disable feature";
        SetCurrent(first.CreateWorkItems().ToArray());
        inventory = JsoncLocalization.GetInventory(first);
        Check(inventory.Stale == 1 && inventory.Translated == 3, "changed source marked stale, not already translated");
        Check(TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, false, true).TotalItems == 2, "stale translation eligible for repair with protection enabled");
        File.WriteAllText(Path.Combine(root, "zh-cn", second.Guid + ".jsonc"), "{ invalid jsonc");
        Check(JsoncLocalization.GetInventory(second).Error != null, "invalid JSONC marked as error");
        Check(TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, true, false).Errors.Count == 1, "damaged selected JSONC reported before running");
        File.WriteAllText(Path.Combine(root, "zh-cn", second.Guid + ".jsonc"), "{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"旧译文\"}}}");
        Check(JsoncLocalization.GetInventory(second).Manual == 1, "legacy JSONC recognized as manual localization");

        Func<TranslationBatchTarget, TranslationResult> output = target =>
        {
            var result = new TranslationResult();
            foreach (var item in target.Items) result.Values[item.Id] = target.Name + "译文";
            return result;
        };
        plan = TranslationBatchPlan.Create(snapshots, selected, scope, true, profile, true, false);
        var saved = new List<string>();
        var progress = 0;
        var report = await TranslationBatchRunner.RunAsync(plan, (target, update, token) => Task.FromResult(output(target)),
            (target, result) =>
            {
                JsoncLocalization.SaveLayer(target.Guid, TranslationLayer.AI, target.Items, result.Values, profile);
                saved.Add(target.Guid);
            }, (name, done, total) => { Check(done >= progress && done <= total, "aggregate progress stays within batch"); progress = done; }, CancellationToken.None);
        Check(report.CompletedPlugins == 2 && report.SavedItems == 2 && saved.SequenceEqual(new[] { first.Guid, second.Guid }), "batch saves every selected plugin in order");
        Check(JsoncLocalization.GetStoredValue(plan.Targets[0].Items[0], TranslationLayer.AI) == "First译文" &&
            JsoncLocalization.GetStoredValue(plan.Targets[1].Items[0], TranslationLayer.AI) == "Second译文", "same field ID routed to correct plugin JSONC");
        var checkpointItems = new[] { Item("First text", "One", "checkpoint.plugin"), Item("Second text", "Two", "checkpoint.plugin"), Item("Third text", "Three", "checkpoint.plugin") };
        var checkpointPlan = new TranslationBatchPlan();
        checkpointPlan.Targets.Add(new TranslationBatchTarget { Guid = "checkpoint.plugin", Name = "Checkpoint", Items = checkpointItems });
        var checkpointSaved = new List<string>();
        var checkpointCalls = 0;
        var checkpointReport = await TranslationBatchRunner.RunAsync(checkpointPlan, (target, update, token) =>
        {
            checkpointCalls++;
            if (checkpointCalls == 2) throw new IOException("checkpoint failure");
            return Task.FromResult(output(target));
        }, (target, result) => checkpointSaved.AddRange(target.Items.Select(value => value.Id)), null, CancellationToken.None, 1);
        Check(checkpointReport.SavedItems == 1 && checkpointReport.Failures.Count == 1 && checkpointSaved.SequenceEqual(new[] { checkpointItems[0].Id }),
            "large plugin persists completed checkpoints before a later failure");

        var partialPlan = new TranslationBatchPlan();
        partialPlan.Targets.Add(new TranslationBatchTarget { Guid = "partial.plugin", Name = "Partial", Items = checkpointItems.Take(2).ToList() });
        checkpointSaved.Clear();
        var partialReport = await TranslationBatchRunner.RunAsync(partialPlan, (target, update, token) =>
        {
            var result = new TranslationResult();
            result.Values[target.Items[0].Id] = "已翻译";
            result.Failures[target.Items[1].Id] = "temporary failure";
            return Task.FromResult(result);
        }, (target, result) => checkpointSaved.AddRange(target.Items.Select(value => value.Id)), null, CancellationToken.None);
        Check(partialReport.SavedItems == 1 && partialReport.CompletedPlugins == 0 && partialReport.Failures.Count == 1 && checkpointSaved.Single() == checkpointItems[0].Id,
            "valid entries from a partial API result are saved without the rejected entry");
        saved.Clear();
        report = await TranslationBatchRunner.RunAsync(plan, (target, update, token) =>
        {
            if (target.Guid == first.Guid) throw new IOException("simulated failure");
            return Task.FromResult(output(target));
        }, (target, result) => saved.Add(target.Guid), null, CancellationToken.None);
        Check(report.Failures.Count == 1 && report.CompletedPlugins == 1 && saved.Single() == second.Guid, "failed plugin does not prevent later plugins");
        saved.Clear();
        using (var cancellation = new CancellationTokenSource())
        {
            report = await TranslationBatchRunner.RunAsync(plan, async (target, update, token) =>
            {
                await Task.Yield();
                if (target.Guid == second.Guid) cancellation.Cancel();
                return output(target);
            }, (target, result) => saved.Add(target.Guid), null, cancellation.Token);
            Check(report.Cancelled && report.CompletedPlugins == 1 && saved.Single() == first.Guid, "cancel retains completed plugin and discards in-flight result");
        }
        saved.Clear();
        report = await TranslationBatchRunner.RunAsync(plan, (target, update, token) => Task.FromResult(new TranslationResult()),
            (target, result) => saved.Add(target.Guid), null, CancellationToken.None);
        Check(report.Failures.Count == 2 && saved.Count == 0, "incomplete results cannot save either plugin");
    }

    private static async Task CheckPluginTitleBatch()
    {
        using var server = new MockServer();
        var plan = new TranslationBatchPlan();
        var first = TranslationWorkItem.ForPlugin("grouped.title.first", "Useful Map");
        var second = TranslationWorkItem.ForPlugin("grouped.title.second", "Other Map");
        first.RelatedDescription = "Shows the mission map.";
        foreach (var title in new[] { first, second })
            plan.Targets.Add(new TranslationBatchTarget { Guid = title.PluginGuid, Name = title.Source,
                Items = new[] { title, Item(guid: title.PluginGuid) } });
        var provider = new TranslationProviderDefinition { Name = "title-fixture", ApiKeyRequired = false };
        var writeLog = new List<string>();
        var observed = new List<TranslationWorkItem>();
        var ordinaryReply = server.Reply;
        server.Reply = request =>
        {
            var messages = JObject.Parse(request.Body)["messages"];
            if (!((string)messages[0]["content"]).Contains("显示标题")) return ordinaryReply(request);
            var requestItems = JObject.Parse((string)messages[1]["content"]);
            Check(requestItems["plugin"] == null && ((JArray)requestItems["items"]).Count == 2,
                "one title-only request carries both plugins without a shared plugin name");
            Check((string)requestItems["items"][0]["description"] == first.RelatedDescription,
                "title-only request contains the short function hint");
            var content = new JObject { ["t0001"] = "实用地图", ["t0002"] = "Other Map" };
            return (200, new JObject { ["choices"] = new JArray(new JObject
                { ["message"] = new JObject { ["content"] = content.ToString() } }) }.ToString());
        };
        var options = new AiExecutionOptions { MaxRequests = 3, RequestDelayMs = 0 };
        var result = await TranslationBatchRunner.RunAsync(plan,
            (target, progress, token) => TranslationClients.TranslateAiAsync(target.Items, provider, server.Url, "model", "",
                "zh-CN", "", 30, 12000, 0, 10, progress, token, options: options),
            (target, translated) => writeLog.Add(target.Guid + ":" + target.Items[0].Id), null, CancellationToken.None,
            translateTitles: (titles, token) => TranslationClients.TranslatePluginTitlesAsync(titles, provider, server.Url, "model", "",
                "zh-CN", "", 30, 12000, 0, 10, token, activity =>
                { if (activity.RequestStarted) observed.AddRange(activity.Items); }, options));
        Check(server.Requests.Count == 3 && result.CompletedPlugins == 1 && result.SavedItems == 3 &&
            result.Failures.Count == 1, "title echo fails independently while both plugins' settings are still saved");
        Check(writeLog[0] == first.PluginGuid + ":plugin-name" && observed.Count == 2 && observed.All(item => item.Id == "plugin-name"),
            "title is persisted first under its own plugin and diagnostics keep original identities");
    }

    private static async Task CheckHttp()
    {
        using (var server = new MockServer())
        {
            var ai = new TranslationProviderDefinition { Name = "test", ApiKeyRequired = false };
            var a = TranslationWorkItem.ForSetting("dedup.plugin", "A", "Category", "General", "key A");
            var b = TranslationWorkItem.ForSetting("dedup.plugin", "B", "Category", "General", "key B");
            var items = new[] { a, b };
            TranslationClients.ClearAiRequestCache();
            var output = await TranslationClients.TranslateAiAsync(items, ai, server.Url, "model", "", "zh", "", 20, 10, null, CancellationToken.None);
            Check(server.Requests.Count == 1, "duplicate semantic text requested once");
            Check(output.Values.Count == 2, "duplicate results expanded to both keys");
            var request = JObject.Parse(server.Requests[0].Body);
            Check(((JArray)request["messages"]).Count == 2, "no conversation history appended");
            var requestItems = (JArray)JObject.Parse((string)request["messages"][1]["content"])["items"];
            Check(requestItems.Count == 1, "one item in batch");
            Check((string)requestItems[0]["kind"] == "Setting", "AI receives the field kind needed for plugin-title instructions");
            await TranslationClients.TranslateAiAsync(items, ai, server.Url, "model", "", "zh", "", 20, 10, null, CancellationToken.None);
            Check(server.Requests.Count == 1, "same session request cache avoids second network request");
            await TranslationClients.TranslateAiAsync(items, ai, server.Url, "model", "", "zh", "different", 20, 10, null, CancellationToken.None);
            Check(server.Requests.Count == 2, "changed prompt creates request");
            var ctx = new SettingSnapshot { StorageKey = "[Audio]Fade", Category = "Audio", DispName = "Fade", Description = "Fade refers to volume." };
            var name = ctx.WorkItem("context.fixture", "DispName", ctx.DispName, "name");
            var desc = ctx.WorkItem("context.fixture", "Description", ctx.Description, "description");
            await TranslationClients.TranslateAiAsync(new[] { name, desc }, ai, server.Url, "model", "", "zh", "", 20, 10, null, CancellationToken.None);
            var promptRows = (JArray)JObject.Parse((string)JObject.Parse(server.Requests.Last().Body)["messages"][1]["content"])["items"];
            Check((string)promptRows[0]["section"] == "Audio" && (string)promptRows[0]["descriptionId"] == (string)promptRows[1]["id"] &&
                promptRows[0]["description"] == null, "same-batch translated description is referenced once");
            await TranslationClients.TranslateAiAsync(new[] { name }, ai, server.Url, "model", "", "zh", "describe only", 20, 10, null, CancellationToken.None);
            promptRows = (JArray)JObject.Parse((string)JObject.Parse(server.Requests.Last().Body)["messages"][1]["content"])["items"];
            Check((string)promptRows[0]["description"] == ctx.Description && promptRows[0]["descriptionId"] == null,
                "name-only requests include the available explanation automatically");
            using (var cancelledCache = new CancellationTokenSource())
            {
                cancelledCache.Cancel();
                await RejectAsync(() => TranslationClients.TranslateAiAsync(items, ai, server.Url, "model", "", "zh", "different", 20, 10, null, cancelledCache.Token), "cancel cached request");
            }
            var successfulReply = server.Reply;
            var partialCalls = 0;
            server.Reply = request =>
            {
                partialCalls++;
                return partialCalls >= 2 && partialCalls <= 4 ? (500, "temporary failure") : successfulReply(request);
            };
            var partialItems = new[] { Item(key: "PartialA"), Item(key: "PartialB") };
            var partial = await TranslationClients.TranslateAiAsync(partialItems, ai, server.Url, "model", "", "zh", "partial", 1, 12000, 2, 10, null, CancellationToken.None);
            Check(partial.Values.Count == 1 && partial.Failures.Count == 1, "temporary failure keeps earlier AI batch result");
            var retried = await TranslationClients.TranslateAiAsync(partialItems, ai, server.Url, "model", "", "zh", "partial", 1, 10, null, CancellationToken.None);
            Check(partialCalls == 5 && retried.Values.Count == 2, "retry resumes from cached successful AI batch");
            TranslationClients.ClearAiRequestCache();
            var malformedCalls = 0;
            server.Reply = request => ++malformedCalls == 2 ? (200, "not-json") : successfulReply(request);
            var malformed = await TranslationClients.TranslateAiAsync(partialItems, ai, server.Url, "model", "", "zh", "malformed", 1, 12000, 2, 10, null, CancellationToken.None);
            Check(malformedCalls == 2 && malformed.Values.Count == 1 && malformed.Failures.Count == 1, "malformed response is never automatically billed again, and earlier results survive");
            var sized = TranslationClients.BatchByInputSize(new[] { Item(new string('a', 600), "LongA"), Item(new string('b', 600), "LongB") }, 20, 1000).ToList();
            Check(sized.Count == 2 && sized.All(batch => batch.Count == 1), "AI batching observes input character budget");
            TranslationClients.ClearAiRequestCache();
            var adaptiveCalls = 0;
            server.Reply = request =>
            {
                adaptiveCalls++;
                var input = (JArray)JObject.Parse((string)JObject.Parse(request.Body)["messages"][1]["content"])["items"];
                return input.Count > 1 ? (413, "payload too large") : successfulReply(request);
            };
            var adaptive = await TranslationClients.TranslateAiAsync(partialItems, ai, server.Url, "model", "", "zh", "adaptive", 20, 12000, 0, 10, null, CancellationToken.None);
            Check(adaptiveCalls == 3 && adaptive.Values.Count == 2, "oversized AI request bisects until accepted");
            TranslationClients.ClearAiRequestCache();
            var beforeIsolation = server.Requests.Count;
            server.Reply = request =>
            {
                var input = (JArray)JObject.Parse((string)JObject.Parse(request.Body)["messages"][1]["content"])["items"];
                var translated = new JObject();
                foreach (var row in input)
                {
                    var text = (string)row["text"];
                    translated[(string)row["id"]] = text.Contains("F1") ? "按 F2：50%" : "普通译文";
                }
                return (200, new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = translated.ToString(Formatting.None) } }) }.ToString(Formatting.None));
            };
            var isolatedItems = new[] { Item("Press F1 at 50%", "Protected"), Item("Ordinary text", "Ordinary") };
            var isolated = await TranslationClients.TranslateAiAsync(isolatedItems, ai, server.Url, "model", "", "zh", "isolation", 20, 10, null, CancellationToken.None);
            Check(isolated.Values.Count == 1 && isolated.Failures.Count == 1 && isolated.Failures.ContainsKey(isolatedItems[0].Id),
                "one malformed AI value is isolated while valid values survive");
            Check(server.Requests.Count - beforeIsolation == 1, "one malformed value never resends the valid items or multiplies token charges");
            TranslationClients.ClearAiRequestCache();
            var structuralCalls = 0;
            server.Reply = request =>
            {
                structuralCalls++;
                if (structuralCalls == 2 || structuralCalls == 3)
                {
                    var content = structuralCalls == 2 ? "{}" : "{\"t0001\":123}";
                    return (200, new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = content } }) }.ToString(Formatting.None));
                }
                return successfulReply(request);
            };
            var structuralItems = Enumerable.Range(0, 4).Select(i => Item("Source " + new string('a', i + 1), "Structural" + i)).ToArray();
            var structural = await TranslationClients.TranslateAiAsync(structuralItems, ai, server.Url, "model", "", "zh", "structural", 1, 12000, 0, 10, null, CancellationToken.None);
            Check(structuralCalls == 4 && structural.Values.Count == 2 && structural.Failures.Count == 2,
                "missing IDs and non-string values are isolated; earlier and later good batches survive");
            TranslationClients.ClearAiRequestCache();
            var authCalls = 0;
            server.Reply = _ => { authCalls++; return (401, "credential-secret-in-error-body"); };
            Exception authError = null;
            try { await TranslationClients.TranslateAiAsync(items, ai, server.Url, "model", "", "zh", "auth", 20, 12000, 2, 10, null, CancellationToken.None); }
            catch (Exception ex) { authError = ex; }
            Check(authError != null && !authError.Message.Contains("credential-secret") && authCalls == 1,
                "HTTP auth error body stays hidden and is not retried per item");
            server.Reply = _ => (200, "{\"data\":{\"translations\":[{\"translatedText\":\"译文\"}]}}");
            var google = TranslationProviderCatalog.GetMachineProvider("Google Translate");
            var item = Item();
            var result = await TranslationClients.TranslateMachineAsync(new[] { item }, google, server.Url, "fake-key", "", "", "en", "zh-CN", 20, 10, null, CancellationToken.None);
            Check(result.Values[item.Id] == "译文", "Google response parsed");
            Check(server.Requests.Last().Headers.IndexOf("X-goog-api-key: fake-key", StringComparison.OrdinalIgnoreCase) >= 0, "Google key in header");
            Check(JObject.Parse(server.Requests.Last().Body)["format"].ToString() == "text", "Google requests plain text");
            server.Reply = _ => (200, "{\"trans_result\":[{\"dst\":\"译文\"}]}");
            await TranslationClients.TranslateMachineAsync(new[] { item }, TranslationProviderCatalog.GetMachineProvider("百度翻译"), server.Url,
                "fake-secret", "app-id", "", "en", "zh", 20, 10, null, CancellationToken.None);
            Check(server.Requests.Last().Body.Contains("appid=app-id") && !server.Requests.Last().Body.Contains("fake-secret"), "Baidu sends signature not secret");
            server.Reply = _ => (200, "{\"errorCode\":\"0\",\"translation\":[\"译文\"]}");
            await TranslationClients.TranslateMachineAsync(new[] { item }, TranslationProviderCatalog.GetMachineProvider("有道翻译"), server.Url,
                "fake-secret", "app-id", "", "en", "zh-CHS", 20, 10, null, CancellationToken.None);
            Check(server.Requests.Last().Body.Contains("signType=v3") && !server.Requests.Last().Body.Contains("fake-secret"), "Youdao v3 signed form");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var before = server.Requests.Count;
                await RejectAsync(() => TranslationClients.TranslateMachineAsync(new[] { item }, google, server.Url,
                    "key", "", "", "en", "zh-CN", 20, 10, null, cancelled.Token), "cancel before sending");
                Check(server.Requests.Count == before, "cancelled call has no network request");
            }
        }
    }

    private sealed class Request
    {
        internal string Headers, Body;
    }
    private sealed class MockServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Task _loop;
        internal readonly List<Request> Requests = new List<Request>();
        internal Func<Request, (int Code, string Body)> Reply;
        internal string Url { get; }
        internal MockServer()
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/translate";
            Reply = request =>
            {
                var payload = JObject.Parse(request.Body);
                var input = JObject.Parse((string)payload["messages"][1]["content"])["items"];
                var translated = new JObject();
                foreach (var row in input) translated[(string)row["id"]] = "译文";
                return (200, new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = translated.ToString(Formatting.None) } }) }.ToString(Formatting.None));
            };
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using (var client = await _listener.AcceptTcpClientAsync())
                        using (var stream = client.GetStream())
                        {
                            var header = new List<byte>(); var buffer = new byte[1];
                            while (await stream.ReadAsync(buffer, 0, 1) == 1)
                            {
                                header.Add(buffer[0]);
                                if (header.Count >= 4 && Encoding.ASCII.GetString(header.Skip(header.Count - 4).ToArray()) == "\r\n\r\n") break;
                            }
                            var headers = Encoding.ASCII.GetString(header.ToArray());
                            var lengthHeader = headers.Split('\n').FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                            var length = lengthHeader == null ? 0 : int.Parse(lengthHeader.Split(':')[1].Trim());
                            var body = new byte[length]; var offset = 0;
                            while (offset < length) { var read = await stream.ReadAsync(body, offset, length - offset); if (read == 0) throw new IOException("Incomplete body"); offset += read; }
                            var request = new Request { Headers = headers, Body = Encoding.UTF8.GetString(body) };
                            Requests.Add(request);
                            var response = Reply(request);
                            var bytes = Encoding.UTF8.GetBytes(response.Body);
                            var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.Code + " Test\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                            await stream.WriteAsync(prefix, 0, prefix.Length);
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
        }
        public void Dispose() { _listener.Stop(); _loop.GetAwaiter().GetResult(); }
    }
}
