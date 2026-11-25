using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using ConfigurationManager;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if IL2CPP
using RuntimePlugin = BepInEx.PluginInfo;
#else
using RuntimePlugin = BepInEx.BaseUnityPlugin;
#endif

namespace ConfigurationManagerZhCn
{
    internal static class JsoncLocalization
    {
        private static readonly object Sync = new object();
        private static readonly object SaveSync = new object();
        private static readonly Dictionary<string, Dictionary<string, TranslationWorkItem>> CurrentItems = new Dictionary<string, Dictionary<string, TranslationWorkItem>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, TemporaryTranslation>> Temporary = new Dictionary<string, Dictionary<string, TemporaryTranslation>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LocalizationSnapshot> Snapshots = new Dictionary<string, LocalizationSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, LocalizationDocument> Documents = new Dictionary<string, LocalizationDocument>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> OriginalPluginNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<SettingEntryBase, SettingContext> SettingContexts = new Dictionary<SettingEntryBase, SettingContext>(SettingReferenceComparer.Instance);
        private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(1);
        private static ManualLogSource _log;
        private static LocalizationDisplayMode _displayMode = LocalizationDisplayMode.Chinese;
        private static volatile bool _manualEnabled = true;
        private static volatile bool _aiEnabled = true;
        private static volatile bool _machineEnabled = true;
        private static DateTime _nextReloadCheckUtc;

        internal static LocalizationDisplayMode CurrentDisplayMode
        {
            get { lock (Sync) return _displayMode; }
        }

        internal static void Initialize(ManualLogSource log, LocalizationDisplayMode displayMode,
            bool manualEnabled, bool aiEnabled, bool machineEnabled)
        {
            _log = log;
            _displayMode = displayMode;
            SetEnabledLayers(manualEnabled, aiEnabled, machineEnabled);
            MigrateAutomaticFiles();
        }

        internal static void SetEnabledLayers(bool manualEnabled, bool aiEnabled, bool machineEnabled)
        {
            _manualEnabled = manualEnabled;
            _aiEnabled = aiEnabled;
            _machineEnabled = machineEnabled;
        }

        internal static void BeginCollection()
        {
            lock (Sync)
                SettingContexts.Clear();
        }

        internal static void SetDisplayMode(LocalizationDisplayMode displayMode)
        {
            lock (Sync)
                _displayMode = displayMode;
        }

        internal static void LogError(string message)
        {
            if (_log != null)
                _log.LogError(message);
        }

        internal static void RegisterAndApply(RuntimePlugin plugin, IList<SettingEntryBase> settings)
        {
            var metadata = PluginRuntime.GetMetadata(plugin);
            if (metadata == null || settings == null)
                return;

            var guid = metadata.GUID;
            if (string.IsNullOrWhiteSpace(guid))
                return;

            var snapshot = BuildSnapshot(guid, metadata.Name, settings);
            snapshot.PluginVersion = metadata.Version.ToString();
            lock (Sync)
            {
                LocalizationSnapshot previous;
                if (Snapshots.TryGetValue(guid, out previous))
                    foreach (var control in previous.CustomControls)
                        if (snapshot.CustomControls.Count < MaxCustomControlsPerPlugin &&
                            snapshot.CustomControls.Count(item => item.SettingKey == control.SettingKey) < MaxCustomControlsPerSetting &&
                            snapshot.Settings.Any(setting => setting.StorageKey == control.SettingKey) &&
                            !snapshot.CustomControls.Any(existing => existing.SettingKey == control.SettingKey &&
                            existing.OriginalText == control.OriginalText)) snapshot.CustomControls.Add(control);
                // Include carried-over controls before publishing the canonical index.
                var currentItems = BuildItemIndex(snapshot);
                Snapshots[guid] = snapshot;
                CurrentItems[guid] = currentItems;
                for (var index = 0; index < settings.Count && index < snapshot.Settings.Count; index++)
                    SettingContexts[settings[index]] = new SettingContext { Guid = guid, Setting = snapshot.Settings[index] };
            }

            var document = GetDocument(guid);
            TrySetPluginName(plugin, FormatPluginTitle(snapshot.PluginName, ResolvePluginName(document, snapshot.PluginName)));

            for (var index = 0; index < settings.Count && index < snapshot.Settings.Count; index++)
            {
                var setting = settings[index];
                var source = snapshot.Settings[index];
                UpdateSettingProperty(setting, "Category", FormatDisplay(source.Category,
                    ResolveSetting(document, source, "Category", source.Category), false));
                UpdateSettingProperty(setting, "DispName", FormatDisplay(source.DispName,
                    ResolveSetting(document, source, "DispName", source.DispName), false));
                UpdateSettingProperty(setting, "Description", FormatDisplay(source.Description,
                    ResolveSetting(document, source, "Description", source.Description), true));
            }
        }

        internal static Dictionary<string, TranslationWorkItem> BuildItemIndex(LocalizationSnapshot snapshot)
        {
            return TranslationWorkItem.Index(snapshot.CreateWorkItems());
        }

        internal static void DiscardPluginCollection(string guid)
        {
            lock (Sync)
            {
                Snapshots.Remove(guid);
                CurrentItems.Remove(guid);
                foreach (var setting in SettingContexts.Where(p => p.Value.Guid == guid).Select(p => p.Key).ToArray())
                    SettingContexts.Remove(setting);
            }
        }

        internal static IList<LocalizationSnapshot> GetSnapshots()
        {
            lock (Sync)
                return Snapshots.Values.OrderBy(s => s.PluginName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static TranslationWorkItem GetPluginWorkItem(string guid)
        {
            lock (Sync)
            {
                LocalizationSnapshot snapshot;
                return Snapshots.TryGetValue(guid, out snapshot) ? TranslationWorkItem.ForPlugin(guid, snapshot.PluginName) : null;
            }
        }

        internal static SettingSnapshot GetOriginalSetting(SettingEntryBase setting)
        {
            if (setting == null) return null;
            lock (Sync)
            {
                SettingContext context;
                return SettingContexts.TryGetValue(setting, out context) ? context.Setting : null;
            }
        }

        internal static TranslationWorkItem GetSettingWorkItem(SettingEntryBase setting, string field)
        {
            if (setting == null)
                return null;
            lock (Sync)
            {
                SettingContext context;
                if (!SettingContexts.TryGetValue(setting, out context))
                    return null;
                var source = field == "Category" ? context.Setting.Category :
                    field == "Description" ? context.Setting.Description : context.Setting.DispName;
                return TranslationWorkItem.ForSetting(context.Guid, context.Setting.StorageKey, field, source,
                    FieldContext(field) + "；设置键：" + context.Setting.StorageKey);
            }
        }

        internal static TranslationWorkItem FindCategoryWorkItem(string guid, string displayedText)
        {
            lock (Sync)
            {
                LocalizationSnapshot snapshot;
                if (!Snapshots.TryGetValue(guid, out snapshot))
                    return null;
                foreach (var setting in snapshot.Settings.Where(s => !string.IsNullOrWhiteSpace(s.Category)).GroupBy(s => s.Category).Select(g => g.First()))
                {
                    var item = TranslationWorkItem.ForSetting(guid, setting.StorageKey, "Category", setting.Category,
                        "配置分类；设置键：" + setting.StorageKey);
                    if (string.Equals(setting.Category, displayedText, StringComparison.Ordinal) ||
                        string.Equals(GetDisplayValue(item), displayedText, StringComparison.Ordinal))
                        return item;
                }
                return snapshot.Settings.Select(s => GetSettingWorkItemBySnapshot(guid, s, "Category"))
                    .FirstOrDefault(item => item != null && !string.IsNullOrWhiteSpace(item.Source));
            }
        }

        internal static string GetCustomControlDisplay(SettingEntryBase setting, string originalText)
        {
            if (setting == null || string.IsNullOrWhiteSpace(originalText))
                return originalText;
            var item = RegisterCustomControl(setting, originalText);
            if (item == null)
                return originalText;
            return FormatDisplay(originalText, ResolveCustom(GetDocument(item.PluginGuid), item.EntryKey, originalText, originalText), false);
        }

        internal const int MaxCustomControlsPerPlugin = 256;
        internal const int MaxCustomControlsPerSetting = 64;
        internal const int MaxCustomControlCharacters = 512;

        internal static TranslationWorkItem RegisterCustomControl(SettingEntryBase setting, string originalText, bool explicitCapture = false)
        {
            lock (Sync)
            {
                SettingContext context;
                LocalizationSnapshot snapshot;
                if (!SettingContexts.TryGetValue(setting, out context) ||
                    !Snapshots.TryGetValue(context.Guid, out snapshot) || string.IsNullOrWhiteSpace(originalText) || originalText.Length > MaxCustomControlCharacters)
                    return null;
                if (!snapshot.CustomControlIndexReady)
                {
                    foreach (var control in snapshot.CustomControls)
                    {
                        HashSet<string> values;
                        if (!snapshot.CustomControlIndex.TryGetValue(control.SettingKey, out values))
                            snapshot.CustomControlIndex[control.SettingKey] = values = new HashSet<string>(StringComparer.Ordinal);
                        values.Add(control.OriginalText);
                    }
                    snapshot.CustomControlIndexReady = true;
                }
                HashSet<string> texts;
                snapshot.CustomControlIndex.TryGetValue(context.Setting.StorageKey, out texts);
                if (texts == null || !texts.Contains(originalText))
                {
                    var dynamicText = originalText.Any(char.IsDigit);
                    if ((!explicitCapture && dynamicText) || snapshot.CustomControls.Count >= MaxCustomControlsPerPlugin ||
                        texts != null && texts.Count >= MaxCustomControlsPerSetting) return null;
                    if (texts == null) snapshot.CustomControlIndex[context.Setting.StorageKey] = texts = new HashSet<string>(StringComparer.Ordinal);
                    texts.Add(originalText);
                    snapshot.CustomControls.Add(new CustomControlSnapshot { SettingKey = context.Setting.StorageKey,
                        OriginalText = originalText, ExcludeFromBatch = dynamicText });
                }
                var item = TranslationWorkItem.ForCustom(context.Guid, context.Setting.StorageKey, originalText,
                    "自定义控件；设置键：" + context.Setting.StorageKey);
                item.PluginName = snapshot.PluginName;
                item.PluginVersion = snapshot.PluginVersion;
                Dictionary<string, TranslationWorkItem> items;
                if (CurrentItems.TryGetValue(context.Guid, out items)) items[item.Id] = item;
                return item;
            }
        }

        internal static string GetDisplayValue(TranslationWorkItem item)
        {
            if (item == null)
                return string.Empty;
            var document = GetDocument(item.PluginGuid);
            string translated;
            if (item.Kind == "Plugin")
                translated = ResolvePluginName(document, item.Source);
            else if (item.Kind == "Setting")
            {
                SettingSnapshot setting;
                lock (Sync)
                {
                    LocalizationSnapshot snapshot;
                    setting = Snapshots.TryGetValue(item.PluginGuid, out snapshot)
                        ? snapshot.Settings.FirstOrDefault(value => value.StorageKey == item.EntryKey)
                        : null;
                }
                if (setting == null)
                    setting = new SettingSnapshot { StorageKey = item.EntryKey, Category = string.Empty, DispName = item.Source };
                translated = ResolveSetting(document, setting, item.Field, item.Source);
            }
            else if (item.Kind == "Enum")
                translated = ResolveEnum(document, item.EnumAlias == null ? new[] { item.EnumType } : new[] { item.EnumType, item.EnumAlias },
                    item.EnumMember, item.Source);
            else
                translated = ResolveCustom(document, item.EntryKey, item.Source, item.Source);
            return item.Kind == "Plugin"
                ? FormatPluginTitle(item.Source, translated)
                : FormatDisplay(item.Source, translated, item.Field == "Description");
        }

        internal static string GetEnumLocalizedDescription(Type enumType, string enumValueName)
        {
            if (enumType == null || string.IsNullOrWhiteSpace(enumValueName) || CurrentDisplayMode == LocalizationDisplayMode.English)
                return null;

            var typeNames = new[] { enumType.FullName, enumType.Name }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            foreach (var guid in GetRelevantDocumentGuids(typeNames))
            {
                var document = GetDocument(guid);
                var translated = ResolveEnum(document, typeNames, enumValueName, enumValueName);
                if (!string.IsNullOrWhiteSpace(translated))
                    return FormatDisplay(enumValueName, translated, false);
            }
            return null;
        }

        internal static string GetStoredValue(TranslationWorkItem item, TranslationLayer layer)
        {
            if (item == null)
                return string.Empty;
            var document = GetDocument(item.PluginGuid);
            var layerDocument = layer == TranslationLayer.AI ? document.Ai :
                layer == TranslationLayer.Machine ? document.Machine : document.Sidecar;
            var value = ReadItem(layerDocument, layer.ToString(), item);
            if (!string.IsNullOrWhiteSpace(value) && !TranslationTracking.CanDisplay(document.Sidecar, layer.ToString(), item, value))
                value = string.Empty;
            if (layer == TranslationLayer.Machine && string.IsNullOrWhiteSpace(value))
                value = ReadTemporary(item.PluginGuid, item.Id, item.Source);
            if (layer == TranslationLayer.Manual && string.IsNullOrWhiteSpace(value))
                value = ReadLegacyItem(document.Base, item);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        }

        internal static void SaveWebLayer(string pluginGuid, TranslationLayer layer, IList<TranslationWorkItem> items,
            IDictionary<string, string> expected, IDictionary<string, string> translated)
        {
            lock (SaveSync)
            {
                lock (Sync) Documents.Remove(pluginGuid);
                foreach (var item in items)
                    if (GetStoredValue(item, layer) != expected[item.Id])
                        throw new InvalidOperationException("此译文层已有其他修改，整批未应用。请重新导出并核对。");
                SaveLayer(pluginGuid, layer, items, translated);
            }
        }

        internal static void SaveLayer(string pluginGuid, TranslationLayer layer, IEnumerable<TranslationWorkItem> items,
            IDictionary<string, string> translatedValues, string profile = null)
        {
            if (string.IsNullOrWhiteSpace(pluginGuid))
                throw new ArgumentException("缺少插件 GUID。", "pluginGuid");
            var materialized = items.ToList();
            lock (SaveSync)
            {
                // A malformed file must never be silently replaced by an empty document.
                var document = LoadDocument(pluginGuid, true);
                var sidecar = document.Sidecar != null ? (JObject)document.Sidecar.DeepClone() : new JObject();
                sidecar["SchemaVersion"] = 2;
                sidecar.Remove("Machine");
                sidecar.Remove("AI");
                var current = GetCurrentItems(pluginGuid, materialized);
                TranslationTracking.EnsureBaseline(sidecar, current);
                PreserveLayerSources(sidecar, document.Base, current);
                var layerDocument = layer == TranslationLayer.AI
                    ? (document.Ai != null ? (JObject)document.Ai.DeepClone() : new JObject())
                    : layer == TranslationLayer.Machine
                        ? (document.Machine != null ? (JObject)document.Machine.DeepClone() : new JObject())
                        : sidecar;
                layerDocument["SchemaVersion"] = 2;
                var layerObject = EnsureObject(layerDocument, layer.ToString());

                foreach (var item in materialized)
                {
                    string value;
                    if (item == null || !translatedValues.TryGetValue(item.Id, out value))
                        continue;
                    if (item.PluginGuid != pluginGuid) throw new InvalidOperationException("译文与插件 GUID 不匹配。");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        if (layer == TranslationLayer.Manual) value = TranslationSafety.PreserveOuterFormatting(item.Source, value);
                        value = TranslationSafety.Validate(value, item, layer == TranslationLayer.Manual);
                    }
                    if (layer == TranslationLayer.AI)
                    {
                        if (item.Kind == "Setting" && item.QualifiedSettingKey != null && item.QualifiedSettingKey != item.EntryKey)
                            (layerObject["ConfigEntries"]?[item.QualifiedSettingKey] as JObject)?.Remove(item.Field);
                        if (item.Kind == "Enum" && item.EnumAlias != null && item.EnumAlias != item.EnumType)
                            (layerObject["Enum"]?[item.EnumAlias] as JObject)?.Remove(item.EnumMember);
                    }
                    if (string.IsNullOrWhiteSpace(value))
                        RemoveItem(layerObject, item);
                    else
                        item.WriteTo(layerObject, value.Trim());
                    var savedProfile = profile ?? (string)sidecar["Tracking"]?[layer.ToString()]?[item.Id]?["Profile"];
                    TranslationTracking.Record(sidecar, layer.ToString(), item, value, savedProfile);
                }

                // Each layer commits its source records with its text in ONE atomic file.
                // The baseline cache is advisory and may be updated/rebuilt afterwards.
                if (layer != TranslationLayer.Manual)
                {
                    layerDocument["LayerTrackingVersion"] = 1;
                    layerDocument["Tracking"] = new JObject { [layer.ToString()] = sidecar["Tracking"]?[layer.ToString()]?.DeepClone() ?? new JObject() };
                }
                if (layer == TranslationLayer.AI) SaveAutomaticDocument(pluginGuid, GetAiPath(pluginGuid), layerDocument, "api");
                if (layer == TranslationLayer.Machine) SaveAutomaticDocument(pluginGuid, GetMachinePath(pluginGuid), layerDocument, "mt");
                SaveTrackedDocument(pluginGuid, sidecar);
            }
        }

        internal static void StoreTemporary(string guid, IEnumerable<TranslationWorkItem> items, IDictionary<string, string> values)
        {
            var validated = new Dictionary<string, TemporaryTranslation>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                string value;
                if (item.PluginGuid != guid) throw new InvalidOperationException("临时译文的插件不匹配。");
                if (values.TryGetValue(item.Id, out value))
                    validated[item.Id] = new TemporaryTranslation { Source = item.Source, Context = item.Context,
                        Value = TranslationSafety.Validate(value, item) };
            }
            lock (Sync)
            {
                Dictionary<string, TemporaryTranslation> existing;
                if (!Temporary.TryGetValue(guid, out existing)) Temporary[guid] = existing = new Dictionary<string, TemporaryTranslation>(StringComparer.Ordinal);
                foreach (var pair in validated) existing[pair.Key] = pair.Value;
            }
        }

        internal static void ClearTemporary(string guid)
        {
            lock (Sync) Temporary.Remove(guid);
        }

        private static string ReadTemporary(string guid, string id, string source)
        {
            lock (Sync)
            {
                Dictionary<string, TemporaryTranslation> entries;
                TemporaryTranslation entry;
                return Temporary.TryGetValue(guid, out entries) && entries.TryGetValue(id, out entry) && entry.Source == source ? entry.Value : null;
            }
        }

        internal static IList<TranslationWorkItem> PendingAiItems(IEnumerable<TranslationWorkItem> items, string profile, bool force)
        {
            if (force) return items.ToList();
            var result = new List<TranslationWorkItem>();
            foreach (var group in items.GroupBy(item => item.PluginGuid))
            {
                var document = GetDocument(group.Key);
                result.AddRange(group.Where(item => !TranslationTracking.CanReuse(document.Sidecar, item, ReadItem(document.Ai, "AI", item), profile)));
            }
            return result;
        }

        internal static IList<TranslationChange> GetChanges(LocalizationSnapshot snapshot)
        {
            return TranslationTracking.Compare(GetDocument(snapshot.Guid).Sidecar, snapshot.CreateWorkItems());
        }

        internal static bool HasBaseline(string guid)
        {
            return GetDocument(guid).Sidecar?["Tracking"]?["Baseline"] is JObject;
        }

        internal static TranslationInventory GetInventory(LocalizationSnapshot snapshot)
        {
            lock (SaveSync) return ReadInventory(snapshot);
        }

        private static TranslationInventory ReadInventory(LocalizationSnapshot snapshot)
        {
            var inventory = new TranslationInventory();
            var items = snapshot.CreateWorkItems();
            inventory.Total = items.Count;
            try
            {
                // Read exactly what is on disk, including files changed outside the workbench.
                var document = LoadDocument(snapshot.Guid, true);
                inventory.HasFiles = document.Files.Values.Any(stamp => stamp != DateTime.MinValue);
                var legacy = document.Base;
                var sidecar = document.Sidecar;
                var aiDocument = document.Ai;
                var machineDocument = document.Machine;
                foreach (var item in items)
                {
                    var manual = ReadItem(sidecar, "Manual", item);
                    var oldManual = ReadLegacyItem(legacy, item);
                    var ai = ReadItem(aiDocument, "AI", item);
                    var machine = ReadItem(machineDocument, "Machine", item);
                    var validManual = !string.IsNullOrWhiteSpace(manual) && TranslationTracking.CanDisplay(sidecar, "Manual", item, manual);
                    var validLegacy = !string.IsNullOrWhiteSpace(oldManual) && TranslationTracking.CanDisplay(sidecar, "Legacy", item, oldManual);
                    var validAi = TranslationSafety.IsEffectiveTranslation(item, ai) && TranslationTracking.CanDisplay(sidecar, "AI", item, ai);
                    var validMachine = TranslationSafety.IsEffectiveTranslation(item, machine) && TranslationTracking.CanDisplay(sidecar, "Machine", item, machine);
                    if (!string.IsNullOrWhiteSpace(manual) || !string.IsNullOrWhiteSpace(oldManual)) inventory.StoredManualIds.Add(item.Id);
                    if (!string.IsNullOrWhiteSpace(ai)) inventory.StoredAiIds.Add(item.Id);
                    if (validManual || validLegacy) inventory.Manual++;
                    if (validAi) inventory.Ai++;
                    if (validMachine) inventory.Machine++;
                    if ((validManual || validLegacy) && validAi) inventory.Overlap++;
                    if (validManual || validLegacy || validAi || validMachine) inventory.ValidIds.Add(item.Id);
                    if ((!string.IsNullOrWhiteSpace(manual) && !validManual) ||
                        (!string.IsNullOrWhiteSpace(oldManual) && !validLegacy && !validManual) ||
                        (!string.IsNullOrWhiteSpace(ai) && !validAi) ||
                        (!string.IsNullOrWhiteSpace(machine) && !validMachine)) inventory.Stale++;
                }
                inventory.Translated = inventory.ValidIds.Count;
            }
            catch (Exception) { inventory.Error = "JSONC 无法读取，请修复文件后再批量翻译。"; }
            return inventory;
        }

        internal static void EstablishBaseline(LocalizationSnapshot snapshot)
        {
            lock (SaveSync)
            {
                var sidecar = ReadSidecar(snapshot.Guid, true) ?? new JObject();
                if (sidecar["Tracking"]?["Baseline"] != null) return;
                sidecar["SchemaVersion"] = 2;
                sidecar.Remove("Machine");
                var items = snapshot.CreateWorkItems();
                TranslationTracking.EnsureBaseline(sidecar, items);
                PreserveLayerSources(sidecar, ReadObjectStrict(GetBasePath(snapshot.Guid)), items);
                SaveTrackedDocument(snapshot.Guid, sidecar);
            }
        }

        internal static void AcknowledgeRemoved(string guid, string id)
        {
            lock (SaveSync)
            {
                var sidecar = ReadSidecar(guid, true);
                if (sidecar == null) return;
                var current = GetCurrentItems(guid, new TranslationWorkItem[0]);
                if (current.Any(item => item.Id == id)) throw new InvalidOperationException("该条目仍存在，不能标记为移除。");
                // Archive the old baseline separately; leave the actual translation available for recovery.
                var tracking = EnsureObject(sidecar, "Tracking");
                var baseline = tracking["Baseline"] as JObject;
                if (baseline?[id] != null)
                {
                    EnsureObject(tracking, "Removed")[id] = baseline[id].DeepClone();
                    baseline.Remove(id);
                }
                SaveTrackedDocument(guid, sidecar);
            }
        }

        private static IList<TranslationWorkItem> GetCurrentItems(string guid, IEnumerable<TranslationWorkItem> fallback)
        {
            lock (Sync)
            {
                Dictionary<string, TranslationWorkItem> items;
                return CurrentItems.TryGetValue(guid, out items) ? items.Values.ToList() : fallback.ToList();
            }
        }

        private static void PreserveLayerSources(JObject sidecar, JObject legacy, IEnumerable<TranslationWorkItem> items)
        {
            var tracking = EnsureObject(sidecar, "Tracking");
            foreach (var item in items)
            foreach (var layer in new[] { "Manual", "AI", "Legacy" })
            {
                var records = EnsureObject(tracking, layer);
                var value = layer == "Legacy" ? ReadLegacyItem(legacy, item) :
                    layer == "AI" ? ReadItem(GetDocument(item.PluginGuid).Ai, layer, item) : ReadItem(sidecar, layer, item);
                if (records[item.Id] == null && !string.IsNullOrWhiteSpace(value))
                    records[item.Id] = tracking["Baseline"]?[item.Id]?.DeepClone() ?? TranslationTracking.Describe(item);
            }
        }

        private static void SaveTrackedDocument(string guid, JObject sidecar)
        {
            var path = GetSidecarPath(guid);
            sidecar["UpdatedUtc"] = DateTime.UtcNow.ToString("O");
            ArchiveFile(guid, path, "translations");
            ArchiveFile(guid, GetBasePath(guid), "legacy");
            var metadata = (JObject)sidecar.DeepClone();
            metadata.Remove("LayerTrackingRequired"); // Reconstructed from actual layer files on every load.
            var manual = metadata["Manual"] as JObject;
            if (manual != null)
            {
                var manualDocument = new JObject { ["SchemaVersion"] = 2, ["Manual"] = manual.DeepClone(),
                    ["LayerTrackingVersion"] = 1,
                    ["Tracking"] = new JObject { ["Manual"] = metadata["Tracking"]?["Manual"]?.DeepClone() ?? new JObject() } };
                var previous = ReadObjectStrict(GetManualPath(guid));
                if (!JToken.DeepEquals(previous, manualDocument))
                {
                    ArchiveFile(guid, GetManualPath(guid), "manual");
                    WriteJsonAtomic(GetManualPath(guid), manualDocument);
                }
            }
            metadata.Remove("Manual");
            WriteJsonAtomic(path, metadata);
            lock (Sync) { Documents.Remove(guid); _nextReloadCheckUtc = DateTime.MinValue; }
        }

        private static void SaveAutomaticDocument(string guid, string path, JObject document, string kind)
        {
            document["UpdatedUtc"] = DateTime.UtcNow.ToString("O");
            ArchiveFile(guid, path, kind);
            WriteJsonAtomic(path, document);
            lock (Sync) { Documents.Remove(guid); _nextReloadCheckUtc = DateTime.MinValue; }
        }

        private static void ArchiveFile(string guid, string path, string kind)
        {
            if (!File.Exists(path)) return;
            var content = File.ReadAllText(path);
            var folder = GetHistoryFolder(guid);
            Directory.CreateDirectory(folder);
            var archive = Path.Combine(folder, kind + "-" + TranslationSafety.Hash(content) + ".jsonc");
            if (!File.Exists(archive)) File.WriteAllText(archive, content, new UTF8Encoding(false));
        }

        internal static string GetHistoryFolder(string guid)
        {
            return Path.Combine(GetLocalizationFolder(), "cache", "history", SafeGuid(guid));
        }

        internal static string GetTranslationPath(string guid) { return GetSidecarPath(guid); }
        internal static string GetLegacyTranslationPath(string guid) { return GetBasePath(guid); }
        internal static string GetTranslationPath(string guid, TranslationLayer layer) => GetReadableLayerPath(guid, layer);

        private static JObject ReadObjectStrict(string path)
        {
            if (!File.Exists(path)) return null;
            var value = JObject.Parse(File.ReadAllText(path));
            ValidateDocumentShape(value);
            return value;
        }

        internal static void ValidateDocumentShape(JObject root)
        {
            if (root["LayerTrackingVersion"] != null && (root["LayerTrackingVersion"].Type != JTokenType.Integer || (int)root["LayerTrackingVersion"] != 1))
                throw new InvalidDataException("不支持的译文跟踪格式。");
            foreach (var name in new[] { "Tracking", "Manual", "AI", "Machine" })
                RequireObject(root[name], name);
            var tracking = root["Tracking"] as JObject;
            if (tracking != null)
                foreach (var name in new[] { "Manual", "AI", "Machine", "Legacy", "Baseline", "Removed" })
                {
                    RequireObject(tracking[name], "Tracking/" + name);
                    var records = tracking[name] as JObject;
                    if (records != null)
                        foreach (var record in records.Properties())
                        {
                            RequireObject(record.Value, "Tracking/" + name + "/" + record.Name);
                            if (!(record.Value is JObject)) throw new InvalidDataException("原文基准记录必须是对象。");
                            foreach (var field in new[] { "Source", "Context", "PluginVersion", "TranslationHash", "Profile", "Kind" })
                                RequireString(record.Value[field], "Tracking/" + name + "/" + field);
                        }
                }
            foreach (var layer in new[] { root, root["Manual"] as JObject, root["AI"] as JObject, root["Machine"] as JObject })
            {
                if (layer == null) continue;
                foreach (var section in new[] { "PluginInfo", "Plugin", "ConfigEntries", "Setting", "Enum", "CustomControls" })
                {
                    RequireObject(layer[section], section);
                    var map = layer[section] as JObject;
                    if (map == null) continue;
                    foreach (var property in map.Properties())
                        if (section == "PluginInfo" || section == "Plugin") RequireString(property.Value, section);
                        else
                        {
                            RequireObject(property.Value, section + "/" + property.Name);
                            if (property.Value is JObject entries)
                                foreach (var value in entries.Properties()) RequireString(value.Value, section + "/" + value.Name);
                        }
                }
            }
        }

        private static void RequireObject(JToken value, string location)
        {
            if (value != null && value.Type != JTokenType.Null && !(value is JObject))
                throw new InvalidDataException("译文结构错误（需要对象）：" + location);
        }

        private static void RequireString(JToken value, string location)
        {
            if (value != null && value.Type != JTokenType.Null && value.Type != JTokenType.String)
                throw new InvalidDataException("译文结构错误（需要文本）：" + location);
        }

        internal static string FormatDisplay(string original, string translated, bool multiline)
        {
            var mode = CurrentDisplayMode;
            if (mode == LocalizationDisplayMode.English || string.IsNullOrWhiteSpace(translated) ||
                string.Equals(original, translated, StringComparison.Ordinal))
                return original;
            if (mode == LocalizationDisplayMode.Bilingual && !string.IsNullOrWhiteSpace(original))
                return translated + "\n" + original;
            return translated;
        }

        internal static string FormatPluginTitle(string original, string translated)
        {
            var source = (original ?? string.Empty).Trim();
            var localized = (translated ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(localized) || string.Equals(source, localized, StringComparison.Ordinal))
                return source;
            if (!string.IsNullOrWhiteSpace(source) &&
                (localized.StartsWith(source + " ", StringComparison.OrdinalIgnoreCase) ||
                 localized.StartsWith(source + "\n", StringComparison.OrdinalIgnoreCase)))
                return localized;
            return string.IsNullOrWhiteSpace(source) ? localized : source + " " + localized;
        }

        private static LocalizationSnapshot BuildSnapshot(string guid, string currentPluginName, IList<SettingEntryBase> settings)
        {
            string originalPluginName;
            lock (Sync)
            {
                if (!OriginalPluginNames.TryGetValue(guid, out originalPluginName))
                {
                    originalPluginName = currentPluginName;
                    OriginalPluginNames[guid] = originalPluginName;
                }
            }

            var snapshot = new LocalizationSnapshot { Guid = guid, PluginName = originalPluginName ?? guid };
            var nameCounts = settings.Where(s => s != null)
                .GroupBy(s => (s.DispName ?? string.Empty).Trim(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var legacyKeys = settings.Select(setting =>
            {
                var name = (setting.DispName ?? string.Empty).Trim();
                return nameCounts[name] > 1 ? "[" + (setting.Category ?? string.Empty).Trim() + "]" + name : name;
            }).ToArray();
            var keyCounts = legacyKeys.GroupBy(key => key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            // Reserve all display aliases as well as storage keys to avoid a generated key
            // accidentally becoming an ordinary setting's legacy lookup alias.
            var reserved = new HashSet<string>(legacyKeys, StringComparer.Ordinal);
            foreach (var setting in settings)
            {
                reserved.Add((setting.DispName ?? string.Empty).Trim());
                reserved.Add("[" + (setting.Category ?? string.Empty).Trim() + "]" + (setting.DispName ?? string.Empty).Trim());
            }
            for (var index = 0; index < settings.Count; index++)
            {
                var setting = settings[index];
                var category = setting != null ? (setting.Category ?? string.Empty).Trim() : string.Empty;
                var name = setting != null ? (setting.DispName ?? string.Empty).Trim() : string.Empty;
                var description = setting != null ? setting.Description ?? string.Empty : string.Empty;
                var storageKey = legacyKeys[index];
                var ambiguous = keyCounts[storageKey] > 1;
                if (ambiguous)
                {
                    storageKey = GetStableSettingKey(setting);
                    if (storageKey == null)
                        throw new InvalidOperationException("Cannot safely identify duplicate setting: " + legacyKeys[index]);
                    while (reserved.Contains(storageKey)) storageKey = "@" + storageKey;
                }
                snapshot.Settings.Add(new SettingSnapshot
                {
                    StorageKey = storageKey,
                    HasAmbiguousLegacyKey = ambiguous,
                    Category = category,
                    DispName = name,
                    Description = description
                });

                var type = setting != null ? setting.SettingType : null;
                if (type == null || !type.IsEnum)
                {
                    var choices = setting != null ? setting.AcceptableValues : null;
                    if (choices != null)
                        foreach (var choice in choices.Where(value => value != null)
                            .Select(value => value.ToString()).Where(value => !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetter))
                            .Distinct(StringComparer.Ordinal).Where(value => value.Length <= MaxCustomControlCharacters).Take(MaxCustomControlsPerSetting))
                        {
                            if (snapshot.CustomControls.Count >= MaxCustomControlsPerPlugin) break;
                            snapshot.CustomControls.Add(new CustomControlSnapshot { SettingKey = storageKey, OriginalText = choice });
                        }
                    continue;
                }
                var typeName = type.FullName ?? type.Name;
                foreach (var member in Enum.GetNames(type))
                {
                    if (!snapshot.EnumValues.Any(e => e.TypeName == typeName && e.MemberName == member))
                        snapshot.EnumValues.Add(new EnumValueSnapshot { TypeName = typeName, MemberName = member });
                }
            }
            return snapshot;
        }

        private static string GetStableSettingKey(SettingEntryBase setting)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var entryProperty = setting.GetType().GetProperty("Entry", flags);
            var entry = entryProperty == null ? null : entryProperty.GetValue(setting, null) as BepInEx.Configuration.ConfigEntryBase;
            if (entry != null)
                return "@config:" + EncodeIdentityPart(ConfigFileIdentity(entry.ConfigFile.ConfigFilePath)) +
                    EncodeIdentityPart(entry.Definition.Section) + EncodeIdentityPart(entry.Definition.Key);
            var propertyMember = setting.GetType().GetProperty("Property", flags);
            var property = propertyMember == null ? null : propertyMember.GetValue(setting, null) as PropertyInfo;
            if (property != null && property.DeclaringType != null)
            {
                var instanceMember = setting.GetType().GetProperty("Instance", flags);
                var instance = instanceMember == null ? null : instanceMember.GetValue(setting, null);
                var getter = property.GetGetMethod(true);
                // Multiple arbitrary instances of a type have no persistent identity.
                if (getter == null || (!getter.IsStatic && !ReferenceEquals(instance, setting.PluginInstance))) return null;
                return "@property:" + EncodeIdentityPart(property.DeclaringType.Assembly.GetName().Name) +
                    EncodeIdentityPart(property.DeclaringType.FullName) + EncodeIdentityPart(property.Name);
            }
            return null;
        }

        private static string EncodeIdentityPart(string value)
        {
            value = value ?? string.Empty;
            return value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        }

        private static string ConfigFileIdentity(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.IsNullOrEmpty(Paths.ConfigPath))
            {
                var configRoot = Path.GetFullPath(Paths.ConfigPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase))
                    return fullPath.Substring(configRoot.Length).Replace('\\', '/');
            }
            // Nonstandard files outside the config root must still remain distinct.
            return fullPath.Replace('\\', '/');
        }

        private static void UpdateSettingProperty(SettingEntryBase setting, string propertyName, string newValue)
        {
            if (setting == null || newValue == null)
                return;
            var property = typeof(SettingEntryBase).GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (property != null && property.CanWrite)
                property.SetValue(setting, newValue, null);
        }

        private static void TrySetPluginName(RuntimePlugin plugin, string newName)
        {
            TrySetPluginMetadataName(PluginRuntime.GetMetadata(plugin), newName);
        }

        internal static void TrySetPluginMetadataName(BepInPlugin metadata, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return;
            try
            {
                if (metadata == null)
                    return;
                var nameField = typeof(BepInPlugin).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);
                if (nameField != null)
                {
                    nameField.SetValue(metadata, newName);
                    return;
                }
                var nameProperty = typeof(BepInPlugin).GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                if (nameProperty != null && nameProperty.CanWrite)
                    nameProperty.SetValue(metadata, newName, null);
            }
            catch (Exception ex)
            {
                LogError("Failed to update plugin name: " + ex.Message);
            }
        }

        private static LocalizationDocument GetDocument(string guid)
        {
            ReloadChangedDocumentsIfNeeded();
            lock (Sync)
            {
                LocalizationDocument cached;
                if (Documents.TryGetValue(guid, out cached))
                    return cached;
            }

            var loaded = LoadDocument(guid);
            lock (Sync)
                Documents[guid] = loaded;
            return loaded;
        }

        private static void ReloadChangedDocumentsIfNeeded()
        {
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                if (now < _nextReloadCheckUtc)
                    return;
                _nextReloadCheckUtc = now.Add(ReloadInterval);
                var changed = Documents.Where(pair => ActivePaths(pair.Key).Any(path =>
                    !pair.Value.Files.TryGetValue(path, out var stamp) || stamp != GetLastWriteUtc(path)))
                    .Select(pair => pair.Key).ToArray();
                foreach (var guid in changed)
                    Documents.Remove(guid);
            }
        }

        private static IEnumerable<string> ActivePaths(string guid)
        {
            yield return GetBasePath(guid);
            yield return GetReadableSidecarPath(guid);
            yield return GetReadableLayerPath(guid, TranslationLayer.Manual);
            yield return GetReadableLayerPath(guid, TranslationLayer.AI);
            yield return GetReadableLayerPath(guid, TranslationLayer.Machine);
        }

        private static LocalizationDocument LoadDocument(string guid, bool strict = false)
        {
            var basePath = GetBasePath(guid);
            var sidecarPath = GetReadableSidecarPath(guid);
            var manualPath = GetReadableLayerPath(guid, TranslationLayer.Manual);
            var apiPath = GetReadableLayerPath(guid, TranslationLayer.AI);
            var machinePath = GetReadableLayerPath(guid, TranslationLayer.Machine);
            Func<string, JObject> read = strict ? (Func<string, JObject>)ReadObjectStrict : ReadObject;
            var metadata = read(sidecarPath);
            var manual = read(manualPath);
            var ai = read(apiPath);
            var machine = read(machinePath);
            return new LocalizationDocument
            {
                Guid = guid,
                Base = read(basePath),
                Sidecar = MergeSidecar(metadata, manual, ai, machine),
                Ai = ai,
                Machine = machine,
                Files = new[] { basePath, sidecarPath, manualPath, apiPath, machinePath }
                    .ToDictionary(path => path, GetLastWriteUtc, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static JObject ReadObject(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                return ReadObjectStrict(path);
            }
            catch (Exception ex)
            {
                LogError("Failed to read localization file " + path + ": " + ex.Message);
                return null;
            }
        }

        private static string ResolvePluginName(LocalizationDocument document, string source)
        {
            return FirstTranslation(source,
                _manualEnabled ? Guard(document, "Manual", "plugin-name", ReadPath(document.Sidecar, "Manual", "PluginInfo", "PluginName")) : null,
                _manualEnabled ? Guard(document, "Legacy", "plugin-name", ReadPath(document.Base, "PluginInfo", "PluginName") ?? ReadPath(document.Base, "Plugin", "Name")) : null,
                _aiEnabled ? Guard(document, "AI", "plugin-name", ReadPath(document.Ai, "AI", "PluginInfo", "PluginName")) : null,
                _machineEnabled ? Guard(document, "Machine", "plugin-name", ReadPath(document.Machine, "Machine", "PluginInfo", "PluginName")) ?? ReadTemporary(document.Guid, "plugin-name", source) : null);
        }

        private static string ResolveSetting(LocalizationDocument document, SettingSnapshot setting, string field, string source)
        {
            var keys = CandidateKeys(setting);
            var id = "setting|" + setting.StorageKey + "|" + field;
            return FirstTranslation(source,
                _manualEnabled ? Guard(document, "Manual", id, ReadSetting(document.Sidecar, "Manual", keys, field)) : null,
                _manualEnabled ? Guard(document, "Legacy", id, ReadSetting(document.Base, null, keys, field)) : null,
                _aiEnabled ? Guard(document, "AI", id, ReadSetting(document.Ai, "AI", keys, field)) : null,
                _machineEnabled ? Guard(document, "Machine", id, ReadSetting(document.Machine, "Machine", keys, field)) ?? ReadTemporary(document.Guid, id, source) : null);
        }

        private static string ResolveEnum(LocalizationDocument document, IEnumerable<string> typeNames, string member, string source)
        {
            var aliases = typeNames.ToArray();
            var id = "enum|" + aliases[0] + "|" + member;
            return FirstTranslation(source,
                _manualEnabled ? Guard(document, "Manual", id, ReadEnumAliases(document.Sidecar, "Manual", aliases, member)) : null,
                _manualEnabled ? Guard(document, "Legacy", id, ReadEnumAliases(document.Base, null, aliases, member)) : null,
                _aiEnabled ? Guard(document, "AI", id, ReadEnumAliases(document.Ai, "AI", aliases, member)) : null,
                _machineEnabled ? Guard(document, "Machine", id, ReadEnumAliases(document.Machine, "Machine", aliases, member)) ?? ReadTemporary(document.Guid, id, source) : null);
        }

        private static string ResolveCustom(LocalizationDocument document, string settingKey, string originalText, string source)
        {
            var id = "custom|" + settingKey + "|" + originalText;
            return FirstTranslation(source,
                _manualEnabled ? Guard(document, "Manual", id, ReadPath(document.Sidecar, "Manual", "CustomControls", settingKey, originalText)) : null,
                _aiEnabled ? Guard(document, "AI", id, ReadPath(document.Ai, "AI", "CustomControls", settingKey, originalText)) : null,
                _machineEnabled ? Guard(document, "Machine", id, ReadPath(document.Machine, "Machine", "CustomControls", settingKey, originalText)) ?? ReadTemporary(document.Guid, id, source) : null);
        }

        private static string Guard(LocalizationDocument document, string layer, string id, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            lock (Sync)
            {
                Dictionary<string, TranslationWorkItem> items;
                TranslationWorkItem item;
                if (CurrentItems.TryGetValue(document.Guid, out items) && items.TryGetValue(id, out item) &&
                    !TranslationTracking.CanDisplay(document.Sidecar, layer, item, value)) return null;
            }
            return value;
        }

        private static string ReadEnumAliases(JObject root, string layer, IEnumerable<string> typeNames, string member)
        {
            foreach (var typeName in typeNames)
            {
                var value = layer == null
                    ? ReadPath(root, "Enum", typeName, member)
                    : ReadPath(root, layer, "Enum", typeName, member);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static string FirstTranslation(string source, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate.Trim();
            }
            return null;
        }

        private static string[] CandidateKeys(SettingSnapshot setting)
        {
            if (setting.HasAmbiguousLegacyKey) return new[] { setting.StorageKey };
            return new[] { setting.StorageKey, "[" + setting.Category + "]" + setting.DispName, setting.DispName }
                .Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToArray();
        }

        private static string ReadSetting(JObject root, string layer, IEnumerable<string> keys, string field)
        {
            foreach (var key in keys)
            {
                var value = layer == null
                    ? ReadPath(root, "ConfigEntries", key, field) ?? ReadPath(root, "Setting", key, field)
                    : ReadPath(root, layer, "ConfigEntries", key, field);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static string ReadItem(JObject root, string layer, TranslationWorkItem item)
        {
            if (item.Kind == "Plugin")
                return ReadPath(root, layer, "PluginInfo", "PluginName");
            if (item.Kind == "Setting")
                return ReadPath(root, layer, "ConfigEntries", item.EntryKey, item.Field) ??
                    (layer == "AI" && item.QualifiedSettingKey != null
                        ? ReadPath(root, layer, "ConfigEntries", item.QualifiedSettingKey, item.Field) : null);
            if (item.Kind == "Enum")
                return ReadPath(root, layer, "Enum", item.EnumType, item.EnumMember) ??
                    (layer == "AI" && item.EnumAlias != null ? ReadPath(root, layer, "Enum", item.EnumAlias, item.EnumMember) : null);
            return ReadPath(root, layer, "CustomControls", item.EntryKey, item.Source);
        }

        private static string ReadLegacyItem(JObject root, TranslationWorkItem item)
        {
            if (item.Kind == "Plugin")
                return ReadPath(root, "PluginInfo", "PluginName") ?? ReadPath(root, "Plugin", "Name");
            if (item.Kind == "Setting")
            {
                lock (Sync)
                {
                    LocalizationSnapshot snapshot;
                    var setting = Snapshots.TryGetValue(item.PluginGuid, out snapshot)
                        ? snapshot.Settings.FirstOrDefault(value => value.StorageKey == item.EntryKey) : null;
                    return ReadSetting(root, null, setting == null ? new[] { item.EntryKey } : CandidateKeys(setting), item.Field);
                }
            }
            if (item.Kind == "Enum")
                return ReadPath(root, "Enum", item.EnumType, item.EnumMember);
            return null;
        }

        private static string ReadPath(JObject root, params string[] path)
        {
            if (root == null)
                return null;
            JToken token = root;
            foreach (var segment in path)
            {
                var obj = token as JObject;
                if (obj == null || !obj.TryGetValue(segment, StringComparison.Ordinal, out token))
                    return null;
            }
            return token.Type == JTokenType.String ? token.ToString() : null;
        }

        private static void RemoveItem(JObject layer, TranslationWorkItem item)
        {
            if (item.Kind == "Plugin")
            {
                var pluginInfo = layer["PluginInfo"] as JObject;
                if (pluginInfo != null)
                    pluginInfo.Property("PluginName")?.Remove();
                return;
            }
            if (item.Kind == "Setting")
            {
                var entry = layer["ConfigEntries"]?[item.EntryKey] as JObject;
                if (entry != null)
                    entry.Property(item.Field)?.Remove();
                return;
            }
            if (item.Kind == "Enum")
            {
                var enumType = layer["Enum"]?[item.EnumType] as JObject;
                if (enumType != null)
                    enumType.Property(item.EnumMember)?.Remove();
                return;
            }
            var controls = layer["CustomControls"]?[item.EntryKey] as JObject;
            if (controls != null)
                controls.Property(item.Source)?.Remove();
        }

        private static JObject EnsureObject(JObject parent, string propertyName)
        {
            var result = parent[propertyName] as JObject;
            if (result != null)
                return result;
            result = new JObject();
            parent[propertyName] = result;
            return result;
        }

        private static IEnumerable<string> GetRelevantDocumentGuids(IEnumerable<string> typeNames)
        {
            var names = new HashSet<string>(typeNames, StringComparer.Ordinal);
            lock (Sync)
            {
                return Snapshots.Values
                    .Where(snapshot => snapshot.EnumValues.Any(value => names.Contains(value.TypeName) || names.Contains(ShortTypeName(value.TypeName))))
                    .Select(snapshot => snapshot.Guid).ToArray();
            }
        }

        private static string ShortTypeName(string fullName)
        {
            var index = fullName != null ? fullName.LastIndexOf('.') : -1;
            return index >= 0 ? fullName.Substring(index + 1) : fullName;
        }

        private static TranslationWorkItem GetSettingWorkItemBySnapshot(string guid, SettingSnapshot setting, string field)
        {
            if (setting == null)
                return null;
            var source = field == "Category" ? setting.Category : field == "Description" ? setting.Description : setting.DispName;
            if (string.IsNullOrWhiteSpace(source))
                return null;
            return setting.WorkItem(guid, field, source, FieldContext(field));
        }

        private static string FieldContext(string field)
        {
            if (field == "Category")
                return "配置分类";
            if (field == "Description")
                return "配置项说明";
            return "配置项显示名";
        }

        private static string GetLocalizationFolder()
        {
            return Path.Combine(Paths.PluginPath, "zh-cn");
        }

        internal static void MigrateAutomaticFiles()
        {
            var folder = GetLocalizationFolder();
            if (!Directory.Exists(folder)) return;
            lock (SaveSync)
            {
                var translations = Path.Combine(folder, "translations");
                Directory.CreateDirectory(translations);
                var legacyManual = Path.Combine(translations, "Manual");
                var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in new[] { folder, legacyManual, Path.Combine(translations, "API"),
                    Path.Combine(translations, "MT"), Path.Combine(folder, "API"), Path.Combine(folder, "MT") })
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (var path in Directory.EnumerateFiles(directory, "*.jsonc"))
                    {
                        var guid = Path.GetFileNameWithoutExtension(path);
                        if (directory == folder && guid.Equals("UI", StringComparison.OrdinalIgnoreCase)) continue;
                        if (directory == folder && guid.EndsWith(".translations", StringComparison.OrdinalIgnoreCase))
                        {
                            guid = guid.Substring(0, guid.Length - ".translations".Length);
                            if (!string.IsNullOrWhiteSpace(guid) && guid != "." && guid != "..")
                                ImportLegacyFile(new[] { GetSidecarPath(guid), path }, reportConflicts: false);
                            continue;
                        }
                        if ((directory == folder || directory == legacyManual) &&
                            guid.EndsWith(".overrides", StringComparison.OrdinalIgnoreCase))
                            guid = guid.Substring(0, guid.Length - ".overrides".Length);
                        if (!string.IsNullOrWhiteSpace(guid) && guid != "." && guid != "..") guids.Add(guid);
                    }
                }
                foreach (var guid in guids)
                {
                    ImportLegacyFile(GetBasePaths(guid), true);
                    foreach (var layer in new[] { TranslationLayer.Manual, TranslationLayer.AI, TranslationLayer.Machine })
                        ImportLegacyFile(GetLayerPaths(guid, layer));
                }
                var baselines = Path.Combine(folder, "cache", "baselines");
                if (Directory.Exists(baselines))
                    foreach (var path in Directory.EnumerateFiles(baselines, "*.translations.jsonc"))
                        SplitEmbeddedTranslations(path);
                MoveStorageTree(Path.Combine(folder, ".history"), Path.Combine(folder, "cache", "history"));
                MoveStorageTree(Path.Combine(folder, "diagnostics"), Path.Combine(folder, "cache", "diagnostics"));
                foreach (var path in Directory.GetFiles(folder, "*.bak", SearchOption.AllDirectories))
                    if (!path.StartsWith(Path.Combine(folder, "cache") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        MoveStorageFile(path, BackupPath(path));
                lock (Sync) { Documents.Clear(); _nextReloadCheckUtc = DateTime.MinValue; }
            }
        }

        private static void ImportLegacyFile(string[] paths, bool manualBase = false, bool reportConflicts = true)
        {
            var destination = paths[0];
            var oldFiles = paths.Skip(1).Where(File.Exists).ToArray();
            if (oldFiles.Length == 0) return;
            var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (!File.Exists(destination))
                {
                    var source = oldFiles[0];
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.WriteAllBytes(temporary, File.ReadAllBytes(source));
                    var document = ReadObjectStrict(temporary);
                    if (manualBase && Path.GetDirectoryName(source) == GetLocalizationFolder() &&
                        !new[] { "PluginInfo", "Plugin", "ConfigEntries", "Setting", "Enum", "CustomControls" }
                            .Any(section => document[section] is JObject))
                        return;
                    // A file installed while migration runs still wins the commit.
                    if (!File.Exists(destination)) File.Move(temporary, destination);
                }
                if (!reportConflicts || _log == null) return;
                var active = File.ReadAllBytes(destination);
                foreach (var old in oldFiles)
                    if (!active.SequenceEqual(File.ReadAllBytes(old)))
                        _log.LogWarning("译文迁移保留不同的文件，以新路径为准：" + destination + "。旧文件：" + old);
            }
            catch (Exception ex)
            {
                LogError("译文迁移未完成，已保留原文件：" + oldFiles[0] + "。" + ex.Message);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void SplitEmbeddedTranslations(string path)
        {
            try
            {
                var sidecar = ReadObjectStrict(path);
                var name = Path.GetFileName(path);
                var guid = name.Substring(0, name.Length - ".translations.jsonc".Length);
                var changed = false;
                foreach (var layer in new[] { TranslationLayer.AI, TranslationLayer.Machine, TranslationLayer.Manual })
                {
                    var key = layer.ToString();
                    if (!(sidecar[key] is JObject values)) continue;
                    var paths = GetLayerPaths(guid, layer);
                    var existing = ReadObjectStrict(GetExistingOrPreferredPath(paths));
                    if (existing == null)
                        WriteJsonAtomic(paths[0], new JObject { ["SchemaVersion"] = 2, [key] = values.DeepClone() });
                    else if (!JToken.DeepEquals(values, existing[key]))
                        _log?.LogWarning("已保留独立译文文件。旧的内嵌译文将归档：" + paths[0]);
                    sidecar.Remove(key);
                    changed = true;
                }
                if (!changed) return;
                ArchiveFile(guid, path, "migration-baseline");
                WriteJsonAtomic(path, sidecar);
            }
            catch (Exception ex) { LogError("旧版内嵌译文迁移未完成：" + path + "。" + ex.Message); }
        }

        private static void MoveStorageTree(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                MoveStorageFile(file, Path.Combine(destination, file.Substring(source.Length + 1)));
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length).Concat(new[] { source }))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }

        private static void MoveStorageFile(string source, string destination)
        {
            var root = Path.GetFullPath(GetLocalizationFolder()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(source).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(destination).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Migration path escapes zh-cn.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination))
            {
                // Keep both versions; never silently replace a newer layout file.
                var archive = Path.Combine(GetLocalizationFolder(), "cache", "migration", Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                File.Move(source, archive);
                LogError("Migration kept existing file " + destination + "; old file archived at " + archive);
            }
            else File.Move(source, destination);
        }

        private static string GetManualPath(string guid) => GetLayerPaths(guid, TranslationLayer.Manual)[0];

        private static JObject ReadSidecar(string guid, bool strict)
        {
            Func<string, JObject> read = strict ? (Func<string, JObject>)ReadObjectStrict : ReadObject;
            return MergeSidecar(read(GetReadableSidecarPath(guid)), read(GetReadableLayerPath(guid, TranslationLayer.Manual)),
                read(GetReadableLayerPath(guid, TranslationLayer.AI)), read(GetReadableLayerPath(guid, TranslationLayer.Machine)));
        }

        private static JObject MergeSidecar(JObject metadata, JObject manual, JObject ai, JObject machine)
        {
            metadata?.Remove("LayerTrackingRequired");
            if (manual?["Manual"] != null)
            {
                metadata = metadata ?? new JObject();
                metadata["Manual"] = manual["Manual"].DeepClone();
            }
            foreach (var layer in new[] { "Manual", "AI", "Machine" })
            {
                var document = layer == "Manual" ? manual : layer == "AI" ? ai : machine;
                if ((int?)document?["LayerTrackingVersion"] != 1) continue;
                metadata = metadata ?? new JObject();
                var required = metadata["LayerTrackingRequired"] as JArray;
                if (required == null) metadata["LayerTrackingRequired"] = required = new JArray();
                required.Add(layer);
                EnsureObject(metadata, "Tracking")[layer] = document["Tracking"]?[layer]?.DeepClone() ?? new JObject();
            }
            return metadata;
        }

        private static string BackupPath(string path)
        {
            var root = Path.GetFullPath(GetLocalizationFolder()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Backup path escapes zh-cn.");
            return Path.Combine(root, "cache", "backups", full.Substring(root.Length));
        }

        private static string GetBasePath(string guid)
        {
            return GetExistingOrPreferredPath(GetBasePaths(guid));
        }

        private static string[] GetBasePaths(string guid)
        {
            var folder = GetLocalizationFolder();
            var translations = Path.Combine(folder, "translations");
            var name = SafeGuid(guid);
            return new[] { Path.Combine(translations, name + "_Manual.jsonc"),
                Path.Combine(translations, "Manual", name + ".jsonc"), Path.Combine(folder, name + ".jsonc") };
        }

        private static string GetSidecarPath(string guid)
        {
            return Path.Combine(GetLocalizationFolder(), "cache", "baselines", SafeGuid(guid) + ".translations.jsonc");
        }

        private static string GetReadableSidecarPath(string guid)
        {
            return GetExistingOrPreferredPath(new[] { GetSidecarPath(guid),
                Path.Combine(GetLocalizationFolder(), SafeGuid(guid) + ".translations.jsonc") });
        }

        private static string GetAiPath(string guid) => GetLayerPaths(guid, TranslationLayer.AI)[0];
        private static string GetMachinePath(string guid) => GetLayerPaths(guid, TranslationLayer.Machine)[0];

        private static string GetReadableLayerPath(string guid, TranslationLayer layer) =>
            GetExistingOrPreferredPath(GetLayerPaths(guid, layer));

        private static string[] GetLayerPaths(string guid, TranslationLayer layer)
        {
            var folder = GetLocalizationFolder();
            var translations = Path.Combine(folder, "translations");
            var name = SafeGuid(guid);
            var marker = layer == TranslationLayer.Manual ? "Manual" : layer == TranslationLayer.AI ? "API" : "MT";
            var suffix = layer == TranslationLayer.Manual ? ".overrides.jsonc" : ".jsonc";
            var oldName = name + suffix;
            return new[] { Path.Combine(translations, name + "_" + marker + suffix),
                Path.Combine(translations, marker, oldName),
                layer == TranslationLayer.Manual ? Path.Combine(folder, oldName) : Path.Combine(folder, marker, oldName) };
        }

        private static string GetExistingOrPreferredPath(string[] paths) => paths.FirstOrDefault(File.Exists) ?? paths[0];

        private static string SafeGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid) || guid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || guid == "." || guid == "..")
                throw new InvalidOperationException("插件 GUID 不能用于译文文件名。");
            return guid;
        }

        private static DateTime GetLastWriteUtc(string path)
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        private static void WriteJsonAtomic(string path, JObject value)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("无法确定译文目录。");
            Directory.CreateDirectory(directory);
            var tempPath = path + ".tmp";
            var backupPath = BackupPath(path + ".bak");
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
            File.WriteAllText(tempPath, value.ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(tempPath, path, backupPath, true);
            else
                File.Move(tempPath, path);
        }

        private sealed class LocalizationDocument
        {
            internal string Guid;
            internal JObject Base;
            internal JObject Sidecar;
            internal JObject Ai;
            internal JObject Machine;
            internal Dictionary<string, DateTime> Files;
        }

        private sealed class TemporaryTranslation
        {
            internal string Source, Context, Value;
        }

        private sealed class SettingContext
        {
            internal string Guid;
            internal SettingSnapshot Setting;
        }
    }
}
