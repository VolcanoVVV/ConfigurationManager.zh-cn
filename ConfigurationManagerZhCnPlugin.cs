using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using ConfigurationManager;
using HarmonyLib;
using UnityEngine;

#if IL2CPP
using BepInEx.Unity.IL2CPP;
using PluginBase = BepInEx.Unity.IL2CPP.BasePlugin;
using RuntimePlugin = BepInEx.PluginInfo;
#else
using PluginBase = BepInEx.BaseUnityPlugin;
using RuntimePlugin = BepInEx.BaseUnityPlugin;
#endif

namespace ConfigurationManagerZhCn
{
    [BepInPlugin(PluginGuid, "Volcano ConfigurationManager 汉化扩展", PluginVersion)]
    [BepInDependency("com.bepis.bepinex.configurationmanager", BepInDependency.DependencyFlags.HardDependency)]
#if !IL2CPP
    [BepInDependency(BetterBepInExCompatibility.Guid, BepInDependency.DependencyFlags.SoftDependency)]
#endif
    [Browsable(false)]
    public sealed class ConfigurationManagerZhCnPlugin : PluginBase
    {
        internal const string PluginGuid = "ConfigurationManager.zh-cn";
        // Release numbers are owner-selected; see docs/VERSION_POLICY.zh-CN.md.
        internal const string PluginVersion = "2.14.2";
        internal static ConfigEntry<bool> EnableServerLiveSync;
        internal static ConfigEntry<bool> QuietServerLiveSyncLogs;

        internal static ConfigurationManagerZhCnPlugin Instance { get; private set; }

        internal ConfigEntry<LocalizationDisplayMode> DisplayMode { get; private set; }
        internal ConfigEntry<bool> EnableManualTranslation { get; private set; }
        internal ConfigEntry<bool> EnableAiTranslation { get; private set; }
        internal ConfigEntry<bool> EnableMachineTranslation { get; private set; }
        internal ConfigEntry<bool> ManualAutoAdvance { get; private set; }
        internal WorkbenchAppearance Appearance { get; private set; }
        internal ConfigEntry<string> MachineProvider { get; private set; }
        internal ConfigEntry<string> MachineEndpoint { get; private set; }
        internal ConfigEntry<string> MachineRegion { get; private set; }
        internal ConfigEntry<string> MachineSourceLanguage { get; private set; }
        internal ConfigEntry<string> MachineTargetLanguage { get; private set; }
        internal ConfigEntry<int> MachineBatchSize { get; private set; }
        internal ConfigEntry<string> AiProvider { get; private set; }
        internal ConfigEntry<string> AiEndpoint { get; private set; }
        internal ConfigEntry<string> AiModel { get; private set; }
        internal ConfigEntry<bool> AutoFetchAiModels { get; private set; }
        internal ConfigEntry<string> TargetLanguage { get; private set; }
        internal ConfigEntry<string> AiExtraPrompt { get; private set; }
        internal ConfigEntry<int> AiBatchSize { get; private set; }
        internal ConfigEntry<int> AiMaxInputCharacters { get; private set; }
        internal ConfigEntry<int> AiRetryCount { get; private set; }
        internal ConfigEntry<string> AiAdapter { get; private set; }
        internal ConfigEntry<string> AiReasoning { get; private set; }
        internal ConfigEntry<int> AiMaxOutputTokens { get; private set; }
        internal ConfigEntry<bool> AiJsonOutput { get; private set; }
        internal ConfigEntry<int> AiMaxReportedTokens { get; private set; }
        internal ConfigEntry<bool> AiStopped { get; private set; }
        internal ConfigEntry<string> PendingServerJob { get; private set; }
        internal ConfigEntry<int> AiMaxRequests { get; private set; }
        internal ConfigEntry<int> AiRequestDelayMs { get; private set; }
        internal ConfigEntry<int> RequestTimeoutSeconds { get; private set; }
        internal ConfigEntry<bool> RememberApiCredentials { get; private set; }
        internal ConfigEntry<bool> TranslatePluginNames { get; private set; }
        internal ConfigEntry<bool> TranslateCategories { get; private set; }
        internal ConfigEntry<bool> TranslateSettingNames { get; private set; }
        internal ConfigEntry<bool> TranslateDescriptions { get; private set; }
        internal ConfigEntry<bool> TranslateEnums { get; private set; }
        internal ConfigEntry<bool> TranslateCustomControls { get; private set; }
        internal ConfigEntry<string> ServerAdminKey { get; private set; }

#if IL2CPP
        public override void Load()
#else
        private void Awake()
#endif
        {
            InitializePlugin();
#if IL2CPP
            ConfigurationManagerZhCnBehaviour.Plugin = this;
            AddComponent<ConfigurationManagerZhCnBehaviour>();
#endif
        }

        private void InitializePlugin()
        {
            Instance = this;
            MainThreadDispatcher.Initialize();
            BindSettings();
            JsoncLocalization.Initialize(PluginLogger, DisplayMode.Value,
                EnableManualTranslation.Value, EnableAiTranslation.Value, EnableMachineTranslation.Value);
            UiLocalization.Initialize(PluginLogger);
            ServerTranslationFeature.Initialize(PluginLogger);
            TranslationPanel.Initialize(this, PluginLogger);
            // Migrate a legacy plaintext key into this process only. Never write new administrator credentials to cfg.
            ServerAdminKey.Value = string.Empty;

            DisplayMode.SettingChanged += delegate
            {
                JsoncLocalization.SetDisplayMode(DisplayMode.Value);
                TranslationPanel.RequestManagerRefresh();
            };
            EnableManualTranslation.SettingChanged += TranslationLayerEnabledChanged;
            EnableAiTranslation.SettingChanged += TranslationLayerEnabledChanged;
            EnableMachineTranslation.SettingChanged += TranslationLayerEnabledChanged;
            RememberApiCredentials.SettingChanged += delegate
            {
                TranslationPanel.SetRememberApiCredentials(RememberApiCredentials.Value);
            };

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(ConfigurationManagerZhCnPlugin).Assembly);
#if !IL2CPP
                BetterBepInExCompatibility.Initialize(PluginLogger);
#endif
                PluginLogger.LogInfo("ConfigurationManager.zh-cn " + PluginVersion + " ready.");
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed to apply Harmony patches: " + ex);
            }
        }

        private ManualLogSource PluginLogger
        {
            get
            {
#if IL2CPP
                return Log;
#else
                return Logger;
#endif
            }
        }

        private void BindSettings()
        {
            Appearance = new WorkbenchAppearance(Config, WorkbenchAppearance.DefaultScale(Screen.width, Screen.height));
            DisplayMode = Config.Bind("显示", "显示模式", LocalizationDisplayMode.Chinese,
                "中文按人工精翻、AI 精翻、机翻的顺序取值；也可显示英文或中英双语。");
            EnableManualTranslation = Config.Bind("翻译来源", "启用人工精翻", true, "启用人工译文层；始终拥有最高优先级。");
            EnableAiTranslation = Config.Bind("翻译来源", "启用AI精翻", true, "启用 AI 精翻层；人工译文不存在时使用。");
            EnableMachineTranslation = Config.Bind("翻译来源", "启用机翻", true, "启用机器翻译层；仅作为最后的中文回退。");
            ManualAutoAdvance = Config.Bind("人工精翻", "保存后自动下一项", true, "保存人工译文后自动移动到下一条文本。");

            MachineProvider = Config.Bind("机翻接口", "服务商", "Microsoft Translator", "百度、有道、Google、Microsoft、DeepL 或 LibreTranslate；接口机翻结果保存到 zh-cn/MT。");
            MachineEndpoint = Config.Bind("机翻接口", "接口地址", TranslationProviderCatalog.MachineProviders[0].Endpoint,
                "机器翻译 HTTP 接口地址。API 密钥在工作台输入；只有主动开启“记住 API 凭据”时才会加密保存。");
            if (string.Equals(MachineEndpoint.Value,
                "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=en&to=zh-Hans",
                StringComparison.OrdinalIgnoreCase))
                MachineEndpoint.Value = TranslationProviderCatalog.MachineProviders[0].Endpoint;
            MachineRegion = Config.Bind("机翻接口", "区域", string.Empty, "Microsoft Translator 的资源区域；全局资源可留空。");
            MachineSourceLanguage = Config.Bind("机翻接口", "源语言", "en", "源语言代码；LibreTranslate 可填写 auto。");
            MachineTargetLanguage = Config.Bind("机翻接口", "目标语言", "zh-Hans", "机器翻译目标语言代码。");
            MachineBatchSize = Config.Bind("机翻接口", "批量大小", 20,
                new ConfigDescription("每次机翻请求的文本数量。", new AcceptableValueRange<int>(1, 50)));

            var defaultAi = TranslationProviderCatalog.GetAiProvider("DeepSeek");
            AiProvider = Config.Bind("AI 精翻接口", "服务商", defaultAi.Name, "内置服务商或自定义 OpenAI Chat Completions 兼容接口。");
            AiEndpoint = Config.Bind("AI 精翻接口", "接口地址", defaultAi.Endpoint, "Chat Completions 完整地址。");
            AiModel = Config.Bind("AI 精翻接口", "模型", defaultAi.DefaultModel, "服务商模型 ID，可手动输入。");
            AutoFetchAiModels = Config.Bind("AI 精翻接口", "自动获取模型", true, "打开 AI 设置后按接口和当前密钥获取模型元数据，缓存 10 分钟；不会自动替换已选模型。可关闭并手动输入。");
            TargetLanguage = Config.Bind("AI 精翻接口", "目标语言", "简体中文", "翻译目标语言。");
            AiExtraPrompt = Config.Bind("AI 精翻接口", "附加提示词", string.Empty, "追加到内置结构保护提示词后的术语或风格要求。");
            AiBatchSize = Config.Bind("AI 精翻接口", "批量大小", 40,
                new ConfigDescription("每次 AI 请求最多包含的文本条目数；实际还会按字符预算自动拆小。", new AcceptableValueRange<int>(1, 60)));
            AiMaxInputCharacters = Config.Bind("AI 精翻接口", "单批字符预算", 12000,
                new ConfigDescription("每次 AI 请求的原文与上下文字符预算。大文件会按此值自动拆批；接口仍提示过大时可调低。", new AcceptableValueRange<int>(1000, 60000)));
            AiRetryCount = Config.Bind("AI 精翻接口", "临时失败重试次数", 1,
                new ConfigDescription("仅 HTTP 429/5xx 自动重试；超时、断网和无效结果不自动重发，避免重复消费。已完成检查点立即写盘。", new AcceptableValueRange<int>(0, 3)));
            AiAdapter = Config.Bind("AI 精翻接口", "参数适配器", "auto", "auto、generic、openai、deepseek、qwen、gemini。代理地址可手动指定适配器；generic 不发送供应商思考参数。");
            AiReasoning = Config.Bind("AI 精翻接口", "思考强度", "off", "default、off、low、medium、high。Gemini 3 的 off 映射到 low；未知接口使用供应商默认。");
            AiMaxOutputTokens = Config.Bind("AI 精翻接口", "输出token上限", 8192, new ConfigDescription("包含模型推理的输出预算；过小会截断完整 JSON。", new AcceptableValueRange<int>(256, 32768)));
            AiJsonOutput = Config.Bind("AI 精翻接口", "结构化JSON输出", true, "发送 response_format=json_object。不支持此参数的兼容服务可关闭。");
            AiMaxReportedTokens = Config.Bind("AI 精翻接口", "任务已报告token预算", TranslationFoundation.PaidApiBudget.DefaultTokens, new ConfigDescription("发送前预留 token。供应商缺报时保留预留值。0 使用默认额度。", new AcceptableValueRange<int>(0, 10000000)));
            AiMaxRequests = Config.Bind("AI 精翻接口", "任务请求上限", TranslationFoundation.PaidApiBudget.DefaultRequests, new ConfigDescription("含重试，0 使用默认上限。", new AcceptableValueRange<int>(0, 10000)));
            AiStopped = Config.Bind("API 使用确认", "暂停发送API翻译请求", false);
            AiRequestDelayMs = Config.Bind("AI 精翻接口", "请求间隔毫秒", 250, new ConfigDescription("降低持续请求的限流概率。", new AcceptableValueRange<int>(0, 10000)));
            RequestTimeoutSeconds = Config.Bind("翻译", "请求超时秒数", 120, new ConfigDescription("机翻和 AI 请求的超时时间。", new AcceptableValueRange<int>(10, 600)));
            RememberApiCredentials = Config.Bind("翻译", "记住 API 凭据", false,
                "开启后使用当前 Windows 用户的 DPAPI 加密保存 AI/机翻密钥和 App ID，重启后自动恢复。同一 Windows 账户下的其他进程仍可能解密；共享或不可信电脑请保持关闭。关闭会删除已保存文件，但不清空本次游戏内输入。");

            TranslatePluginNames = Config.Bind("翻译范围", "插件名称", true, "批量翻译插件名称。");
            TranslateCategories = Config.Bind("翻译范围", "分类", true, "批量翻译配置分类。");
            TranslateSettingNames = Config.Bind("翻译范围", "键值名称", true, "批量翻译配置项显示名称。");
            TranslateDescriptions = Config.Bind("翻译范围", "说明", true, "批量翻译配置项说明。");
            TranslateEnums = Config.Bind("翻译范围", "枚举值", true, "批量翻译枚举显示值。");
            TranslateCustomControls = Config.Bind("翻译范围", "自定义控件", true, "批量翻译已观测到的自定义按钮、开关、纯文本标签，以及标准非枚举下拉候选项。");
            EnableServerLiveSync = Config.Bind("服务端汉化", "自动同步正式译文", true, "连接服务端发布通知，收到正式译文更新后才拉取。旧服务端可手动拉取。");
            QuietServerLiveSyncLogs = Config.Bind("服务端汉化", "隐藏自动同步成功请求日志", true,
                "只隐藏 RequestHandler 对 /workbench/live 的请求开始和成功 Info 日志；失败及其他请求仍保留。关闭可恢复详细调试日志。");
            PendingServerJob = Config.Bind("服务端汉化", "上次服务端任务", string.Empty,
                "连接身份摘要和任务 ID，用于在重新连接后只查询旧任务。");
            ServerAdminKey = Config.Bind("服务端汉化", "Fika 管理员密钥", string.Empty,
                "管理员推送、文件开关、更新基准和重新加载所需。密钥在服务端 SimpleTranslatorCS/workbench-admin.jsonc 中；普通玩家留空，只能按冷却时间手动拉取已生效译文。");
        }

        private void TranslationLayerEnabledChanged(object sender, EventArgs args)
        {
            JsoncLocalization.SetEnabledLayers(EnableManualTranslation.Value,
                EnableAiTranslation.Value, EnableMachineTranslation.Value);
            TranslationPanel.RequestManagerRefresh();
        }

        private void Update()
        {
            MainThreadDispatcher.Pump();
            ServerTranslationFeature.CheckConnection();
            ServerLocaleSynchronizer.Tick();
            WebWorkbenchBridge.UpdateServerConnection(PluginLogger);
        }

#if IL2CPP
        private sealed class ConfigurationManagerZhCnBehaviour : MonoBehaviour
        {
            internal static ConfigurationManagerZhCnPlugin Plugin;

            private void Update()
            {
                if (Plugin != null)
                    Plugin.Update();
            }
        }
#endif

#if IL2CPP
        public override bool Unload()
#else
        private void OnDestroy()
#endif
        {
#if !IL2CPP
            BetterBepInExCompatibility.Shutdown();
#endif
            MainThreadDispatcher.Shutdown();
            TranslationPanel.Shutdown();
            ServerLocaleSynchronizer.StopListening();
            ServerTranslationFeature.Dispose();
            WebWorkbenchBridge.UpdateServerConnection(PluginLogger, force: true);
            TranslationPanelTheme.Dispose();
#if IL2CPP
            ConfigurationManagerZhCnBehaviour.Plugin = null;
            return true;
#endif
        }
    }

    internal static class ConfigurationManagerMethods
    {
        // Resolve inside the known dependency assembly. AccessTools.Method(string)
        // scans every loaded IL2CPP interop assembly and logs unrelated type-load errors.
        private static readonly Assembly ManagerAssembly = typeof(ConfigurationManager.ConfigurationManager).Assembly;

        internal static MethodBase Get(string typeName, string methodName)
        {
            var type = ManagerAssembly.GetType("ConfigurationManager." + typeName, false);
            return type == null ? null : AccessTools.Method(type, methodName);
        }
    }

    [HarmonyPatch]
    internal static class SettingSearcherPatches
    {
        private static readonly Dictionary<string, string> LastFailures = new Dictionary<string, string>(StringComparer.Ordinal);

        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("SettingSearcher", "CollectSettings");
        }

        private static void Prefix()
        {
            try { JsoncLocalization.BeginCollection(); }
            catch (Exception ex) { ReportFailure("<collection>", ex); }
        }

        private static void Postfix(ref IEnumerable<SettingEntryBase> results)
        {
            if (results == null)
                return;

            try
            {
                var list = results as IList<SettingEntryBase> ?? results.ToList();
                // Publish the collected list before optional localization work.
                results = list;
                LocalizeSettings(list, PluginFinder.FindPlugins());
            }
            catch (Exception ex) { ReportFailure("<collection>", ex); }
        }

        internal static void LocalizeSettings(IEnumerable<SettingEntryBase> list, IEnumerable<RuntimePlugin> plugins)
        {
            try
            {
                var pluginMap = plugins
                    .Where(p => p != null && PluginRuntime.GetMetadata(p) != null && !string.IsNullOrEmpty(PluginRuntime.GetMetadata(p).GUID))
                    .GroupBy(p => PluginRuntime.GetMetadata(p).GUID)
                    .ToDictionary(g => g.Key, g => g.First());
                foreach (var group in list.Where(s => s != null).GroupBy(s => s.PluginInfo != null ? s.PluginInfo.GUID : null))
                {
                    if (string.IsNullOrEmpty(group.Key)) continue;
                    var entries = group.Distinct(SettingReferenceComparer.Instance).ToList();
                    LocalizePlugin(group.Key, entries, originals =>
                    {
                        RuntimePlugin plugin;
                        if (pluginMap.TryGetValue(group.Key, out plugin))
                            JsoncLocalization.RegisterAndApply(plugin, entries);
                        for (var index = 0; index < entries.Count; index++)
                        {
                            var source = originals[index];
                            UiLocalization.ApplySetting(group.Key, entries[index], source[0], source[1], source[2]);
                        }
                    });
                }
            }
            catch (Exception ex) { ReportFailure("<collection>", ex); }
        }

        internal static void LocalizePlugin(string guid, IList<SettingEntryBase> entries, Action<string[][]> localize)
        {
            string[][] originals = null;
            Dictionary<BepInPlugin, string> names = null;
            try
            {
                originals = entries.Select(s => new[] { s.Category, s.DispName, s.Description }).ToArray();
                names = entries.Where(s => s.PluginInfo != null).Select(s => s.PluginInfo).Distinct()
                    .ToDictionary(info => info, info => info.Name);
                localize(originals);
            }
            catch (Exception ex)
            {
                JsoncLocalization.DiscardPluginCollection(guid);
                if (originals != null)
                    for (var index = 0; index < entries.Count; index++)
                        foreach (var property in new[] { "Category", "DispName", "Description" }.Select((name, field) => new { name, field }))
                            try
                            {
                                typeof(SettingEntryBase).GetProperty(property.name).SetValue(entries[index], originals[index][property.field], null);
                            }
                            catch (Exception restoreError) { ReportFailure(guid + "/restore", restoreError); }
                if (names != null)
                    foreach (var name in names) JsoncLocalization.TrySetPluginMetadataName(name.Key, name.Value);
                ReportFailure(guid, ex);
            }
        }

        private static void ReportFailure(string guid, Exception error)
        {
            var signature = error.GetType().FullName + ": " + error.Message;
            lock (LastFailures)
            {
                string previous;
                if (LastFailures.TryGetValue(guid, out previous) && previous == signature) return;
                LastFailures[guid] = signature;
            }
            JsoncLocalization.LogError("Localization failed for plugin " + guid +
                "; collected settings retained, localization reverted where possible. " + error);
        }
    }

    internal sealed class SettingReferenceComparer : IEqualityComparer<SettingEntryBase>
    {
        internal static readonly SettingReferenceComparer Instance = new SettingReferenceComparer();
        public bool Equals(SettingEntryBase left, SettingEntryBase right) { return ReferenceEquals(left, right); }
        public int GetHashCode(SettingEntryBase value) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value); }
    }

    [HarmonyPatch]
    internal static class ConfigurationManagerSidePanelPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("ConfigurationManager", "OnGUI");
        }

        private static void Prefix(object __instance, ref ConfigurationManagerScale.State __state)
        {
#if !IL2CPP
            if (BetterBepInExCompatibility.ReplacementMenuActive) return;
#endif
            ConfigurationManagerScale.Begin(__instance, ref __state);
            TranslationPanel.DrawSidePanel(__instance);
        }

        private static void Finalizer(ConfigurationManagerScale.State __state) { ConfigurationManagerScale.End(__state); }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        { return ConfigurationManagerScale.RewriteCoordinates(instructions); }
    }

    [HarmonyPatch]
    internal static class ConfigurationManagerWindowPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("ConfigurationManager", "SettingsWindow");
        }

        private static void Prefix(object __instance, ref ConfigurationManagerScale.DropdownFrame __state)
        {
            ConfigurationManagerScale.BeginDropdownWindow(__instance, ref __state);
            TranslationPanel.DrawEmbedded(__instance);
            TranslationPanel.BeginManagerContent();
        }

        private static void Finalizer(ConfigurationManagerScale.DropdownFrame __state)
        {
            ConfigurationManagerScale.EndDropdownWindow(__state);
            TranslationPanel.EndManagerContent();
        }
    }

    [HarmonyPatch]
    internal static class SettingFieldDrawerObjectToGuiContentPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("SettingFieldDrawer", "ObjectToGuiContent");
        }

        private static bool Prefix(object x, ref GUIContent __result)
        {
            var enumValue = x as Enum;
            if (enumValue == null)
            {
                var localizedChoice = TranslationPanel.LocalizeSettingChoice(x);
                if (string.IsNullOrEmpty(localizedChoice) || string.Equals(localizedChoice, x != null ? x.ToString() : null, StringComparison.Ordinal))
                    return true;
                __result = new GUIContent(localizedChoice);
                return false;
            }

            var localized = JsoncLocalization.GetEnumLocalizedDescription(enumValue.GetType(), enumValue.ToString());
            if (string.IsNullOrEmpty(localized))
                return true;

            __result = new GUIContent(localized);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SettingFieldDrawerFlagsPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawFlagsField");
        }

        private static bool Prefix(SettingEntryBase setting, System.Collections.IList enumValues, int maxWidth)
        {
            if (setting == null || enumValues == null)
                return true;

            try
            {
                var currentValue = Convert.ToInt64(setting.Get());
                var values = enumValues.Cast<Enum>()
                    .Select(x => new
                    {
                        name = JsoncLocalization.GetEnumLocalizedDescription(setting.SettingType, x.ToString()) ?? x.ToString(),
                        val = Convert.ToInt64(x)
                    })
                    .ToArray();

                GUILayout.BeginVertical(GUILayout.MaxWidth(maxWidth));
                for (var index = 0; index < values.Length;)
                {
                    GUILayout.BeginHorizontal();
                    var currentWidth = 0;
                    var itemsInRow = 0;
                    for (; index < values.Length; index++)
                    {
                        var value = values[index];
                        if (value.val == 0)
                            continue;

                        var textDimension = (int)GUI.skin.toggle.CalcSize(new GUIContent(value.name)).x;
                        currentWidth += textDimension;
                        if (currentWidth > maxWidth && itemsInRow > 0)
                            break;

                        GUI.changed = false;
                        var newVal = GUILayout.Toggle((currentValue & value.val) == value.val, value.name, GUILayout.ExpandWidth(false));
                        if (GUI.changed)
                        {
                            var newValue = newVal ? currentValue | value.val : currentValue & ~value.val;
                            setting.Set(Enum.ToObject(setting.SettingType, newValue));
                        }
                        itemsInRow++;
                    }
                    GUILayout.EndHorizontal();
                }
                GUI.changed = false;
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                return false;
            }
            catch (Exception ex)
            {
                JsoncLocalization.LogError("DrawFlagsField failed: " + ex.Message);
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class UiTextPatches
    {
        private static readonly HashSet<string> UiExclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "searchBox",
            "searchBox "
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return new[]
            {
                ConfigurationManagerMethods.Get("ConfigurationManager", "OnGUI"),
                ConfigurationManagerMethods.Get("ConfigurationManager", "SettingsWindow"),
                ConfigurationManagerMethods.Get("ConfigurationManager", "DrawTips"),
                ConfigurationManagerMethods.Get("ConfigurationManager", "DrawWindowHeader"),
                ConfigurationManagerMethods.Get("ConfigurationManager", "DrawSingleSetting"),
                ConfigurationManagerMethods.Get("ConfigurationManager", "DrawSinglePlugin"),
                ConfigurationManagerMethods.Get("ConfigurationManager", "DrawDefaultButton"),
                ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawBoolField"),
                ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawKeyCode"),
                ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawKeyboardShortcut")
            }.Where(m => m != null);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getMethod = AccessTools.Method(typeof(UiLocalization), "Get");
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr)
                {
                    yield return instruction;
                    if (!UiExclude.Contains(instruction.operand as string))
                        yield return new CodeInstruction(OpCodes.Call, getMethod);
                    continue;
                }

                yield return instruction;
            }
        }
    }

    internal static class PluginFinder
    {
        internal static RuntimePlugin[] FindPlugins()
        {
            try
            {
#if IL2CPP
                return IL2CPPChainloader.Instance.Plugins.Values
                    .Where(x => x != null && x.Instance is BasePlugin)
                    .ToArray();
#else
                var chain = Chainloader.PluginInfos.Values.Select(x => x.Instance).Where(x => x != null);
                var scene = UnityEngine.Object.FindObjectsOfType(typeof(BaseUnityPlugin)).Cast<BaseUnityPlugin>();
                return chain.Union(scene).ToArray();
#endif
            }
            catch
            {
                return new RuntimePlugin[0];
            }
        }
    }

    internal static class PluginRuntime
    {
        internal static BepInPlugin GetMetadata(RuntimePlugin plugin)
        {
#if IL2CPP
            return plugin != null ? plugin.Metadata : null;
#else
            return plugin != null && plugin.Info != null ? plugin.Info.Metadata : null;
#endif
        }
    }
}
