using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ConfigurationManager;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private sealed class UnknownSetting : SettingEntryBase
    {
        internal UnknownSetting()
        {
            Category = "General";
            DispName = "Keyboard Shortcut";
            Description = "Original description";
            PluginInfo = new BepInPlugin("collection.test", "Original plugin", "1.0.0");
        }
        public override Type SettingType => typeof(string);
        public override object Get() { return "unchanged"; }
        protected override void SetValue(object value) { throw new Exception("Must not write configuration values"); }
    }

    private static LocalizationSnapshot CollectionSnapshot(params SettingEntryBase[] entries)
    {
        return (LocalizationSnapshot)typeof(JsoncLocalization).GetMethod("BuildSnapshot", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { "collection.test", "Original plugin", entries });
    }

    private static void SetSettingText(SettingEntryBase entry, string property, string value)
    {
        typeof(SettingEntryBase).GetProperty(property).SetValue(entry, value);
    }

    private static void CheckSettingCollectionResilience()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-collection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigFile(Path.Combine(root, "fixture.cfg"), false);
            Func<string, string, SettingEntryBase> entry = (section, key) =>
            {
                var value = config.Bind(section, key, "unchanged", "Original description");
                var setting = (SettingEntryBase)Activator.CreateInstance(typeof(SettingEntryBase).Assembly.GetType("ConfigurationManager.ConfigSettingEntry"),
                    new object[] { value, null });
                SetSettingText(setting, "Category", "General");
                SetSettingText(setting, "DispName", "Keyboard Shortcut");
                return setting;
            };
            var first = entry("ActualSection", "FirstHotkey");
            var second = entry("ActualSection", "SecondHotkey");
            var snapshot = CollectionSnapshot(first, second);
            var index = JsoncLocalization.BuildItemIndex(snapshot);
            Check(index.Count == 7 && snapshot.Settings.Select(s => s.StorageKey).Distinct().Count() == 2,
                "same-category same-name settings get separate work items using real config definitions");
            Check(snapshot.Settings.All(s => s.HasAmbiguousLegacyKey), "ambiguous display aliases are identified");
            var reverse = CollectionSnapshot(second, first);
            Check(snapshot.Settings[0].StorageKey == reverse.Settings[1].StorageKey && snapshot.Settings[1].StorageKey == reverse.Settings[0].StorageKey,
                "collision identities survive collection reorder");
            var aliases = typeof(JsoncLocalization).GetMethod("CandidateKeys", BindingFlags.NonPublic | BindingFlags.Static);
            Check(((string[])aliases.Invoke(null, new object[] { snapshot.Settings[0] })).SequenceEqual(new[] { snapshot.Settings[0].StorageKey }),
                "ambiguous old translations cannot be reused across distinct config entries");
            SetSettingText(second, "Category", "Other");
            var ordinary = CollectionSnapshot(first, second);
            Check(ordinary.Settings[0].StorageKey == "[General]Keyboard Shortcut" && ordinary.Settings[1].StorageKey == "[Other]Keyboard Shortcut",
                "existing noncolliding translation keys remain compatible");
            Check(((string[])aliases.Invoke(null, new object[] { ordinary.Settings[0] })).Contains("Keyboard Shortcut"),
                "ordinary settings retain legacy translation aliases");
            SetSettingText(second, "Category", "General");
            var duplicate = CollectionSnapshot(first, first);
            Check(JsoncLocalization.BuildItemIndex(duplicate).Count == 4, "repeated collection of one config entry is idempotent");
            const string reportedKey = "@config:25:Mattdokn.BossNotifier.cfg7:General17:Keyboard Shortcut";
            var reported = new LocalizationSnapshot { Guid = "Mattdokn.BossNotifier", PluginName = "BossNotifier", PluginVersion = "1.0" };
            for (var copy = 0; copy < 2; copy++)
                reported.Settings.Add(new SettingSnapshot { StorageKey = reportedKey, Category = "General", DispName = "Keyboard Shortcut", Description = "Toggle notifications" });
            Check(TranslationTracking.Compare(null, reported.CreateWorkItems()).Count == 4,
                "reported BossNotifier duplicate key passes change review with one record per identity");
            Check(reported.CreateWorkItems().Count == 4 && reported.CreateWorkItems(TranslationScope.All).Count == 4,
                "editor and scoped batch share normalized work items");
            var repeatedItems = reported.CreateWorkItems().Concat(reported.CreateWorkItems()).ToList();
            Check(TranslationTracking.Compare(null, repeatedItems).Count == 4, "change-review boundary also handles raw repeated inputs");
            var baseline = new JObject();
            TranslationTracking.EnsureBaseline(baseline, reported.CreateWorkItems());
            Check(TranslationTracking.Compare(baseline, reported.CreateWorkItems()).Count == 0, "repeated records do not create false changes");
            var inventories = new Dictionary<string, TranslationInventory> { [reported.Guid] = new TranslationInventory() };
            var plan = TranslationBatchPlan.Create(new[] { reported }, new HashSet<string> { reported.Guid }, TranslationScope.All,
                false, null, false, false, inventories);
            Check(plan.TotalItems == 4, "repeated snapshot items cannot inflate paid batch counts");
            var pack = new TranslationExchange(); pack.Local.AddRange(reported.CreateWorkItems());
            var response = new JObject();
            for (var itemIndex = 0; itemIndex < pack.Local.Count; itemIndex++) response["t" + (itemIndex + 1).ToString("D4")] = pack.Local[itemIndex].Source;
            Check(pack.Parse(response.ToString(), true).Count == 4, "offline return mapping keeps one item per identity");
            var wrapperOne = entry("General", "Keyboard Shortcut");
            var wrapperTwo = entry("General", "Keyboard Shortcut");
            var wrapped = CollectionSnapshot(wrapperOne, wrapperTwo);
            Check(!ReferenceEquals(wrapperOne, wrapperTwo) && wrapped.Settings.Count == 2 && wrapped.CreateWorkItems().Count == 4,
                "distinct wrappers of one ConfigEntry retain positional mapping while work items are unique");
            Check(TranslationTracking.Compare(null, wrapped.CreateWorkItems()).Count == 4 &&
                (string)wrapperOne.Get() == "unchanged" && (string)wrapperTwo.Get() == "unchanged",
                "real duplicate wrappers cannot break review or change stored configuration values");
            var conflicting = TranslationWorkItem.ForSetting(reported.Guid, reportedKey, "Category", "General", "different context");
            Reject(() => TranslationTracking.Compare(null, new[] { reported.CreateWorkItems().First(item => item.Field == "Category"), conflicting }),
                "change-review boundary rejects conflicting identity metadata instead of choosing a translation");
            duplicate.Settings[1].Description = "Different description";
            Reject(() => JsoncLocalization.BuildItemIndex(duplicate), "conflicting identities fail safely instead of silently overwriting translations");
            var reservedName = snapshot.Settings[0].StorageKey;
            var reserved = entry("OtherSection", "LiteralName");
            SetSettingText(reserved, "DispName", reservedName);
            var reservedSnapshot = CollectionSnapshot(first, second, reserved);
            Check(JsoncLocalization.BuildItemIndex(reservedSnapshot).Count == 10,
                "generated identity cannot collide with a literal display key");
            var delimited = CollectionSnapshot(entry("a:b", "c"), entry("a", "b:c"));
            Check(delimited.Settings[0].StorageKey != delimited.Settings[1].StorageKey, "identity encoding is unambiguous with delimiters");
            var otherFile = new ConfigFile(Path.Combine(root, "other.cfg"), false);
            var otherValue = otherFile.Bind("ActualSection", "FirstHotkey", "unchanged", "Original description");
            var otherSetting = (SettingEntryBase)Activator.CreateInstance(typeof(SettingEntryBase).Assembly.GetType("ConfigurationManager.ConfigSettingEntry"),
                new object[] { otherValue, null });
            SetSettingText(otherSetting, "DispName", "Keyboard Shortcut");
            SetSettingText(otherSetting, "Category", "General");
            var twoFiles = CollectionSnapshot(first, otherSetting);
            Check(twoFiles.Settings[0].StorageKey != twoFiles.Settings[1].StorageKey,
                "same section and key in different config files remain distinct");

            var unknown = new UnknownSetting();
            var unknown2 = new UnknownSetting();
            Reject(() => CollectionSnapshot(unknown, unknown2), "unidentifiable custom collisions reject unsafe persisted translations");
            var log = new ManualLogSource("collection-regression");
            var errors = new List<string>();
            log.LogEvent += (sender, args) => errors.Add(args.Data.ToString());
            var logField = typeof(JsoncLocalization).GetField("_log", BindingFlags.NonPublic | BindingFlags.Static);
            var oldLog = logField.GetValue(null);
            logField.SetValue(null, log);
            try
            {
                var guid = "collection-failure-" + Guid.NewGuid().ToString("N");
                for (var attempt = 0; attempt < 2; attempt++)
                    SettingSearcherPatches.LocalizePlugin(guid, new[] { unknown }, originals =>
                    {
                        SetSettingText(unknown, "Category", "Changed category");
                        SetSettingText(unknown, "DispName", "Changed label");
                        SetSettingText(unknown, "Description", "Changed description");
                        JsoncLocalization.TrySetPluginMetadataName(unknown.PluginInfo, "Changed plugin");
                        var snapshots = (Dictionary<string, LocalizationSnapshot>)typeof(JsoncLocalization)
                            .GetField("Snapshots", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                        snapshots[guid] = new LocalizationSnapshot { Guid = guid };
                        throw new InvalidOperationException("Injected translation failure");
                    });
                Check(unknown.Category == "General" && unknown.DispName == "Keyboard Shortcut" && unknown.Description == "Original description" &&
                    unknown.PluginInfo.Name == "Original plugin" && (string)unknown.Get() == "unchanged",
                    "plugin failure restores all display fields and plugin name without writing config values");
                Check(!JsoncLocalization.GetSnapshots().Any(s => s.Guid == guid), "failed collection removes stale workbench state");
                Check(errors.Count == 1 && errors[0].Contains(guid) && errors[0].Contains("Injected translation failure"),
                    "failure diagnostics identify plugin and suppress repeated refresh errors");
                SettingSearcherPatches.LocalizePlugin("unknown-collision.plugin", new[] { unknown, unknown2 }, originals =>
                {
                    var invalid = CollectionSnapshot(unknown, unknown2);
                    JsoncLocalization.BuildItemIndex(invalid);
                });
                Check(unknown.DispName == "Keyboard Shortcut" && unknown2.DispName == "Keyboard Shortcut",
                    "actual ambiguous collection failure returns normally with collected settings intact");
                var healthy = new UnknownSetting();
                SettingSearcherPatches.LocalizePlugin("healthy.plugin", new[] { healthy }, originals => SetSettingText(healthy, "DispName", "健康插件"));
                Check(healthy.DispName == "健康插件", "another plugin still localizes after a plugin failure");
                SettingSearcherPatches.LocalizePlugin("healthy.plugin", new[] { first, first }, originals => { });
                Check((string)first.Get() == "unchanged", "duplicate references preserve original configuration values");
            }
            finally { logField.SetValue(null, oldLog); log.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
