using System;
using System.IO;
using System.Reflection;
using BepInEx;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private static void CheckStorageLayout()
    {
        var previousRoot = Paths.PluginPath;
        var root = Path.Combine(Path.GetTempPath(), "cm-storage-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "zh-cn");
        Directory.CreateDirectory(Path.Combine(folder, "API"));
        try
        {
            typeof(Paths).GetProperty("PluginPath", BindingFlags.Public | BindingFlags.Static).SetValue(null, root);
            var legacy = "// preserve comment\n{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"旧人工\"}}}";
            var guid = "layout.plugin";
            var translations = Path.Combine(folder, "translations");
            foreach (var layer in new[] { "Manual", "API", "MT" })
                Directory.CreateDirectory(Path.Combine(translations, layer));
            var earliestManual = Path.Combine(folder, guid + ".jsonc");
            File.WriteAllText(earliestManual, legacy);
            File.WriteAllText(Path.Combine(folder, guid + ".translations.jsonc"),
                "{\"Tracking\":{\"Baseline\":{}},\"Manual\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"人工覆盖\"}}},\"Machine\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"机翻\"}}}}");
            File.WriteAllText(Path.Combine(folder, "API", guid + ".jsonc"), "{\"AI\":{}}");
            File.WriteAllText(Path.Combine(folder, "API", guid + ".jsonc.bak"), "backup bytes");
            File.WriteAllText(Path.Combine(folder, "UI.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "UI.jsonc"), legacy);
            File.WriteAllText(Path.Combine(folder, "unrelated.jsonc"), "{\"option\":true}");
            var layered = "layered.plugin";
            var oldManual = Path.Combine(translations, "Manual", layered + ".jsonc");
            var oldApi = Path.Combine(translations, "API", layered + ".jsonc");
            var oldMachine = Path.Combine(translations, "MT", layered + ".jsonc");
            var oldOverride = Path.Combine(translations, "Manual", layered + ".overrides.jsonc");
            File.WriteAllText(Path.Combine(folder, layered + ".jsonc"), legacy);
            File.WriteAllText(oldManual, legacy.Replace("旧人工", "分层人工"));
            File.WriteAllText(oldOverride, "{\"Manual\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"分层校订\"}}}}");
            File.WriteAllText(oldApi, "// api comment\n{\"AI\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"分层接口\"}}}}");
            File.WriteAllText(oldMachine, "{\"Machine\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"分层机翻\"}}}}");
            var activeApi = Path.Combine(translations, layered + "_API.jsonc");
            File.WriteAllText(activeApi, "{\"AI\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"新版接口\"}}}}");
            var broken = Path.Combine(translations, "API", "broken.plugin.jsonc");
            File.WriteAllText(broken, "{ broken");
            Directory.CreateDirectory(Path.Combine(folder, "diagnostics"));
            File.WriteAllText(Path.Combine(folder, "diagnostics", "task.jsonl"), "log bytes");
            JsoncLocalization.MigrateAutomaticFiles();
            Check(File.ReadAllText(Path.Combine(translations, guid + "_Manual.jsonc")) == legacy &&
                File.ReadAllText(earliestManual) == legacy, "earliest manual file is copied byte-for-byte with original retained");
            Check(File.ReadAllText(Path.Combine(translations, layered + "_Manual.jsonc")) == File.ReadAllText(oldManual) &&
                File.ReadAllText(Path.Combine(translations, layered + "_MT.jsonc")) == File.ReadAllText(oldMachine) &&
                File.ReadAllText(Path.Combine(translations, layered + "_Manual.overrides.jsonc")) == File.ReadAllText(oldOverride),
                "layered files win over older manual copies and keep their separate override and machine identities");
            var layeredItem = Item(guid: layered);
            Check(JsoncLocalization.GetDisplayValue(layeredItem) == "分层校订" &&
                JsoncLocalization.GetStoredValue(layeredItem, TranslationLayer.AI) == "新版接口" &&
                File.ReadAllText(oldApi).Contains("分层接口"), "existing flat files win conflicts without modifying the old layer");
            Check(File.ReadAllText(broken) == "{ broken" && !File.Exists(Path.Combine(translations, "broken.plugin_API.jsonc")),
                "broken legacy JSONC remains untouched and is not copied");
            Check(!File.Exists(Path.Combine(translations, "UI_Manual.jsonc")) &&
                !File.Exists(Path.Combine(translations, "unrelated_Manual.jsonc")), "UI and unrelated root JSONC stay outside translation storage");
            var metadata = JObject.Parse(File.ReadAllText(JsoncLocalization.GetTranslationPath(guid)));
            Check(metadata["Tracking"] != null && metadata["Manual"] == null && metadata["Machine"] == null, "baselines contain no manual or automatic translation layer");
            Check(File.ReadAllText(JsoncLocalization.GetTranslationPath(guid, TranslationLayer.Manual)).Contains("人工覆盖"), "workbench manual overrides have a separate translation file");
            var item = Item(guid: guid);
            Check(JsoncLocalization.GetDisplayValue(item) == "人工覆盖", "manual precedence survives split storage");
            Check(File.ReadAllText(Path.Combine(folder, "cache", "diagnostics", "task.jsonl")) == "log bytes", "diagnostics move under cache without edits");
            Check(Directory.GetFiles(Path.Combine(folder, "translations"), "*.bak", SearchOption.AllDirectories).Length == 0
                && File.Exists(Path.Combine(folder, "UI.json")), "backups are outside translations and UI remains at the root");
            var count = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length;
            JsoncLocalization.MigrateAutomaticFiles();
            Check(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == count, "migration is idempotent");

            var late = Item(guid: "late.plugin");
            var lateOld = Path.Combine(translations, "API", late.PluginGuid + ".jsonc");
            var lateNew = Path.Combine(translations, late.PluginGuid + "_API.jsonc");
            File.WriteAllText(lateOld, "{\"AI\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"后装译文\"}}}}");
            Check(JsoncLocalization.GetDisplayValue(late) == "后装译文", "an old language pack installed after startup works through the reader");
            File.WriteAllText(lateNew, "{\"AI\":{\"ConfigEntries\":{\"Enabled\":{\"Description\":\"扁平译文\"}}}}");
            File.SetLastWriteTimeUtc(lateNew, File.GetLastWriteTimeUtc(lateOld));
            typeof(JsoncLocalization).GetField("_nextReloadCheckUtc", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, DateTime.MinValue);
            Check(JsoncLocalization.GetDisplayValue(late) == "扁平译文", "same-timestamp path switch invalidates the cache");
            JsoncLocalization.SaveLayer(late.PluginGuid, TranslationLayer.AI, new[] { late }, Values(late, ""));
            JsoncLocalization.MigrateAutomaticFiles();
            Check(JsoncLocalization.GetDisplayValue(late) == late.Source && File.ReadAllText(lateOld).Contains("后装译文"),
                "cleared flat translations suppress old copies across a later migration");

            var legacyOnly = Item(guid: "late-machine.plugin");
            var legacyMachinePath = Path.Combine(translations, "MT", legacyOnly.PluginGuid + ".jsonc");
            const string oldText = "{\"Machine\":{\"ConfigEntries\":{\"Other\":{\"Description\":\"旧条目\"}}}}";
            File.WriteAllText(legacyMachinePath, oldText);
            JsoncLocalization.SaveLayer(legacyOnly.PluginGuid, TranslationLayer.Machine, new[] { legacyOnly }, Values(legacyOnly, "新机翻"));
            var writtenPath = Path.Combine(translations, legacyOnly.PluginGuid + "_MT.jsonc");
            Check(File.ReadAllText(legacyMachinePath) == oldText && File.ReadAllText(writtenPath).Contains("旧条目") &&
                JsoncLocalization.GetDisplayValue(legacyOnly) == "新机翻", "saving from fallback copies the active layer to the flat path");
        }
        finally
        {
            typeof(Paths).GetProperty("PluginPath", BindingFlags.Public | BindingFlags.Static).SetValue(null, previousRoot);
            Directory.Delete(root, true);
        }
    }
}
