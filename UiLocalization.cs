using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal static class UiLocalization
    {
        private const string UiJsonPath = "BepInEx/plugins/zh-cn/ui.json";
        private static readonly object CacheLock = new object();
        private static Dictionary<string, string> _map = new Dictionary<string, string>();
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

            EnsureLoaded();

            string localized;
            lock (CacheLock)
            {
                if (_map.TryGetValue(original, out localized))
                    return localized;
            }

            return original;
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

            if (!File.Exists(UiJsonPath))
                return;

            var lastWrite = File.GetLastWriteTimeUtc(UiJsonPath);
            lock (CacheLock)
            {
                if (_lastWriteUtc == lastWrite && _map.Count > 0)
                    return;
            }

            try
            {
                var raw = File.ReadAllText(UiJsonPath);
                var cleaned = RemoveJsonComments(raw);
                var obj = JObject.Parse(cleaned);
                var dict = obj.Properties()
                    .Where(p => p.Name != null)
                    .ToDictionary(p => p.Name, p => p.Value != null ? p.Value.ToString() : string.Empty);

                lock (CacheLock)
                {
                    _map = dict;
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
    }
}
