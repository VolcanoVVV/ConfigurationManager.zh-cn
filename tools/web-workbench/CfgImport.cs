using System.Text.RegularExpressions;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

internal sealed class CfgImport
{
    internal const int MaxCharacters = 262144;
    internal const int MaxItems = 1600;
    private const string GuidComment = "## Plugin GUID:";
    private const string TypeComment = "# Setting type:";
    private const string ChoicesComment = "# Acceptable values:";
    private static readonly Regex Header = new(@"^## Settings file was created by plugin (.+) v[0-9][^\s]*\s*$");
    private static readonly Regex PluginGuid = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$");
    private static readonly Regex MemberName = new(@"^[\p{L}_][\p{L}\p{Nd}_]*$");
    private static readonly Regex TypeName = new(@"^[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*$");
    private static readonly Regex ReservedFile = new(@"^(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "String", "Boolean", "Char", "Byte", "SByte", "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64",
        "Single", "Double", "Decimal", "Color", "Color32", "Vector2", "Vector3", "Vector4", "KeyboardShortcut"
    };

    internal readonly string Guid;
    internal readonly string Name;
    internal readonly IList<TranslationWorkItem> Items;
    internal readonly int SkippedTypes;

    private CfgImport(string guid, string name, IList<TranslationWorkItem> items, int skippedTypes)
    {
        Guid = guid;
        Name = name;
        Items = items;
        SkippedTypes = skippedTypes;
    }

    internal static CfgImport Parse(string filename, string text, string overrideGuid)
    {
        if (filename.Length == 0 || filename.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
            !filename.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            throw new InputError("请选择一份 BepInEx CFG 文件。");
        if (text.Length > MaxCharacters || text.Contains('\0')) throw new InputError("CFG 内容过大或编码无效。");

        string section = null, declaredGuid = null, name = null, type = null, choices = null;
        var comments = new List<string>();
        var settings = new List<StaticSetting>();
        var identities = new HashSet<(string, string)>();
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (section == null && line.StartsWith(GuidComment, StringComparison.Ordinal))
            {
                declaredGuid = line[GuidComment.Length..].Trim();
                continue;
            }
            if (section == null && Header.Match(line) is { Success: true } header)
            {
                name = header.Groups[1].Value.Trim();
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                comments.Clear();
                type = choices = null;
                continue;
            }
            if (section == null) continue;
            if (line.StartsWith("##", StringComparison.Ordinal)) { comments.Add(line[2..].TrimStart()); continue; }
            if (line.StartsWith(TypeComment, StringComparison.Ordinal)) { type = line[TypeComment.Length..].Trim(); continue; }
            if (line.StartsWith(ChoicesComment, StringComparison.Ordinal)) { choices = line[ChoicesComment.Length..].Trim(); continue; }
            if (line.StartsWith('#') || line.StartsWith(';')) continue;
            var equals = line.IndexOf('=');
            if (equals < 1) continue;
            var key = line[..equals].Trim();
            if (!identities.Add((section, key)))
                throw new InputError("CFG 中有重复的分类与配置键，请先核对原文件。");
            settings.Add(new StaticSetting(section, key, string.Join("\n", comments).Trim(), type,
                choices?.Split(',').Select(value => value.Trim()).ToArray()));
            comments.Clear();
            type = choices = null;
        }
        if (settings.Count == 0) throw new InputError("CFG 中未找到分类下的设置。");
        var guid = string.IsNullOrWhiteSpace(overrideGuid) ? declaredGuid ?? filename[..^4] : overrideGuid.Trim();
        if (!PluginGuid.IsMatch(guid) || guid.EndsWith('.') || ReservedFile.IsMatch(guid))
            throw new InputError("无法识别有效的插件 GUID，请填写与游戏中一致的 GUID。");

        var snapshot = new LocalizationSnapshot { Guid = guid, PluginName = name ?? guid };
        var outputKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            var storageKey = "[" + setting.Section + "]" + setting.Name;
            if (!outputKeys.Add(storageKey)) throw new InputError("CFG 分类和名称组合后重复，请核对原文件。");
            snapshot.Settings.Add(new SettingSnapshot
            {
                StorageKey = storageKey, Category = setting.Section, DispName = setting.Name, Description = setting.Description
            });
        }
        var skipped = 0;
        foreach (var group in settings.Where(item => item.Choices != null)
            .GroupBy(item => item.Type ?? "", StringComparer.Ordinal))
        {
            // BepInEx can also print acceptable values for ordinary strings and numeric settings.
            var members = new HashSet<string>(group.First().Choices, StringComparer.Ordinal);
            if (!TypeName.IsMatch(group.Key) || PrimitiveTypes.Contains(TranslationWorkItem.ShortEnumType(group.Key)) ||
                members.Any(member => !MemberName.IsMatch(member)) || group.Any(item => !members.SetEquals(item.Choices)))
            {
                skipped++;
                continue;
            }
            foreach (var member in group.First().Choices.Distinct(StringComparer.Ordinal))
                snapshot.EnumValues.Add(new EnumValueSnapshot { TypeName = group.Key, MemberName = member });
        }
        var items = snapshot.CreateWorkItems(new TranslationScope
        {
            PluginNames = name != null, Categories = true, SettingNames = true, Descriptions = true, Enums = true
        });
        if (items.Count > MaxItems) throw new InputError("CFG 可译字段超过 1600 条。请先导入较小的配置文件。");
        return new CfgImport(guid, snapshot.PluginName, items, skipped);
    }

    internal JObject Export(JObject values)
    {
        if (values.Count == 0) throw new InputError("请先完成至少一条翻译。");
        var items = TranslationWorkItem.Index(Items);
        var layer = new JObject();
        foreach (var value in values.Properties())
        {
            if (!items.TryGetValue(value.Name, out var item) || value.Value.Type != JTokenType.String ||
                ((string)value.Value).Length > 4000)
                throw new InputError("译文包含未知条目或格式错误。请重新读取本次 CFG。");
            try { item.WriteTo(layer, TranslationSafety.Validate((string)value.Value, item)); }
            catch (TranslationValidationException ex) { throw new InputError(ex.Message); }
        }
        return new JObject { ["SchemaVersion"] = 2, ["AI"] = layer };
    }

    private sealed record StaticSetting(string Section, string Name, string Description, string Type, string[] Choices);
}
