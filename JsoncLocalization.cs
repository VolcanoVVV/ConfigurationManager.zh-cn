using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using ConfigurationManager;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal static class JsoncLocalization
    {
        private const string LocalizationFolder = "BepInEx/plugins/zh-cn";
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, PluginLocalization> PluginCache = new Dictionary<string, PluginLocalization>();
        private static readonly Dictionary<string, Dictionary<string, string>> EnumLocalization = new Dictionary<string, Dictionary<string, string>>();
        private static ManualLogSource _log;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void LogError(string message)
        {
            if (_log != null)
                _log.LogError(message);
        }

        internal static void ApplyPluginName(BaseUnityPlugin plugin)
        {
            if (plugin == null) return;

            var data = GetPluginLocalization(plugin.Info.Metadata.GUID);
            if (data == null || string.IsNullOrEmpty(data.PluginName)) return;

            TrySetPluginName(plugin, data.PluginName);
        }

        internal static void ApplyLocalization(BaseUnityPlugin plugin, IEnumerable<SettingEntryBase> settings)
        {
            if (plugin == null || settings == null) return;

            var data = GetPluginLocalization(plugin.Info.Metadata.GUID);
            if (data == null) return;

            if (!string.IsNullOrEmpty(data.PluginName))
                TrySetPluginName(plugin, data.PluginName);

            if (data.ConfigEntries == null || data.ConfigEntries.Count == 0)
                return;

            foreach (var setting in settings)
            {
                if (setting == null) continue;

                var category = setting.Category != null ? setting.Category.Trim() : null;
                var settingKey = setting.DispName != null ? setting.DispName.Trim() : null;

                if (string.IsNullOrEmpty(settingKey))
                    continue;

                var categorizedKey = category != null ? "[" + category + "]" + settingKey : settingKey;
                ConfigEntryLocalization entry;
                var matched = false;

                if (data.ConfigEntries.TryGetValue(categorizedKey, out entry))
                {
                    if (string.Equals(entry.Category, category, StringComparison.Ordinal))
                    {
                        ApplySettingEntry(setting, entry);
                        matched = true;
                    }
                }

                if (!matched && data.ConfigEntries.TryGetValue(settingKey, out entry))
                {
                    ApplySettingEntry(setting, entry);
                }
            }
        }

        internal static string GetEnumLocalizedDescription(Type enumType, string enumValueName)
        {
            if (enumType == null || string.IsNullOrEmpty(enumValueName))
                return null;

            Dictionary<string, string> localized;
            lock (CacheLock)
            {
                EnumLocalization.TryGetValue(enumType.FullName ?? string.Empty, out localized);
                if (localized == null)
                    EnumLocalization.TryGetValue(enumType.Name, out localized);
            }

            if (localized == null)
                return null;

            string description;
            return localized.TryGetValue(enumValueName, out description) ? description : null;
        }

        private static void ApplySettingEntry(SettingEntryBase setting, ConfigEntryLocalization entry)
        {
            if (entry == null) return;

            UpdateSettingProperty(setting, "DispName", entry.DispName);
            UpdateSettingProperty(setting, "Description", entry.Description);
            UpdateSettingProperty(setting, "Category", entry.Category);
        }

        private static void UpdateSettingProperty(SettingEntryBase setting, string propertyName, string newValue)
        {
            if (string.IsNullOrEmpty(newValue)) return;

            var property = typeof(SettingEntryBase).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (property != null && property.CanWrite)
                property.SetValue(setting, newValue, null);
        }

        private static void TrySetPluginName(BaseUnityPlugin plugin, string newName)
        {
            try
            {
                var nameField = typeof(BepInPlugin).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);
                if (nameField != null)
                {
                    nameField.SetValue(plugin.Info.Metadata, newName);
                    return;
                }

                var nameProp = typeof(BepInPlugin).GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                if (nameProp != null && nameProp.CanWrite)
                    nameProp.SetValue(plugin.Info.Metadata, newName, null);
            }
            catch (Exception ex)
            {
                LogError("Failed to update plugin name: " + ex.Message);
            }
        }

        private static PluginLocalization GetPluginLocalization(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;

            var path = Path.Combine(LocalizationFolder, guid + ".jsonc");
            if (!File.Exists(path))
            {
                lock (CacheLock)
                {
                    PluginLocalization old;
                    if (PluginCache.TryGetValue(guid, out old))
                        RemoveEnumEntries(old);

                    PluginCache.Remove(guid);
                }
                return null;
            }

            var lastWrite = File.GetLastWriteTimeUtc(path);
            lock (CacheLock)
            {
                PluginLocalization cached;
                if (PluginCache.TryGetValue(guid, out cached) && cached.LastWriteUtc == lastWrite)
                    return cached;
            }

            try
            {
                var rawContent = File.ReadAllText(path);
                var cleaned = RemoveJsonComments(rawContent);
                var json = JObject.Parse(cleaned);

                var data = new PluginLocalization
                {
                    PluginName = ReadPluginName(json),
                    ConfigEntries = ReadConfigEntries(json),
                    LastWriteUtc = lastWrite
                };

                data.EnumTypeNames = ReadEnumLocalization(json);

                lock (CacheLock)
                {
                    PluginLocalization old;
                    if (PluginCache.TryGetValue(guid, out old))
                        RemoveEnumEntries(old);

                    PluginCache[guid] = data;
                }

                return data;
            }
            catch (Exception ex)
            {
                LogError("Failed to read localization file " + path + ": " + ex.Message);
                return null;
            }
        }

        private static string ReadPluginName(JObject json)
        {
            var name = json["PluginInfo"]?["PluginName"]?.ToString();
            if (!string.IsNullOrEmpty(name))
                return name.Trim();

            name = json["Plugin"]?["Name"]?.ToString();
            return string.IsNullOrEmpty(name) ? null : name.Trim();
        }

        private static Dictionary<string, ConfigEntryLocalization> ReadConfigEntries(JObject json)
        {
            var settingsJson = json["ConfigEntries"] as JObject ?? json["Setting"] as JObject;
            if (settingsJson == null)
                return new Dictionary<string, ConfigEntryLocalization>();

            var result = new Dictionary<string, ConfigEntryLocalization>();
            foreach (var prop in settingsJson.Properties())
            {
                var valueObj = prop.Value as JObject;
                if (valueObj == null)
                    continue;

                var entry = new ConfigEntryLocalization
                {
                    DispName = valueObj["DispName"] != null ? valueObj["DispName"].ToString().Trim() : null,
                    Description = valueObj["Description"] != null ? valueObj["Description"].ToString().Trim() : null,
                    Category = valueObj["Category"] != null ? valueObj["Category"].ToString().Trim() : null
                };

                var key = prop.Name != null ? prop.Name.Trim() : null;
                if (!string.IsNullOrEmpty(key))
                    result[key] = entry;
            }

            return result;
        }

        private static List<string> ReadEnumLocalization(JObject json)
        {
            var enumJson = json["Enum"] as JObject;
            if (enumJson == null)
                return new List<string>();

            var enumKeys = new List<string>();
            foreach (var enumEntry in enumJson)
            {
                var enumTypeName = enumEntry.Key != null ? enumEntry.Key.Trim() : null;
                var enumValues = enumEntry.Value as JObject;
                if (string.IsNullOrEmpty(enumTypeName) || enumValues == null)
                    continue;

                var descriptions = enumValues.Properties()
                    .ToDictionary(p => p.Name.Trim(), p => p.Value.ToString().Trim());

                lock (CacheLock)
                {
                    if (!EnumLocalization.ContainsKey(enumTypeName))
                        EnumLocalization[enumTypeName] = descriptions;
                    else
                        LogError("Enum localization key conflict: " + enumTypeName);

                    if (enumTypeName.IndexOf('.') >= 0)
                    {
                        var shortName = enumTypeName.Substring(enumTypeName.LastIndexOf('.') + 1);
                        if (!EnumLocalization.ContainsKey(shortName))
                            EnumLocalization[shortName] = descriptions;
                    }
                }

                enumKeys.Add(enumTypeName);
            }

            return enumKeys;
        }

        private static void RemoveEnumEntries(PluginLocalization old)
        {
            if (old == null || old.EnumTypeNames == null)
                return;

            foreach (var name in old.EnumTypeNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                lock (CacheLock)
                {
                    EnumLocalization.Remove(name);
                }
            }
        }

        private static string RemoveJsonComments(string json)
        {
            var lines = json.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            return string.Join(Environment.NewLine, lines.Where(line => !line.TrimStart().StartsWith("//")).ToArray());
        }

        private sealed class PluginLocalization
        {
            public string PluginName;
            public Dictionary<string, ConfigEntryLocalization> ConfigEntries;
            public DateTime LastWriteUtc;
            public List<string> EnumTypeNames;
        }

        private sealed class ConfigEntryLocalization
        {
            public string DispName;
            public string Description;
            public string Category;
        }
    }
}
