using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal sealed class TranslationChange
    {
        internal string Id, State, OldSource, NewSource, Context, OldVersion;
        internal TranslationWorkItem Current;
    }

    internal static class TranslationTracking
    {
        internal static JObject Object(JObject parent, string key)
        {
            var result = parent[key] as JObject;
            if (result == null) parent[key] = result = new JObject();
            return result;
        }

        internal static JObject Describe(TranslationWorkItem item)
        {
            return new JObject
            {
                ["Source"] = item.Source, ["Context"] = item.Context, ["Kind"] = item.Kind,
                ["EntryKey"] = item.EntryKey, ["Field"] = item.Field, ["EnumType"] = item.EnumType,
                ["EnumMember"] = item.EnumMember, ["PluginVersion"] = item.PluginVersion,
                ["RecordedUtc"] = DateTime.UtcNow.ToString("O")
            };
        }

        internal static bool Matches(JToken record, TranslationWorkItem item)
        {
            return record != null && string.Equals((string)record["Source"], item.Source, StringComparison.Ordinal) &&
                string.Equals((string)record["Context"], item.Context, StringComparison.Ordinal);
        }

        internal static bool CanDisplay(JObject sidecar, string layer, TranslationWorkItem item, string value = null)
        {
            var tracking = sidecar?["Tracking"];
            var record = tracking?[layer]?[item.Id];
            if ((sidecar?["LayerTrackingRequired"] as JArray)?.Values<string>().Contains(layer) == true && record == null) return false;
            record = record ?? tracking?["Baseline"]?[item.Id] ?? tracking?["Removed"]?[item.Id];
            // Legacy translations have no historical source. Do not invent an old baseline.
            if (record == null) return true;
            if (!Matches(record, item)) return false;
            var hash = (string)record["TranslationHash"];
            return value == null || string.IsNullOrEmpty(hash) || hash == TranslationSafety.Hash(value.Trim());
        }

        internal static void EnsureBaseline(JObject sidecar, IEnumerable<TranslationWorkItem> current)
        {
            var tracking = Object(sidecar, "Tracking");
            tracking["SchemaVersion"] = 1;
            if (tracking["Baseline"] is JObject) return;
            var baseline = Object(tracking, "Baseline");
            foreach (var item in current) baseline[item.Id] = Describe(item);
        }

        internal static void Record(JObject sidecar, string layer, TranslationWorkItem item, string value, string profile)
        {
            var tracking = Object(sidecar, "Tracking");
            var records = Object(tracking, layer);
            if (string.IsNullOrWhiteSpace(value)) { records.Remove(item.Id); return; }
            var record = Describe(item);
            record["Profile"] = profile;
            record["TranslationHash"] = TranslationSafety.Hash(value.Trim());
            records[item.Id] = record;
            // Each layer retains its own source, so repairing AI never approves stale manual text.
            Object(tracking, "Baseline")[item.Id] = Describe(item);
        }

        internal static bool CanReuse(JObject sidecar, TranslationWorkItem item, string value, string profile)
        {
            var record = sidecar?["Tracking"]?["AI"]?[item.Id];
            return TranslationSafety.IsEffectiveTranslation(item, value) && Matches(record, item) &&
                (string)record["Profile"] == profile && (string)record["TranslationHash"] == TranslationSafety.Hash(value.Trim());
        }

        internal static IList<TranslationChange> Compare(JObject sidecar, IEnumerable<TranslationWorkItem> current)
        {
            var baseline = sidecar?["Tracking"]?["Baseline"] as JObject;
            var items = TranslationWorkItem.Index(current);
            var changes = new List<TranslationChange>();
            foreach (var item in items.Values)
            {
                var record = baseline?[item.Id];
                var staleManual = sidecar?["Tracking"]?["Manual"]?[item.Id];
                var staleAi = sidecar?["Tracking"]?["AI"]?[item.Id];
                var staleMachine = sidecar?["Tracking"]?["Machine"]?[item.Id];
                var staleLegacy = sidecar?["Tracking"]?["Legacy"]?[item.Id];
                var legacyNeedsReview = staleLegacy != null && !Matches(staleLegacy, item) && !Matches(staleManual, item);
                var machineNeedsReview = staleMachine != null && !Matches(staleMachine, item)
                    && !Matches(staleManual, item) && !Matches(staleAi, item);
                if (record == null || !Matches(record, item) || (staleManual != null && !Matches(staleManual, item)) ||
                    (staleAi != null && !Matches(staleAi, item)) ||
                    machineNeedsReview || legacyNeedsReview)
                {
                    var old = staleManual != null && !Matches(staleManual, item) ? staleManual :
                        staleAi != null && !Matches(staleAi, item) ? staleAi :
                        machineNeedsReview ? staleMachine : legacyNeedsReview ? staleLegacy : record;
                    changes.Add(new TranslationChange { Id = item.Id, State = baseline == null ? "未建基准" : record == null ? "新增" : "已修改",
                        OldSource = (string)old?["Source"], OldVersion = (string)old?["PluginVersion"],
                        NewSource = item.Source, Context = item.Context, Current = item });
                }
            }
            if (baseline != null)
                foreach (var entry in baseline.Properties().Where(entry => !items.ContainsKey(entry.Name)))
                    changes.Add(new TranslationChange { Id = entry.Name,
                        State = (string)entry.Value["Kind"] == "Custom" ? "未观测" : "已移除",
                        OldSource = (string)entry.Value["Source"], OldVersion = (string)entry.Value["PluginVersion"], Context = (string)entry.Value["Context"] });
            return changes;
        }
    }
}
