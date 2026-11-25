using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using ConfigurationManagerZhCn;

internal static partial class Program
{
    private const BindingFlags PickerFlags = BindingFlags.Static | BindingFlags.NonPublic;
    private static object PickerCall(string name, params object[] args) => typeof(TranslationPanel).GetMethod(name, PickerFlags).Invoke(null, args);
    private static void PickerSet(string name, object value) => typeof(TranslationPanel).GetField(name, PickerFlags).SetValue(null, value);
    private static T PickerGet<T>(string name) => (T)typeof(TranslationPanel).GetField(name, PickerFlags).GetValue(null);

    private static void CheckPluginSelection()
    {
        Check(!TranslationPanel.UsesBatchPluginSelection(0, false) && TranslationPanel.UsesBatchPluginSelection(1, false) &&
            TranslationPanel.UsesBatchPluginSelection(2, false) && !TranslationPanel.UsesBatchPluginSelection(2, true) &&
            !TranslationPanel.UsesBatchPluginSelection(4, false), "single-editor and API batch selection modes are distinct");
        Check(TranslationPanel.MatchesPluginSearch("Amand Graphics", "图形设置", "com.amand.graphics", "图形") &&
            TranslationPanel.MatchesPluginSearch("Amand Graphics", "图形设置", "com.amand.graphics", "AMAND") &&
            TranslationPanel.MatchesPluginSearch("Amand Graphics", "图形设置", "com.amand.graphics", "com.amand") &&
            !TranslationPanel.MatchesPluginSearch("Amand", "图形", "com.amand", "missing"), "picker searches English, Chinese and GUID without case sensitivity");
        foreach (var size in new[] { new[] { 320f, 480f }, new[] { 640f, 480f }, new[] { 1920f, 1080f } })
        {
            var rect = TranslationPanel.PluginPickerRect(size[0], size[1]);
            Check(rect.x >= 0 && rect.y >= 0 && rect.xMax <= size[0] && rect.yMax <= size[1], "picker rectangle fits screen");
        }
        var previousRoot = Paths.PluginPath;
        var root = Path.Combine(Path.GetTempPath(), "cm-plugin-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            typeof(Paths).GetProperty("PluginPath").SetValue(null, root);
            JsoncLocalization.Initialize(null, LocalizationDisplayMode.Chinese, true, true, true);
            var config = new ConfigFile(Path.Combine(root, "selection.cfg"), false);
            PickerCall("InitializePluginSelection", config);
            Check(!PickerGet<bool>("_selectionInitialized"), "first run has no implicit all-plugins selection");
            var a = new LocalizationSnapshot { Guid = "picker.a", PluginName = "Alpha" };
            a.Settings.Add(new SettingSnapshot { StorageKey = "Enabled", Category = "General", DispName = "Enabled", Description = "Enable feature" });
            var b = new LocalizationSnapshot { Guid = "picker.b", PluginName = "Beta" };
            b.Settings.Add(new SettingSnapshot { StorageKey = "Enabled", Category = "General", DispName = "Enabled", Description = "Enable beta" });
            PickerSet("_snapshots", new List<LocalizationSnapshot> { a, b });
            PickerSet("_selectedPluginGuid", a.Guid); PickerSet("_pluginIndex", 0);
            PickerSet("_editorItems", a.CreateWorkItems());
            var batch = PickerGet<HashSet<string>>("BatchSelection"); batch.Clear(); batch.Add(a.Guid);
            PickerSet("_selectionInitialized", true);
            PickerCall("OpenPluginPicker", true);
            var draft = PickerGet<HashSet<string>>("PickerDraft"); draft.Clear(); draft.Add(b.Guid);
            Check(batch.SetEquals(new[] { a.Guid }), "checkbox edits are staged, not immediately applied");
            PickerCall("CancelPluginPicker");
            Check(batch.SetEquals(new[] { a.Guid }), "cancel leaves batch selection untouched");
            PickerCall("OpenPluginPicker", true); draft.Clear(); draft.Add(b.Guid);
            PickerCall("ApplyPluginBatchSelection");
            Check(batch.SetEquals(new[] { b.Guid }) && PickerGet<string>("_selectedPluginGuid") == a.Guid,
                "batch confirmation applies only batch targets, not the current editor");
            PickerCall("OpenPluginPicker", true); draft.Clear(); PickerCall("ApplyPluginBatchSelection");
            PickerCall("InitializePluginSelection", config);
            Check(batch.Count == 0 && PickerGet<bool>("_selectionInitialized"), "explicit empty batch remains empty after reopening");
            var item = a.CreateWorkItems().First(value => value.Field == "Description");
            SetCurrent(item);
            Action dirty = () =>
            {
                PickerSet("_selectedPluginGuid", a.Guid); PickerSet("_pluginIndex", 0); PickerSet("_editorItems", a.CreateWorkItems());
                PickerSet("_manualLoaded", true); PickerSet("_manualDraftItem", item);
                PickerSet("_manualOriginalValue", ""); PickerSet("_manualValue", "启用功能");
            };
            dirty(); PickerCall("RequestPluginChange", b.Guid);
            Check(PickerGet<bool>("_pickerConfirm") && PickerGet<string>("_selectedPluginGuid") == a.Guid, "dirty plugin switch waits for a decision");
            PickerCall("CancelPluginPicker");
            Check(PickerGet<string>("_selectedPluginGuid") == a.Guid && PickerGet<string>("_manualValue") == "启用功能" && PickerGet<bool>("_manualLoaded"),
                "cancel keeps draft and active plugin");
            PickerCall("OpenPluginPicker", false); PickerCall("RequestPluginChange", b.Guid); PickerCall("CancelPluginPicker");
            Check(PickerGet<bool>("_pickerRequested") && !PickerGet<bool>("_pickerConfirm"), "cancel confirmation returns to the original picker");
            PickerCall("RequestPluginChange", b.Guid);
            PickerSet("_snapshots", new List<LocalizationSnapshot> { a });
            PickerCall("ResolveEditorNavigation", false);
            Check(PickerGet<string>("_selectedPluginGuid") == a.Guid && PickerGet<bool>("_manualLoaded") &&
                PickerGet<bool>("_pickerConfirm") && PickerGet<string>("_pickerError").Contains("已不可用"),
                "disappearing navigation target cannot discard an unsaved draft");
            PickerSet("_snapshots", new List<LocalizationSnapshot> { a, b });
            PickerCall("CancelPluginPicker");
            PickerCall("RequestPluginChange", b.Guid); PickerCall("ResolveEditorNavigation", false);
            Check(PickerGet<string>("_selectedPluginGuid") == b.Guid && !PickerGet<bool>("_manualLoaded") && batch.Count == 0,
                "discard switches editor without silently changing batch targets");
            PickerCall("InitializePluginSelection", config);
            Check(PickerGet<string>("_selectedPluginGuid") == b.Guid, "last edited plugin is restored from config");
            dirty(); PickerCall("RequestPluginChange", b.Guid); PickerCall("ResolveEditorNavigation", true);
            Check(PickerGet<string>("_selectedPluginGuid") == b.Guid && JsoncLocalization.GetStoredValue(item, TranslationLayer.Manual) == "启用功能",
                "save-before-switch commits the original field and then changes plugin");
            dirty();
            var path = JsoncLocalization.GetTranslationPath(a.Guid);
            File.WriteAllText(path, "{\"Tracking\":true}");
            PickerCall("RequestPluginChange", b.Guid); PickerCall("ResolveEditorNavigation", true);
            Check(PickerGet<string>("_selectedPluginGuid") == a.Guid && PickerGet<bool>("_pickerConfirm") &&
                PickerGet<string>("_manualValue") == "启用功能" && PickerGet<string>("_pickerError").Contains("保存失败"),
                "failed saves keep the confirmation open, original plugin and unsaved text");
            File.Delete(path); PickerCall("CancelPluginPicker");
            a.Settings[0].Description = "Changed source";
            PickerCall("RequestPluginChange", b.Guid); PickerCall("ResolveEditorNavigation", true);
            Check(PickerGet<string>("_selectedPluginGuid") == a.Guid && PickerGet<string>("_pickerError").Contains("原文已变化"),
                "source changes cannot silently approve stale drafts");
            a.Settings[0].Description = item.Source; PickerCall("CancelPluginPicker");
            dirty(); PickerSet("_editorIndex", 0);
            PickerCall("MoveEditor", a.CreateWorkItems(), 1); PickerCall("SyncPluginPickerActions");
            Check(PickerGet<int>("_editorIndex") == 0 && PickerGet<bool>("_pickerConfirm"), "field navigation cannot bypass dirty protection");
            PickerCall("CancelPluginPicker");
            dirty(); PickerSet("_pendingCapture", null);
            PickerCall("SelectCapturedItem", b.CreateWorkItems()[0], "capture"); PickerCall("SyncPluginPickerActions");
            Check(PickerGet<object>("_pendingCapture") == null && PickerGet<bool>("_pickerConfirm"), "mouse capture cannot bypass dirty protection");
            PickerCall("CancelPluginPicker");
        }
        finally
        {
            PickerCall("ResetPluginPicker"); PickerSet("_manualLoaded", false); PickerSet("_manualDraftItem", null);
            PickerSet("_lastPluginConfig", null); PickerSet("_batchPluginsConfig", null);
            typeof(Paths).GetProperty("PluginPath").SetValue(null, previousRoot);
            Directory.Delete(root, true);
        }
    }
}
