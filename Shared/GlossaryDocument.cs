using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TranslationFoundation
{
    // Editable format shared by both runtimes; TranslationGlossary remains the immutable execution snapshot.
    internal sealed class GlossaryRow
    {
        internal string Source = "", Target = "";
        internal bool Enabled = true;
    }
    internal sealed class GlossaryDocument
    {
        internal bool Enabled = true;
        internal int MaxCharacters = 800, MaxEntries = 24;
        internal readonly List<GlossaryRow> Rows = new List<GlossaryRow>();
        internal readonly List<string> Comments = new List<string>();
        private const string DisabledPrefix = "#~\t";
        internal static GlossaryDocument Parse(string text)
        {
            if (text == null || Encoding.UTF8.GetByteCount(text) > TranslationGlossary.MaxFileBytes)
                throw new InvalidDataException("术语表超过 256 KiB。");
            text = text.TrimStart('\uFEFF');
            TranslationGlossary.Parse(text);
            var result = new GlossaryDocument();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var clean = line.Trim();
                if (clean.Length == 0) continue;
                if (clean.StartsWith("#@", StringComparison.Ordinal))
                {
                    var pair = clean.Substring(2).Split('=');
                    if (pair[0] == "enabled") result.Enabled = bool.Parse(pair[1]);
                    else if (pair[0] == "maxcharacters") result.MaxCharacters = int.Parse(pair[1]);
                    else if (pair[0] == "maxentries") result.MaxEntries = int.Parse(pair[1]);
                }
                else if (clean.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                {
                    var cells = clean.Substring(DisabledPrefix.Length).Split('\t');
                    if (cells.Length != 2) throw new InvalidDataException("停用词条格式错误：#~<Tab>原文<Tab>译文。");
                    result.Rows.Add(new GlossaryRow { Source = cells[0], Target = cells[1], Enabled = false });
                }
                else if (clean.StartsWith("#", StringComparison.Ordinal)) result.Comments.Add(line);
                else
                {
                    var cells = clean.Split('\t');
                    result.Rows.Add(new GlossaryRow { Source = cells[0], Target = cells[1] });
                }
            }
            result.Serialize(); // Disabled rows also need validation before they can be re-enabled.
            return result;
        }
        internal string Serialize()
        {
            if (MaxCharacters < 0 || MaxCharacters > 2000 || MaxEntries < 0 || MaxEntries > 64)
                throw new InvalidDataException("字符上限须为 0–2000，条目上限须为 0–64。");
            if (Rows.Count > 2000) throw new InvalidDataException("编辑器最多保存 2000 条（包括停用词条）。");
            var output = new StringBuilder();
            output.Append("#@enabled=").Append(Enabled ? "true" : "false").Append('\n');
            output.Append("#@maxcharacters=").Append(MaxCharacters).Append('\n');
            output.Append("#@maxentries=").Append(MaxEntries).Append('\n');
            foreach (var comment in Comments)
            {
                if (!comment.TrimStart().StartsWith("#", StringComparison.Ordinal) || comment.Contains("\n") || comment.Contains("\r") ||
                    comment.TrimStart().StartsWith("#@", StringComparison.Ordinal) || comment.TrimStart().StartsWith(DisabledPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("术语表注释格式错误。");
                output.Append(comment).Append('\n');
            }
            foreach (var row in Rows)
            {
                var source = (row.Source ?? "").Trim(); var target = (row.Target ?? "").Trim();
                if (source.Length == 0 || target.Length == 0 || source.Length > 80 || target.Length > 120 ||
                    source.StartsWith("#", StringComparison.Ordinal) || (row.Source ?? "").Any(char.IsControl) || (row.Target ?? "").Any(char.IsControl))
                    throw new InvalidDataException("每条须填写原文和译文，原文最多80、译文最多120字符；不能含控制字符，原文不能以 # 开头。");
                if (!row.Enabled) output.Append(DisabledPrefix);
                output.Append(source).Append('\t').Append(target).Append('\n');
            }
            var text = output.ToString();
            if (Encoding.UTF8.GetByteCount(text) > TranslationGlossary.MaxFileBytes) throw new InvalidDataException("术语表超过 256 KiB。");
            TranslationGlossary.Parse(text); // Same duplicate/format checks as the actual AI job.
            return text;
        }
        internal string Preview(string source, int maxInputCharacters)
        {
            var snapshot = TranslationGlossary.Parse(Serialize());
            return snapshot.Reference(new[] { source ?? "" }, "zh-CN", snapshot.Budget(maxInputCharacters));
        }
    }
}
