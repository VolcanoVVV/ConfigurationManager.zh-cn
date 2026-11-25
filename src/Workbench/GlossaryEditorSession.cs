using System;
using System.IO;
using System.Linq;
using System.Text;
using TranslationFoundation;

namespace ConfigurationManagerZhCn
{
    // UI drafts never write until Save. Reject changes made on disk since opening/reloading.
    internal sealed class GlossaryEditorSession
    {
        internal readonly string FilePath;
        internal GlossaryDocument Document;
        internal string LoadError = "", OriginalText = "";
        private byte[] _baseline;
        internal string Revision
        {
            get
            {
                if (_baseline == null) return "missing";
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return Convert.ToBase64String(sha.ComputeHash(_baseline));
            }
        }
        internal GlossaryEditorSession(string path) { FilePath = path; Reload(); }
        internal void Reload()
        {
            _baseline = ReadBounded(FilePath);
            Document = null; LoadError = ""; OriginalText = "";
            try
            {
                OriginalText = _baseline == null ? "" : new UTF8Encoding(false, true).GetString(_baseline);
                Document = GlossaryDocument.Parse(OriginalText);
            }
            catch (Exception ex) { LoadError = ex.Message; }
        }
        internal string Save()
        {
            if (Document == null) throw new InvalidDataException("请先导入或修复术语表草稿。");
            var data = new UTF8Encoding(false, true).GetBytes(Document.Serialize());
            var current = ReadBounded(FilePath);
            if ((_baseline == null) != (current == null) || (_baseline != null && !_baseline.SequenceEqual(current)))
                throw new IOException("文件已被其他程序修改。请先导出草稿，再重新读取文件；本次没有覆盖。");
            var directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, ".glossary-" + Guid.NewGuid().ToString("N") + ".tmp");
            var backup = FilePath + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(data, 0, data.Length); output.Flush(true); }
                if (current == null) File.Move(temp, FilePath);
                else File.Replace(temp, FilePath, backup);
                _baseline = data; OriginalText = new UTF8Encoding(false).GetString(data); LoadError = "";
                return current == null ? "" : backup;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static byte[] ReadBounded(string path)
        {
            if (!File.Exists(path)) return null;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[4096]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (bytes.Length + count > TranslationGlossary.MaxFileBytes) throw new InvalidDataException("术语表超过 256 KiB，请用文本编辑器缩减后重新读取。");
                    bytes.Write(buffer, 0, count);
                }
                return bytes.ToArray();
            }
        }
    }
}
