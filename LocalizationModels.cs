using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    public enum LocalizationDisplayMode
    {
        Chinese,
        English,
        Bilingual
    }

    internal enum TranslationLayer
    {
        Machine,
        AI,
        Manual
    }

    internal sealed class LocalizationSnapshot
    {
        internal string Guid;
        internal string PluginName;
        internal string PluginVersion;
        internal readonly List<SettingSnapshot> Settings = new List<SettingSnapshot>();
        internal readonly List<EnumValueSnapshot> EnumValues = new List<EnumValueSnapshot>();
        internal readonly List<CustomControlSnapshot> CustomControls = new List<CustomControlSnapshot>();
        internal readonly Dictionary<string, HashSet<string>> CustomControlIndex = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        internal bool CustomControlIndexReady;

        internal IList<TranslationWorkItem> CreateWorkItems()
        {
            return CreateWorkItems(TranslationScope.All, true);
        }

        internal IList<TranslationWorkItem> CreateWorkItems(TranslationScope scope)
        {
            return CreateWorkItems(scope, false);
        }

        private IList<TranslationWorkItem> CreateWorkItems(TranslationScope scope, bool includeCapturedDynamic)
        {
            var items = new List<TranslationWorkItem>();
            if (scope.PluginNames)
            {
                var title = TranslationWorkItem.ForPlugin(Guid, PluginName);
                title.RelatedDescription = string.Join("\n", Settings
                    .Where(setting => !string.IsNullOrWhiteSpace(setting.Description)).Take(2)
                    .Select(setting => setting.DispName + ": " + setting.Description.Substring(0, Math.Min(110, setting.Description.Length))));
                items.Add(title);
            }

            foreach (var setting in Settings)
            {
                if (scope.Categories && !string.IsNullOrWhiteSpace(setting.Category))
                    items.Add(setting.WorkItem(Guid, "Category", setting.Category, "配置分类"));
                if (scope.SettingNames && !string.IsNullOrWhiteSpace(setting.DispName))
                    items.Add(setting.WorkItem(Guid, "DispName", setting.DispName, "配置项显示名"));
                if (scope.Descriptions && !string.IsNullOrWhiteSpace(setting.Description))
                    items.Add(setting.WorkItem(Guid, "Description", setting.Description, "配置项说明"));
            }

            if (scope.Enums)
            {
                var aliases = EnumValues.Select(value => value.TypeName).Distinct(StringComparer.Ordinal)
                    .GroupBy(TranslationWorkItem.ShortEnumType, StringComparer.Ordinal)
                    .Where(group => group.Count() == 1).ToDictionary(group => group.Single(), group => group.Key, StringComparer.Ordinal);
                foreach (var value in EnumValues)
                {
                    var item = TranslationWorkItem.ForEnum(Guid, value.TypeName, value.MemberName, value.MemberName,
                        "枚举显示值；类型：" + value.TypeName + "；成员：" + value.MemberName);
                    aliases.TryGetValue(value.TypeName, out item.EnumAlias);
                    items.Add(item);
                }
            }
            if (scope.CustomControls)
                items.AddRange(CustomControls.Where(value => !value.ExcludeFromBatch || includeCapturedDynamic).Select(value => TranslationWorkItem.ForCustom(
                    Guid, value.SettingKey, value.OriginalText,
                    "自定义控件；设置键：" + value.SettingKey)));
            foreach (var item in items)
            {
                item.PluginName = PluginName;
                item.PluginVersion = PluginVersion;
            }
            return TranslationWorkItem.Unique(items);
        }
    }

    internal sealed class SettingSnapshot
    {
        internal string StorageKey;
        // Ambiguous display aliases must never select another setting's translation.
        internal bool HasAmbiguousLegacyKey;
        internal string Category;
        internal string DispName;
        internal string Description;

        internal string QualifiedKey => HasAmbiguousLegacyKey ? null : "[" + Category + "]" + DispName;

        internal TranslationWorkItem WorkItem(string guid, string field, string source, string label)
        {
            var item = TranslationWorkItem.ForSetting(guid, StorageKey, field, source, label + "；设置键：" + StorageKey);
            item.QualifiedSettingKey = QualifiedKey;
            if (field != "Category") item.OriginalSection = Category;
            if (field == "DispName") item.RelatedDescription = Description;
            return item;
        }
    }

    internal sealed class EnumValueSnapshot
    {
        internal string TypeName;
        internal string MemberName;
    }

    internal sealed class CustomControlSnapshot
    {
        internal bool ExcludeFromBatch;
        internal string SettingKey;
        internal string OriginalText;
    }

    internal sealed class TranslationScope
    {
        internal static readonly TranslationScope All = new TranslationScope
        {
            PluginNames = true,
            Categories = true,
            SettingNames = true,
            Descriptions = true,
            Enums = true,
            CustomControls = true
        };

        internal bool PluginNames;
        internal bool Categories;
        internal bool SettingNames;
        internal bool Descriptions;
        internal bool Enums;
        internal bool CustomControls;
    }

    internal sealed class TranslationWorkItem
    {
        // A setting can have several wrappers but still refer to one config definition.
        // Keep the snapshot/settings positional mapping; normalize every work-item consumer.
        internal static IList<TranslationWorkItem> Unique(IEnumerable<TranslationWorkItem> source)
        {
            var result = new List<TranslationWorkItem>();
            var seen = new Dictionary<string, TranslationWorkItem>(StringComparer.Ordinal);
            foreach (var item in source)
            {
                if (item == null || string.IsNullOrEmpty(item.Id))
                    throw new InvalidOperationException("Missing translation identity.");
                TranslationWorkItem previous;
                if (seen.TryGetValue(item.Id, out previous))
                {
                    if (previous.PluginGuid != item.PluginGuid || previous.Source != item.Source || previous.Context != item.Context ||
                        previous.Kind != item.Kind || previous.EntryKey != item.EntryKey || previous.Field != item.Field ||
                        previous.EnumType != item.EnumType || previous.EnumMember != item.EnumMember ||
                        previous.OriginalSection != item.OriginalSection || previous.RelatedDescription != item.RelatedDescription ||
                        previous.QualifiedSettingKey != item.QualifiedSettingKey ||
                        previous.EnumAlias != item.EnumAlias ||
                        previous.PluginVersion != item.PluginVersion || previous.PluginName != item.PluginName)
                        throw new InvalidOperationException("Conflicting translation identity: " + item.Id);
                    continue;
                }
                seen.Add(item.Id, item);
                result.Add(item);
            }
            return result;
        }

        internal static Dictionary<string, TranslationWorkItem> Index(IEnumerable<TranslationWorkItem> source)
        {
            var result = new Dictionary<string, TranslationWorkItem>(StringComparer.Ordinal);
            foreach (var item in Unique(source)) result.Add(item.Id, item);
            return result;
        }

        internal string Id;
        internal string PluginGuid;
        internal string Kind;
        internal string EntryKey;
        internal string Field;
        internal string EnumType;
        internal string EnumAlias;
        internal string EnumMember;
        internal string Source;
        internal string Context;
        internal string PluginName;
        internal string PluginVersion;
        internal string OriginalSection;
        internal string RelatedDescription;
        internal string QualifiedSettingKey;

        internal string BriefDescription => RelatedDescription != null && RelatedDescription.Length > 480
            ? RelatedDescription.Substring(0, 480) + "…" : RelatedDescription;

        internal static string ShortEnumType(string type) => type.Substring(type.LastIndexOfAny(new[] { '.', '+' }) + 1);

        internal TranslationWorkItem WithRequestId(string id)
        {
            var item = (TranslationWorkItem)MemberwiseClone();
            item.Id = id;
            return item;
        }

        internal static TranslationWorkItem ForPlugin(string guid, string source)
        {
            return new TranslationWorkItem
            {
                Id = "plugin-name",
                PluginGuid = guid,
                Kind = "Plugin",
                Field = "PluginName",
                Source = source,
                Context = "插件名称"
            };
        }

        internal static TranslationWorkItem ForSetting(string guid, string key, string field, string source, string context)
        {
            return new TranslationWorkItem
            {
                Id = "setting|" + key + "|" + field,
                PluginGuid = guid,
                Kind = "Setting",
                EntryKey = key,
                Field = field,
                Source = source,
                Context = context
            };
        }

        internal static TranslationWorkItem ForEnum(string guid, string type, string member, string source, string context)
        {
            return new TranslationWorkItem
            {
                Id = "enum|" + type + "|" + member,
                PluginGuid = guid,
                Kind = "Enum",
                EnumType = type,
                EnumMember = member,
                Source = source,
                Context = context
            };
        }

        internal static TranslationWorkItem ForServerText(string key, string source, string context)
        {
            var item = ForSetting("server", key, "Description", source, context);
            item.Kind = "ServerGameText";
            return item;
        }

        internal static TranslationWorkItem ForCustom(string guid, string settingKey, string source, string context)
        {
            return new TranslationWorkItem
            {
                Id = "custom|" + settingKey + "|" + source,
                PluginGuid = guid,
                Kind = "Custom",
                EntryKey = settingKey,
                Source = source,
                Context = context
            };
        }

        internal void WriteTo(JObject layer, string value)
        {
            if (Kind == "Plugin")
                TranslationTracking.Object(layer, "PluginInfo")["PluginName"] = value;
            else if (Kind == "Setting")
                TranslationTracking.Object(TranslationTracking.Object(layer, "ConfigEntries"), EntryKey)[Field] = value;
            else if (Kind == "Enum")
                TranslationTracking.Object(TranslationTracking.Object(layer, "Enum"), EnumType)[EnumMember] = value;
            else
                TranslationTracking.Object(TranslationTracking.Object(layer, "CustomControls"), EntryKey)[Source] = value;
        }
    }

    internal sealed class TranslationResult
    {
        internal string StopReason;
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Failures = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal sealed class TranslationActivity
    {
        internal IList<TranslationWorkItem> Items;
        internal string Phase;
        internal bool RequestStarted;
        internal string SystemPrompt;
        internal int InputCharacters;
        internal long? InputTokens, OutputTokens;
    }

    internal sealed class TranslationProviderDefinition
    {
        internal string Name;
        internal string Endpoint;
        internal string DefaultModel;
        internal bool ApiKeyRequired;
        internal bool AnthropicAuthentication;
        internal string DocumentationUrl;
    }

    internal sealed class MachineProviderDefinition
    {
        internal string Name;
        internal string Endpoint;
        internal bool ApiKeyRequired;
        internal bool AppIdRequired;
        internal string DocumentationUrl;
        internal string WebsiteUrl;
        internal string TargetLanguage = "zh-Hans";
    }

    internal static class TranslationProviderCatalog
    {
        internal static readonly MachineProviderDefinition[] MachineProviders = TranslationFoundation.TranslationProviderPresets.Machine.Select(p => new MachineProviderDefinition
        { Name=p.Name, Endpoint=p.Endpoint, TargetLanguage=p.TargetLanguage, ApiKeyRequired=p.KeyRequired, AppIdRequired=p.AppIdRequired,
            DocumentationUrl=p.Documentation, WebsiteUrl=string.IsNullOrEmpty(p.Website) ? null : p.Website }).ToArray();
        internal static readonly TranslationProviderDefinition[] AiProviders = TranslationFoundation.TranslationProviderPresets.Ai.Select(p => new TranslationProviderDefinition
        { Name=p.Name, Endpoint=p.Endpoint, DefaultModel=p.Model, ApiKeyRequired=p.KeyRequired,
            AnthropicAuthentication=p.Anthropic, DocumentationUrl=p.Documentation }).ToArray();

        internal static TranslationProviderDefinition GetAiProvider(string name)
        {
            return AiProviders.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? AiProviders[1];
        }

        internal static MachineProviderDefinition GetMachineProvider(string name)
        {
            return MachineProviders.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? MachineProviders[0];
        }
    }
}
