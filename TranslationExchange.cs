using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    // Clipboard-only exchange: the website never receives API credentials, paths, or the administrator secret.
    internal sealed class TranslationExchange
    {
        internal readonly List<TranslationWorkItem> Local = new List<TranslationWorkItem>();
        internal readonly List<ServerWorkbenchEntry> Server = new List<ServerWorkbenchEntry>();
        internal string Revision;
        internal string Prompt()
        {
            var texts = Server.Count > 0 ? Server.Select(item => item.English) : Local.Select(item => item.Source);
            var values = new JObject();
            var i = 0;
            foreach (var source in texts) values["t" + (++i).ToString("D4")] = source;
            return (Server.Count > 0 ? TranslationSafety.BuildGameTextPrompt("简体中文", null) :
                TranslationSafety.BuildPrompt("简体中文", null)) +
                ClientGlossary.Load().Reference(texts, "简体中文") + "\n" + values.ToString(Formatting.Indented);
        }
        internal Dictionary<string, string> Parse(string json, bool keepOriginal)
        {
            if (json == null || json.Length > 500000) throw new InvalidOperationException("回填 JSON 超过 500000 字符。");
            var value = json.Trim();
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                var first = value.IndexOf('\n'); var last = value.LastIndexOf("```", StringComparison.Ordinal);
                if (first >= 0 && last > first) value = value.Substring(first + 1, last - first - 1).Trim();
            }
            var data = JObject.Parse(value, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var count = Server.Count > 0 ? Server.Count : Local.Count;
            foreach (var property in data.Properties())
            {
                var index = -1;
                for (var i = 0; i < count; i++) if (property.Name == "t" + (i + 1).ToString("D4")) { index = i; break; }
                if (index < 0 || property.Value.Type != JTokenType.String) throw new InvalidOperationException("回填包含未知 ID 或非文本译文。");
                var text = (string)property.Value;
                if (Local.Count > 0)
                    text = TranslationSafety.Validate(TranslationSafety.PreserveOuterFormatting(Local[index].Source, text), Local[index], keepOriginal);
                else text = TranslationSafety.Validate(text, TranslationWorkItem.ForServerText(Server[index].Id, Server[index].English, Server[index].Id), keepOriginal);
                result[Server.Count > 0 ? Server[index].Id : Local[index].Id] = text.Trim();
            }
            if (result.Count == 0) throw new InvalidOperationException("回填没有译文。");
            return result;
        }
    }
}
