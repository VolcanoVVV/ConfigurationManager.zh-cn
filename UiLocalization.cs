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
    internal static class UiLocalization
    {
        private static readonly object CacheLock = new object();
        private static Dictionary<string, string> _map = new Dictionary<string, string>();
        private static Dictionary<string, string> _betterMenu = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, Dictionary<string, UiSettingTranslation>> _settings =
            new Dictionary<string, Dictionary<string, UiSettingTranslation>>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastWriteUtc;
        private static DateTime _nextCheckUtc;
        private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(1);
        private static ManualLogSource _log;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static string Get(string original)
        {
            if (string.IsNullOrEmpty(original))
                return original;

            if (JsoncLocalization.CurrentDisplayMode == LocalizationDisplayMode.English)
                return original;

            EnsureLoaded();

            string localized;
            lock (CacheLock)
            {
                if (_map.TryGetValue(original, out localized))
                    return JsoncLocalization.FormatDisplay(original, localized, false);
            }

            return original;
        }

        internal static bool HasBetterMenuText(string original)
        {
            EnsureLoaded();
            lock (CacheLock) return _betterMenu.ContainsKey(original);
        }

        internal static string GetBetterMenuText(string original)
        {
            if (string.IsNullOrEmpty(original) || JsoncLocalization.CurrentDisplayMode == LocalizationDisplayMode.English)
                return original;
            EnsureLoaded();
            lock (CacheLock)
            {
                string localized;
                return _betterMenu.TryGetValue(original, out localized) ? localized : original;
            }
        }

        internal static string GetBetterEnumText(Type type, string original)
        {
            if (type.FullName == "BetterBepInEx.MenuTheme" || type.FullName == "BetterBepInEx.MenuFont" ||
                type.FullName == "BetterBepInEx.ModSort")
                return GetBetterMenuText(original);
            return original;
        }

        internal static void ApplySetting(string pluginGuid, SettingEntryBase setting,
            string originalCategory, string originalName, string originalDescription)
        {
            if (setting == null || string.IsNullOrWhiteSpace(pluginGuid) ||
                JsoncLocalization.CurrentDisplayMode == LocalizationDisplayMode.English)
                return;
            EnsureLoaded();
            UiSettingTranslation translation;
            lock (CacheLock)
            {
                Dictionary<string, UiSettingTranslation> plugin;
                if (!_settings.TryGetValue(pluginGuid, out plugin) ||
                    !plugin.TryGetValue(SettingKey(originalCategory, originalName), out translation))
                    return;
            }
            // A JSONC layer is more specific and keeps priority over UI.json defaults.
            if (string.Equals(setting.Category, originalCategory, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(translation.Category))
                SetProperty(setting, "Category", JsoncLocalization.FormatDisplay(originalCategory, translation.Category, false));
            if (string.Equals(setting.DispName, originalName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(translation.DispName))
                SetProperty(setting, "DispName", JsoncLocalization.FormatDisplay(originalName, translation.DispName, false));
            if (string.Equals(setting.Description, originalDescription, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(translation.Description))
                SetProperty(setting, "Description", JsoncLocalization.FormatDisplay(originalDescription, translation.Description, true));
        }

        internal static string GetSettingValue(string pluginGuid, string category, string name, string field)
        {
            EnsureLoaded();
            lock (CacheLock)
            {
                Dictionary<string, UiSettingTranslation> plugin;
                UiSettingTranslation translation;
                if (!_settings.TryGetValue(pluginGuid, out plugin) ||
                    !plugin.TryGetValue(SettingKey(category, name), out translation)) return null;
                return field == "Category" ? translation.Category : field == "Description" ? translation.Description : translation.DispName;
            }
        }

        private static void EnsureLoaded()
        {
            var now = DateTime.UtcNow;
            lock (CacheLock)
            {
                if (now < _nextCheckUtc)
                    return;
                _nextCheckUtc = now.Add(ReloadInterval);
            }

            var uiJsonPath = Path.Combine(Paths.PluginPath, "zh-cn", "UI.json");
            if (!File.Exists(uiJsonPath))
                return;

            var lastWrite = File.GetLastWriteTimeUtc(uiJsonPath);
            lock (CacheLock)
            {
                if (_lastWriteUtc == lastWrite && _map.Count > 0)
                    return;
            }

            try
            {
                var raw = File.ReadAllText(uiJsonPath);
                var cleaned = RemoveJsonComments(raw);
                var obj = JObject.Parse(cleaned);
                var dict = obj.Properties()
                    .Where(p => p.Value.Type == JTokenType.String)
                    .ToDictionary(p => p.Name, p => p.Value != null ? p.Value.ToString() : string.Empty);
                var settings = ParseSettings(obj["$Settings"] as JObject);
                var betterMenu = (obj["$BetterBepInEx"] as JObject ?? new JObject()).Properties()
                    .Where(p => p.Value.Type == JTokenType.String)
                    .ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);

                lock (CacheLock)
                {
                    _map = dict;
                    _settings = settings;
                    _betterMenu = betterMenu;
                    _lastWriteUtc = lastWrite;
                }
            }
            catch (Exception ex)
            {
                if (_log != null)
                    _log.LogError("Failed to read ui.json: " + ex.Message);
            }
        }

        private static string RemoveJsonComments(string json)
        {
            var lines = json.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            return string.Join(Environment.NewLine, lines.Where(line => !line.TrimStart().StartsWith("//")).ToArray());
        }

        private static Dictionary<string, Dictionary<string, UiSettingTranslation>> ParseSettings(JObject root)
        {
            var result = new Dictionary<string, Dictionary<string, UiSettingTranslation>>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return result;
            foreach (var pluginProperty in root.Properties())
            {
                var rows = pluginProperty.Value as JObject;
                if (rows == null) continue;
                var plugin = new Dictionary<string, UiSettingTranslation>(StringComparer.Ordinal);
                foreach (var row in rows.Properties())
                {
                    var value = row.Value as JObject;
                    if (value == null) continue;
                    plugin[row.Name] = new UiSettingTranslation
                    {
                        Category = (string)value["Category"],
                        DispName = (string)value["DispName"],
                        Description = (string)value["Description"]
                    };
                }
                result[pluginProperty.Name] = plugin;
            }
            return result;
        }

        private static string SettingKey(string category, string name)
        {
            return "[" + (category ?? string.Empty).Trim() + "]" + (name ?? string.Empty).Trim();
        }

        private static void SetProperty(SettingEntryBase setting, string name, string value)
        {
            var property = typeof(SettingEntryBase).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetSetMethod(true) != null) property.SetValue(setting, value, null);
        }

        private sealed class UiSettingTranslation
        {
            internal string Category, DispName, Description;
        }
    }
}
