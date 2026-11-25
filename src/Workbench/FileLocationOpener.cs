using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;

namespace ConfigurationManagerZhCn
{
    internal static class FileLocationOpener
    {
        internal static string ServerPathTooltip(string relativePath)
        {
            string resolved;
            return TryResolveServerTranslation(relativePath, out resolved)
                ? "在资源管理器中定位：" + resolved
                : "服务端相对路径：db/locales/ch/" + relativePath + "。远程 Fika 客户端无法打开服务器主机上的文件。";
        }

        internal static void OpenServerTranslation(string relativePath)
        {
            string path;
            if (!TryResolveServerTranslation(relativePath, out path))
                throw new FileNotFoundException("本机未找到 SimpleTranslatorCS 服务端目录。远程 Fika 玩家需要让服务器管理员在主机上打开：db/locales/ch/" + relativePath);
            Open(path);
        }

        internal static void Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("文件路径为空。", "path");
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new FileNotFoundException("文件不存在：" + full);
            var argument = File.Exists(full) ? "/select,\"" + full + "\"" : "\"" + full + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }

        private static bool TryResolveServerTranslation(string relativePath, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
                || relativePath.Replace('\\', '/').Split('/').Any(part => part == "..")) return false;
            var gameRoot = Paths.GameRootPath;
            foreach (var runtime in new[] { "SPT_Runtime", "SPT" })
            {
                var mods = Path.Combine(gameRoot, runtime, "user", "mods");
                if (!Directory.Exists(mods)) continue;
                foreach (var mod in Directory.GetDirectories(mods))
                {
                    if (!File.Exists(Path.Combine(mod, "SimpleTranslatorCS.dll"))) continue;
                    var candidate = Path.GetFullPath(Path.Combine(mod, "db", "locales", "ch",
                        relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    var root = Path.GetFullPath(Path.Combine(mod, "db", "locales", "ch")) + Path.DirectorySeparatorChar;
                    if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    {
                        resolved = candidate;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
