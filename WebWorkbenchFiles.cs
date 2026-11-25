using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal sealed class WebWorkbenchFiles
    {
        internal const int MaxSnapshotBytes = 96 * 1024 * 1024;
        internal const int MaxDraftBytes = 2 * 1024 * 1024;
        internal readonly string DirectoryPath;

        internal WebWorkbenchFiles(string pluginPath)
        {
            DirectoryPath = Path.Combine(Path.GetFullPath(pluginPath), "zh-cn", "web-workbench");
        }

        internal static string Name(string domain, string kind)
        {
            if (kind == "api-checkpoint" && (domain == "f12" || domain == "server" || domain == "cfg"))
                return domain + ".api-checkpoint.json";
            if (domain != "f12" && domain != "server") throw new InvalidOperationException("未知工作区。");
            if (kind != "snapshot" && kind != "draft" && kind != "receipt" && (domain != "server" || kind != "connection"))
                throw new InvalidOperationException("未知工作台文件。");
            return domain + "." + kind + ".json";
        }

        internal JObject ReadServerConnection()
        {
            var value = Read("server", "connection");
            if (!Guid.TryParseExact((string)value?["id"], "N", out _) ||
                value["locked"]?.Type != JTokenType.Boolean ||
                !long.TryParse((string)value["expiresUtcTicks"], out var expires))
                return null;
            var now = DateTime.UtcNow;
            return expires > now.Ticks && expires <= now.AddSeconds(20).Ticks ? value : null;
        }

        private string Resolve(string domain, string kind)
        {
            var path = Path.Combine(DirectoryPath, Name(domain, kind));
            RequireUnlinkedPath(path);
            return path;
        }

        internal static void RequireUnlinkedPath(string path)
        {
            // Prevent the web bridge from following redirected folders/files inside the installation.
            var parent = new FileInfo(path).Directory;
            while (parent != null)
            {
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("工作目录不能位于符号链接或目录联接下。");
                parent = parent.Parent;
            }
            CheckFile(path);
        }

        private static void CheckFile(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("工作台文件不能是符号链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        internal JObject Read(string domain, string kind)
        {
            var path = Resolve(domain, kind);
            if (!File.Exists(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
            using (var json = new JsonTextReader(reader) { MaxDepth = 20, DateParseHandling = DateParseHandling.None })
            {
                if (stream.Length > Limit(kind)) throw new InvalidDataException("工作台文件超过大小限制。");
                var value = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (json.Read()) throw new InvalidDataException("工作台文件包含多余内容。");
                if ((int?)value["schema"] != 1 || (string)value["domain"] != domain)
                    throw new InvalidDataException("工作台文件版本或工作区不匹配。");
                return value;
            }
        }

        internal void Write(string domain, string kind, JObject value)
        {
            var path = Resolve(domain, kind);
            value["schema"] = 1;
            value["domain"] = domain;
            var bytes = new UTF8Encoding(false).GetBytes(value.ToString(Formatting.None));
            if (bytes.Length > Limit(kind)) throw new InvalidDataException("工作台文件超过大小限制。");
            Directory.CreateDirectory(DirectoryPath);
            var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes, 0, bytes.Length);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static int Limit(string kind) => kind == "snapshot" ? MaxSnapshotBytes : MaxDraftBytes;
    }
}
