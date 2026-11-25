using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using ConfigurationManager;
using HarmonyLib;
using UnityEngine;
using StockManager = ConfigurationManager.ConfigurationManager;

namespace ConfigurationManagerZhCn
{
    internal static class BetterBepInExCompatibility
    {
        internal const string Guid = "com.mybutthasarash.betterbepinex";
        private static readonly string[] TextMethods =
        {
            "BuildSidebar", "DrawStatusBar", "SearchSummary",
            "Undo", "Redo", "ShortcutText", "KeyName", "AdvancedChipLabel", "DrawPage",
            "DrawPageList", "DrawSearchResults", "DrawChanged", "DrawKeybinds", "ResetRows",
            "DrawTags", "DefaultText", "CommitEdit", "Update", "TakePendingSnapshot", "TakeSnapshot",
            "SaveSnapshotPart", "LayoutSnapshot", "AddSnapRow", "SnapshotValueText"
        };

        private static Harmony _harmony;
        private static StockManager _stock;
        private static Func<bool> _isActive;
        private static Action _rebuild;
        private static FieldInfo _rect, _rowSetting, _pageGuid;
        private static FieldInfo _pageDisplayName, _pageTitle, _pageSidebarLabel;
        private static FieldInfo _rowHaystack, _rowOptions, _rowOptionLabels;
        private static FieldInfo _rowResetLabel;
        private static FieldInfo _blocks, _blockRow, _blockKey, _sectionRows, _sectionIsHiddenGroup, _valueWidth;
        private static Type _sectionType;
        private static FieldInfo _countLabels, _cachedLabels;
        private static MethodInfo _invalidateLists;
        private static Font _chineseFont;
        private static bool _triedChineseFont;
        private static ManualLogSource _log;
        private static bool _loggedRefreshError;
        private static bool _pendingModelBuild;
        private static bool _lastActive;
        private static LocalizationDisplayMode? _lastDisplayMode;

        private static readonly List<KeyValuePair<MethodBase, Patches>> StockWindowPatches =
            new List<KeyValuePair<MethodBase, Patches>>();
        private static readonly List<KeyValuePair<GUIContent, GUIContent>> MenuLabels =
            new List<KeyValuePair<GUIContent, GUIContent>>();
        private static readonly HashSet<object> DecoratedPages = new HashSet<object>();

        internal static bool ReplacementMenuActive => _isActive != null && _isActive();
        private static bool Active => _rebuild != null && ReplacementMenuActive;

        internal static void Initialize(ManualLogSource log)
        {
            PluginInfo pluginInfo;
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out pluginInfo) || pluginInfo.Instance == null)
                return;
            var version = pluginInfo.Metadata.Version;
            var plugin = pluginInfo.Instance;
            var pluginType = plugin.GetType();
            _log = log;
            Action<string> warn = message => log.LogWarning("BetterBepInEx " + version + ": " + message);
            Func<bool> isActive = null;
            try
            {
                isActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), ResolveActiveGetter(pluginType));
                _stock = (StockManager)Chainloader.PluginInfos[StockManager.GUID].Instance;
                var patches = ResolveHooks(pluginType, warn, out var rebuildMethod);
                var rebuild = (Action)Delegate.CreateDelegate(typeof(Action), plugin, rebuildMethod);
                _harmony = NewAdapterHarmony();
                foreach (var patch in patches) patch(_harmony);
                _rebuild = rebuild;
                log.LogInfo("BetterBepInEx " + version + " translation and workbench hooks ready (matched menu interfaces).");
            }
            catch (Exception ex)
            {
                Shutdown();
                warn("Could not attach translation and workbench hooks. " + ex);
            }
            _isActive = isActive;

            // Preserve the stock menu when BetterBepInEx removes its patches during Start,
            // including when the replacement menu could not be adapted.
            try
            {
                var start = Require(FindMethod(pluginType, "Start", typeof(void)), "BetterBepInExPlugin.Start()");
                foreach (var methodName in new[] { "OnGUI", "SettingsWindow" })
                {
                    var target = ConfigurationManagerMethods.Get("ConfigurationManager", methodName);
                    var patches = target == null ? null : Harmony.GetPatchInfo(target);
                    if (patches != null) StockWindowPatches.Add(new KeyValuePair<MethodBase, Patches>(target, patches));
                }
                if (StockWindowPatches.Count > 0)
                {
                    if (_harmony == null) _harmony = NewAdapterHarmony();
                    _harmony.Patch(start, postfix: Hook(nameof(RestoreStockPatches)));
                }
            }
            catch (Exception ex)
            {
                StockWindowPatches.Clear();
                warn("Could not preserve stock window hooks. " + ex.Message);
            }
        }

        private static Harmony NewAdapterHarmony() => new Harmony(ConfigurationManagerZhCnPlugin.PluginGuid + ".BetterBepInEx");

        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static MethodInfo ResolveActiveGetter(Type pluginType)
        {
            var active = AccessTools.PropertyGetter(pluginType, "Active");
            return Require(active?.IsStatic == true && active.ReturnType == typeof(bool) && active.GetParameters().Length == 0
                ? active : null, "BetterBepInExPlugin.Active (static bool)");
        }

        internal static List<Action<Harmony>> ResolveHooks(Type pluginType, Action<string> warn, out MethodInfo rebuildMethod)
        {
            var assembly = pluginType.Assembly;
            var menu = assembly.GetType("BetterBepInEx.MenuWindow", true);
            var model = assembly.GetType("BetterBepInEx.SettingsModel", true);
            var row = assembly.GetType("BetterBepInEx.Row");
            var page = assembly.GetType("BetterBepInEx.Page", true);
            var stock = assembly.GetType("BetterBepInEx.StockManager");
            rebuildMethod = Require(FindMethod(pluginType, "RebuildModel", typeof(void)), "BetterBepInExPlugin.RebuildModel()");
            _rect = Require(FindField(menu, "_rect", typeof(Rect)), "MenuWindow._rect (Rect)");
            _pageDisplayName = Require(FindField(page, "DisplayName", typeof(string)), "Page.DisplayName (string)");
            _pageTitle = Require(FindField(page, "Title", typeof(GUIContent)), "Page.Title (GUIContent)");
            _pageSidebarLabel = Require(FindField(page, "SidebarLabel", typeof(GUIContent)), "Page.SidebarLabel (GUIContent)");
            _rowHaystack = FindField(row, "Haystack", typeof(string));
            _rowOptions = FindField(row, "Options", typeof(object[]));
            _rowOptionLabels = FindField(row, "OptionLabels", typeof(GUIContent[]));
            _countLabels = FindField(menu, "_countLabels", typeof(IDictionary));
            _cachedLabels = FindField(menu, "_cachedLabels", typeof(IDictionary));
            _invalidateLists = FindMethod(menu, "OnModelRebuilt", typeof(void));

            var onGui = FindMethod(menu, "OnGUI", typeof(void));
            var ensureRect = FindMethod(menu, "EnsureRect", typeof(void));
            var build = FindModelHook(model, "Build", typeof(void), typeof(IEnumerable<SettingEntryBase>), typeof(IEnumerable<SettingEntryBase>));
            var pageFor = FindModelHook(model, "PageFor", page, typeof(BepInPlugin));
            var plan = new List<Action<Harmony>>();

            void Add(MethodInfo target, string name, string prefix = null, string postfix = null,
                string transpiler = null, string finalizer = null, bool optional = false)
            {
                if (target == null && optional)
                {
                    warn("Optional hook unavailable: " + name + ".");
                    return;
                }
                Require(target, name);
                plan.Add(harmony =>
                {
                    try { harmony.Patch(target, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer), ilmanipulator: null); }
                    catch (Exception ex) when (optional) { warn("Optional hook unavailable: " + name + ". " + ex.Message); }
                });
            }

            Add(build, "SettingsModel.Build", prefix: nameof(LocalizeModel), transpiler: nameof(LocalizeText));
            Add(pageFor, "SettingsModel.PageFor", postfix: nameof(DecoratePage));
            Add(CallsOnce(onGui, ensureRect) ? onGui : null, "MenuWindow.OnGUI → EnsureRect", transpiler: nameof(DrawWorkbench));
            Add(FindMethod(menu, "DrawSidebar", typeof(void)), "MenuWindow.DrawSidebar",
                prefix: nameof(DrawWorkbenchButtons), transpiler: nameof(LocalizeText));
            Add(FindMethod(pluginType, "Update", typeof(void)), "BetterBepInExPlugin.Update", prefix: nameof(CheckMenuSwitch));
            Add(FindMethod(menu, "OnClose", typeof(void)), "MenuWindow.OnClose", postfix: nameof(ResetWorkbench));

            Add(FindMethod(pluginType, "MenuFontAsset", typeof(Font)), "BetterBepInExPlugin.MenuFontAsset", postfix: nameof(UseChineseFont), optional: true);
            Add(FindModelHook(model, "MakeRow", row, typeof(SettingEntryBase)), "SettingsModel.MakeRow", postfix: nameof(DecorateRow), optional: true);
            Add(FindMethod(model, "OptionText", typeof(string), new[] { typeof(object) }, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                "SettingsModel.OptionText", postfix: nameof(LocalizeOption), optional: true);
            Add(FindMethod(menu, "CountedLabel", typeof(GUIContent), typeof(string), typeof(int), typeof(int), typeof(string)),
                "MenuWindow.CountedLabel", prefix: nameof(LocalizeCount), optional: true);
            foreach (var name in new[] { "UndoTip", "RedoTip" })
                Add(FindMethod(menu, name, typeof(string)), "MenuWindow." + name, postfix: nameof(LocalizeTooltip), transpiler: nameof(LocalizeText), optional: true);

            var drawContent = FindMethod(menu, "DrawContent", typeof(void));
            var drawValue = FindMethod(stock, "DrawValue", typeof(void), typeof(SettingEntryBase));
            if (drawContent != null && drawValue != null)
            {
                Add(drawContent, "MenuWindow.DrawContent", prefix: nameof(BeginContent), finalizer: nameof(EndContent), optional: true);
                Add(drawValue, "StockManager.DrawValue", prefix: nameof(BeginStockValue), finalizer: nameof(EndStockValue), optional: true);
            }
            else warn("Optional hook unavailable: custom setting controls (DrawContent or DrawValue).");

            var block = menu.GetNestedType("Block", BindingFlags.NonPublic | BindingFlags.Public);
            _sectionType = assembly.GetType("BetterBepInEx.Section");
            _blocks = FindField(menu, "_blocks", typeof(IList));
            _blockRow = FindField(block, "Row", row);
            _blockKey = FindField(block, "Key", typeof(object));
            _sectionRows = FindField(_sectionType, "Rows", typeof(IList));
            _sectionIsHiddenGroup = FindField(_sectionType, "IsHiddenGroup", typeof(bool));
            _valueWidth = FindField(menu, "_valueWidth", typeof(float));
            _rowSetting = FindField(row, "Setting", typeof(SettingEntryBase));
            _rowResetLabel = FindField(row, "ResetLabel", typeof(GUIContent));
            var captureRow = _blocks != null && _blockRow != null && _blockKey != null && _sectionRows != null &&
                _sectionIsHiddenGroup != null && _valueWidth != null && _rowSetting != null;
            Add(captureRow ? FindMethod(menu, "DrawBlock", typeof(void), typeof(int), typeof(bool)) : null,
                "MenuWindow.DrawBlock (setting capture)", postfix: nameof(CaptureBlock), optional: true);

            var sidebarPage = FindMethod(menu, "SidebarPage", typeof(void), page);
            _pageGuid = FindField(page, "Guid", typeof(string));
            Add(_pageGuid != null && CallsOnce(sidebarPage, FindSidebarButton(menu)) ? sidebarPage : null, "MenuWindow.SidebarPage → SidebarButton",
                prefix: nameof(BeginPlugin), finalizer: nameof(EndPlugin), transpiler: nameof(CapturePlugin), optional: true);
            var escape = FindMethod(menu, "HandleEscape", typeof(void));
            var setWindow = FindMethod(stock, "set_DisplayingWindow", typeof(void), typeof(bool));
            Add(CallsOnce(escape, setWindow) ? escape : null, "MenuWindow.HandleEscape → DisplayingWindow",
                transpiler: nameof(HandleWorkbenchEscape), optional: true);

            foreach (var method in menu.GetMethods(BindingFlags.DeclaredOnly | InstanceMembers | BindingFlags.Static)
                .Where(method => TextMethods.Contains(method.Name) && method.GetMethodBody() != null))
                Add(method, method.Name, transpiler: nameof(LocalizeText), optional: true);
            return plan;
        }

        private static MethodInfo FindMethod(Type type, string name, Type returnType, params Type[] parameters) =>
            FindMethod(type, name, returnType, parameters, InstanceMembers);

        private static MethodInfo FindMethod(Type type, string name, Type returnType, Type[] parameters, BindingFlags flags)
        {
            var method = type?.GetMethod(name, flags | BindingFlags.ExactBinding, null, parameters, null);
            return method != null && method.ReturnType == returnType && !method.IsGenericMethod && method.GetMethodBody() != null ? method : null;
        }

        // These prefixes and postfixes only consume the leading arguments by index.
        private static MethodInfo FindModelHook(Type type, string name, Type returnType, params Type[] leadingParameters)
        {
            var matches = type.GetMethods(InstanceMembers).Where(method =>
                method.Name == name && method.ReturnType == returnType && !method.IsGenericMethod &&
                method.GetParameters().Take(leadingParameters.Length).Select(parameter => parameter.ParameterType).SequenceEqual(leadingParameters) &&
                method.GetMethodBody() != null).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static MethodInfo FindSidebarButton(Type menu) =>
            FindMethod(menu, "SidebarButton", typeof(bool), typeof(GUIContent), typeof(bool), typeof(int), typeof(GUIStyle), typeof(bool), typeof(bool)) ??
            FindMethod(menu, "SidebarButton", typeof(bool), typeof(GUIContent), typeof(bool), typeof(int), typeof(GUIStyle), typeof(bool));

        private static FieldInfo FindField(Type type, string name, Type valueType)
        {
            var field = type?.GetField(name, InstanceMembers);
            return field != null && valueType.IsAssignableFrom(field.FieldType) ? field : null;
        }

        private static bool CallsOnce(MethodInfo owner, MethodInfo callee) => owner != null && callee != null &&
            PatchProcessor.GetOriginalInstructions(owner).Count(code => code.Calls(callee)) == 1;

        private static T Require<T>(T member, string name) where T : class =>
            member ?? throw new MissingMemberException("BetterBepInEx", name);

        private static HarmonyMethod Hook(string name) =>
            name == null ? null : new HarmonyMethod(typeof(BetterBepInExCompatibility), name);

        internal static void Shutdown()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            _isActive = null;
            _rebuild = null;
            _stock = null;
            _pendingModelBuild = _lastActive = _loggedRefreshError = false;
            _lastDisplayMode = null;
            StockWindowPatches.Clear();
            foreach (var label in MenuLabels)
            {
                label.Key.text = label.Value.text;
                label.Key.tooltip = label.Value.tooltip;
            }
            MenuLabels.Clear();
            DecoratedPages.Clear();
            if (_chineseFont != null) UnityEngine.Object.Destroy(_chineseFont);
            _chineseFont = null;
            _triedChineseFont = false;
        }

        private static void RestoreStockPatches()
        {
            // BetterBepInEx removes all other stock window patches during Start. Restore our own
            // so its in-game toggle can hand control back to a fully functional stock window.
            var stockHarmony = new Harmony(ConfigurationManagerZhCnPlugin.PluginGuid);
            foreach (var entry in StockWindowPatches)
            {
                var current = Harmony.GetPatchInfo(entry.Key);
                foreach (var patch in MissingPatches(entry.Value.Prefixes, current?.Prefixes))
                    stockHarmony.Patch(entry.Key, prefix: patch);
                foreach (var patch in MissingPatches(entry.Value.Postfixes, current?.Postfixes))
                    stockHarmony.Patch(entry.Key, postfix: patch);
                foreach (var patch in MissingPatches(entry.Value.Transpilers, current?.Transpilers))
                    stockHarmony.Patch(entry.Key, transpiler: patch);
                foreach (var patch in MissingPatches(entry.Value.Finalizers, current?.Finalizers))
                    stockHarmony.Patch(entry.Key, finalizer: patch);
            }
            StockWindowPatches.Clear();
        }

        private static IEnumerable<HarmonyMethod> MissingPatches(IEnumerable<Patch> before, IEnumerable<Patch> after)
        {
            foreach (var patch in before)
                if (patch.owner == ConfigurationManagerZhCnPlugin.PluginGuid &&
                    !(after?.Any(existing => existing.owner == patch.owner && existing.PatchMethod == patch.PatchMethod) ?? false))
                    yield return new HarmonyMethod(patch.PatchMethod, patch.priority, patch.before, patch.after);
        }

        private static void CheckMenuSwitch()
        {
            var active = Active;
            if (active && !_lastActive && _pendingModelBuild)
                TryRefresh();
            if (!active && _lastActive)
            {
                _pendingModelBuild = true;
                ConfigurationManagerScale.InvalidateLayout();
                ResetWorkbench();
            }
            _lastActive = active;
        }

        internal static bool TryRefresh()
        {
            if (!Active) return false;
            try { _rebuild(); }
            catch (Exception ex)
            {
                if (!_loggedRefreshError)
                {
                    _loggedRefreshError = true;
                    _log.LogWarning("Could not refresh the BetterBepInEx settings model: " + ex);
                }
            }
            return true;
        }

        private static void LocalizeModel(IEnumerable<SettingEntryBase> __0, IEnumerable<SettingEntryBase> __1)
        {
            DecoratedPages.Clear();
            if (!Active)
            {
                _pendingModelBuild = true;
                return;
            }
            _pendingModelBuild = false;
            JsoncLocalization.BeginCollection();
            SettingSearcherPatches.LocalizeSettings(__0.Concat(__1), Chainloader.PluginInfos.Values.Select(info => info.Instance));
        }

        private static void DecoratePage(BepInPlugin __0, object __result)
        {
            if (!Active || __result == null || !DecoratedPages.Add(__result)) return;
            var item = JsoncLocalization.GetPluginWorkItem(__0.GUID);
            if (item == null) return;
            var title = JsoncLocalization.GetDisplayValue(item);
            if (string.IsNullOrWhiteSpace(title) || title == item.Source) return;
            _pageDisplayName.SetValue(__result, title);
            if (_pageTitle.GetValue(__result) is GUIContent titleLabel) titleLabel.text = title;
            if (_pageSidebarLabel.GetValue(__result) is GUIContent sidebarLabel) sidebarLabel.text = title;
        }

        private static void LocalizeOption(object __0, ref string __result)
        {
            if (__0 is Enum value)
                __result = JsoncLocalization.GetEnumLocalizedDescription(value.GetType(), value.ToString()) ??
                    UiLocalization.GetBetterEnumText(value.GetType(), __result);
            else if (__0 == null)
                __result = UiLocalization.GetBetterMenuText(__result);
        }

        private static void DecorateRow(SettingEntryBase __0, object __result)
        {
            if (!Active || __result == null) return;
            var original = JsoncLocalization.GetOriginalSetting(__0);
            if (original != null && _rowHaystack != null)
            {
                var haystack = (string)_rowHaystack.GetValue(__result);
                _rowHaystack.SetValue(__result, haystack +
                    ("\n" + original.Category + "\n" + original.DispName + "\n" + original.Description).ToLowerInvariant());
            }
            var options = (object[])_rowOptions?.GetValue(__result);
            var labels = (GUIContent[])_rowOptionLabels?.GetValue(__result);
            if (options != null && labels != null && options.Length == labels.Length)
                for (var index = 0; index < options.Length; index++)
                    if (options[index] != null && !(options[index] is Enum) && labels[index] != null)
                        labels[index].text = JsoncLocalization.GetCustomControlDisplay(__0, labels[index].text);
        }

        private static void LocalizeCount(ref string __0, ref string __3)
        {
            __0 = UiLocalization.GetBetterMenuText(__0);
            __3 = UiLocalization.GetBetterMenuText(__3);
        }

        private static void LocalizeTooltip(ref string __result) => __result = UiLocalization.GetBetterMenuText(__result);

        private static void UseChineseFont(ref Font __result)
        {
            if (JsoncLocalization.CurrentDisplayMode == LocalizationDisplayMode.English ||
                (__result != null && __result.HasCharacter('汉') && __result.HasCharacter('设'))) return;
            if (!_triedChineseFont)
            {
                _triedChineseFont = true;
                try
                {
                    var fonts = Font.GetOSInstalledFontNames();
                    var family = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" }
                        .FirstOrDefault(name => fonts.Contains(name, StringComparer.OrdinalIgnoreCase));
                    if (family != null)
                    {
                        _chineseFont = Font.CreateDynamicFontFromOSFont(family, 16);
                        if (_chineseFont != null) _chineseFont.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
                catch (Exception ex) { _log.LogWarning("Could not load a Chinese font for BetterBepInEx: " + ex.Message); }
            }
            if (_chineseFont != null) __result = _chineseFont;
        }

        private static void DrawWorkbenchButtons() => TranslationPanel.DrawBetterMenuButtons();

        private static void DrawWorkbenchWindow(object menu)
        {
            if (Event.current.type == EventType.Layout)
                RefreshMenuLabels(menu);
            TranslationPanel.DrawSidePanel(_stock, (Rect)_rect.GetValue(menu));
        }

        private static void RefreshMenuLabels(object menu)
        {
            var mode = JsoncLocalization.CurrentDisplayMode;
            if (_lastDisplayMode == mode) return;
            var type = menu.GetType();
            if (MenuLabels.Count == 0)
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                    .Where(field => field.FieldType == typeof(GUIContent) && field.IsInitOnly))
                {
                    var label = (GUIContent)field.GetValue(null);
                    if (label != null) MenuLabels.Add(new KeyValuePair<GUIContent, GUIContent>(label, new GUIContent(label)));
                }
            foreach (var label in MenuLabels)
            {
                label.Key.text = UiLocalization.GetBetterMenuText(label.Value.text);
                label.Key.tooltip = UiLocalization.GetBetterMenuText(label.Value.tooltip);
            }
            (_countLabels?.GetValue(menu) as IDictionary)?.Clear();
            (_cachedLabels?.GetValue(menu) as IDictionary)?.Clear();
            if (_lastDisplayMode.HasValue) _invalidateLists?.Invoke(menu, null);
            _lastDisplayMode = mode;
        }

        private static void BeginContent() => TranslationPanel.BeginManagerContent();
        private static void EndContent() => TranslationPanel.EndManagerContent();
        private static void BeginStockValue(SettingEntryBase __0) => TranslationPanel.BeginSettingValue(__0);
        private static void EndStockValue() => TranslationPanel.EndSettingValue();
        private static void ResetWorkbench() => TranslationPanel.ResetMenuState();
        private static void BeginPlugin(object __0) => TranslationPanel.BeginBetterMenuPlugin((string)_pageGuid.GetValue(__0));
        private static void EndPlugin() => TranslationPanel.EndPlugin();

        private static void CaptureBlock(object __instance, int __0, bool __1)
        {
            var capturing = !__1 && TranslationPanel.CapturingMenuText;
            var localizeResetLabel = Event.current.type == EventType.Repaint &&
                JsoncLocalization.CurrentDisplayMode != LocalizationDisplayMode.English;
            if (!capturing && !localizeResetLabel) return;

            var blocks = (IList)_blocks.GetValue(__instance);
            if (blocks == null || __0 < 0 || __0 >= blocks.Count) return;
            var block = blocks[__0];
            if (block == null || !_blockRow.DeclaringType.IsInstanceOfType(block)) return;
            var row = _blockRow.GetValue(block);
            if (row != null && localizeResetLabel)
            {
                var label = (GUIContent)_rowResetLabel?.GetValue(row);
                if (label?.text == "Reset")
                {
                    label.text = UiLocalization.GetBetterMenuText("Reset");
                    const string tooltipPrefix = "Back to ";
                    if (label.tooltip?.StartsWith(tooltipPrefix, StringComparison.Ordinal) == true)
                        label.tooltip = UiLocalization.GetBetterMenuText(tooltipPrefix) + label.tooltip.Substring(tooltipPrefix.Length);
                }
            }
            if (!capturing) return;

            var rect = GUILayoutUtility.GetLastRect();
            if (row != null)
            {
                rect.width = Mathf.Max(0, rect.width - (float)_valueWidth.GetValue(__instance));
                TranslationPanel.CaptureSettingName((SettingEntryBase)_rowSetting.GetValue(row), rect);
                return;
            }
            var key = _blockKey.GetValue(block);
            if (key == null || !_sectionType.IsInstanceOfType(key) || (bool)_sectionIsHiddenGroup.GetValue(key)) return;
            var rows = (IList)_sectionRows.GetValue(key);
            if (rows != null && rows.Count > 0 && rows[0] != null)
                TranslationPanel.CaptureBetterMenuCategory((SettingEntryBase)_rowSetting.GetValue(rows[0]), rect);
        }

        private static bool CapturePluginLabel(bool clicked) => TranslationPanel.CapturingMenuText
            ? TranslationPanel.CapturePluginHeader(clicked, GUILayoutUtility.GetLastRect()) : clicked;

        private static bool KeepMenuOpen(bool displaying) =>
            displaying || TranslationPanel.QueueBetterMenuEscape();

        private static IEnumerable<CodeInstruction> DrawWorkbench(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            InsertAfter(instructions, FindMethod(__originalMethod.DeclaringType, "EnsureRect", typeof(void)),
                new CodeInstruction(OpCodes.Ldarg_0), Call(nameof(DrawWorkbenchWindow)));

        private static IEnumerable<CodeInstruction> CapturePlugin(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            InsertAfter(instructions, FindSidebarButton(__originalMethod.DeclaringType), Call(nameof(CapturePluginLabel)));

        private static IEnumerable<CodeInstruction> HandleWorkbenchEscape(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var stock = __originalMethod.DeclaringType.Assembly.GetType("BetterBepInEx.StockManager");
            var setter = FindMethod(stock, "set_DisplayingWindow", typeof(void), typeof(bool));
            foreach (var code in instructions)
            {
                if (code.Calls(setter))
                {
                    var keepOpen = Call(nameof(KeepMenuOpen));
                    keepOpen.labels.AddRange(code.labels);
                    code.labels.Clear();
                    yield return keepOpen;
                }
                yield return code;
            }
        }

        private static IEnumerable<CodeInstruction> LocalizeText(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var code in instructions)
            {
                yield return code;
                if (code.opcode == OpCodes.Ldstr && code.operand is string text &&
                    text.Length > 1 && UiLocalization.HasBetterMenuText(text))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UiLocalization), nameof(UiLocalization.GetBetterMenuText)));
            }
        }

        private static IEnumerable<CodeInstruction> InsertAfter(IEnumerable<CodeInstruction> instructions,
            MethodInfo target, params CodeInstruction[] addition)
        {
            var found = false;
            foreach (var code in instructions)
            {
                yield return code;
                if (code.Calls(target))
                {
                    if (found) throw new InvalidOperationException("Multiple matches for a BetterBepInEx UI hook.");
                    found = true;
                    foreach (var instruction in addition) yield return instruction;
                }
            }
            if (!found) throw new MissingMemberException("A BetterBepInEx UI hook target is missing.");
        }

        private static CodeInstruction Call(string name) =>
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BetterBepInExCompatibility), name));
    }
}
