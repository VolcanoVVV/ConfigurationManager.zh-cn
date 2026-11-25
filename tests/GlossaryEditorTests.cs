using System;
using System.IO;
using System.Linq;
using System.Text;
using ConfigurationManagerZhCn;
using TranslationFoundation;

internal static partial class Program
{
    private static void CheckGlossaryEditor()
    {
        var root = Path.Combine(Path.GetTempPath(), "glossary-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "eft.zh-CN.tsv");
            var original = "# user note\n#@maxcharacters=700\nGroundZero\t中心区\n#~\traid\t战局\n";
            File.WriteAllText(path, original, new UTF8Encoding(false));
            var session = new GlossaryEditorSession(path);
            Check(session.Document.Rows.Count == 2 && !session.Document.Rows[1].Enabled, "editor reads disabled rows without treating them as active instructions");
            Check(session.Document.Comments.Single() == "# user note" && session.Document.MaxCharacters == 700, "editor preserves user comments and budgets");
            Check(session.Document.Preview("GroundZero raid raider", 8000).Contains("中心区") && !session.Document.Preview("raid", 8000).Contains("战局"), "draft preview excludes disabled terms");
            session.Document.Rows[0].Target = "用户中心区";
            Check(File.ReadAllText(path) == original, "editing a draft does not write to disk");
            var backup = session.Save();
            Check(File.ReadAllText(backup) == original && File.ReadAllText(path).Contains("用户中心区"), "save atomically replaces file and keeps previous bytes in backup");
            Check(!Directory.GetFiles(root, "*.tmp").Any(), "successful save leaves no temporary files");
            File.WriteAllText(path, "raid\t外部编辑\n");
            Reject(() => session.Save(), "external edits cannot be overwritten by stale drafts");
            Check(File.ReadAllText(path) == "raid\t外部编辑\n", "conflict preserves external edit");
            session.Reload(); session.Document.Rows.Add(new GlossaryRow { Source="RAID", Target="重复" });
            Reject(() => session.Save(), "duplicate active source rejects save");
            Check(File.ReadAllText(path) == "raid\t外部编辑\n", "invalid draft leaves file intact");
            session.Document.Rows.Clear(); session.Save(); session.Reload();
            Check(session.Document.Rows.Count == 0, "empty user glossary stays empty after save/reload");
            File.WriteAllText(path, "invalid TSV"); session.Reload();
            Check(session.Document == null && session.LoadError.Length > 0 && session.OriginalText == "invalid TSV", "malformed file opens as a repair draft");
            session.Document = GlossaryDocument.Parse("GroundZero\t修复\n"); backup = session.Save();
            Check(File.ReadAllText(backup) == "invalid TSV", "repair save backs up invalid original");
            File.WriteAllBytes(path, new byte[] { 0xff, 0xfe }); session.Reload();
            Check(session.Document == null && session.LoadError.Length > 0, "invalid UTF-8 is rejected before editing or requests");
            session.Document = GlossaryDocument.Parse("raid\t战局\n"); backup = session.Save();
            Check(File.ReadAllBytes(backup).SequenceEqual(new byte[] { 0xff, 0xfe }), "repair backup preserves invalid UTF-8 bytes exactly");
            File.Delete(path); var missing = new GlossaryEditorSession(path); missing.Save();
            Check(File.Exists(path), "saving an initially missing glossary creates the active file");
            File.Delete(path); missing = new GlossaryEditorSession(path); File.WriteAllText(path, "raid\t新文件");
            Reject(() => missing.Save(), "another program's newly created glossary cannot be overwritten");
            File.WriteAllBytes(path, new byte[TranslationGlossary.MaxFileBytes + 1]);
            Reject(() => new GlossaryEditorSession(path), "oversized source is rejected before allocation");
            Reject(() => GlossaryDocument.Parse("#~\tbad\n"), "malformed disabled rows are rejected");
            Reject(() => GlossaryDocument.Parse("#~\t\t空原文"), "disabled rows also need valid cells");
            var doc = GlossaryDocument.Parse("GroundZero\t中心区");
            doc.MaxCharacters = 2001; Reject(() => doc.Serialize(), "character cap is bounded");
            doc.MaxCharacters = 800; doc.MaxEntries = 65; Reject(() => doc.Serialize(), "entry cap is bounded");
            doc.MaxEntries = 24; doc.Rows[0].Source = "#comment"; Reject(() => doc.Serialize(), "source cannot accidentally turn into a comment on save");
            foreach (var size in new[] { new[] {1920f,1080f}, new[] {1280f,720f}, new[] {650f,480f}, new[] {320f,240f} })
            {
                var rect = GlossaryEditorWindow.CalculateRect(size[0],size[1]);
                Check(rect.x >= 0 && rect.y >= 0 && rect.xMax <= size[0] && rect.yMax <= size[1], "editor fits available screen");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
