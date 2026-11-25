using System;
using System.IO;

namespace TranslationFoundation
{
    internal static class TranslationLogPath
    {
        internal static string ForProject(string anchor, string project)
        {
            var start = new DirectoryInfo(Path.GetFullPath(anchor));
            for (var dir = start; dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "BepInEx")) || File.Exists(Path.Combine(dir.FullName, "EscapeFromTarkov.exe")))
                    return Path.Combine(dir.FullName, "API翻译日志", project);
                if (dir.Name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
                    return Path.Combine(dir.Parent.FullName, "API翻译日志", project);
                if (dir.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) && dir.Parent != null &&
                    dir.Parent.Name.Equals("user", StringComparison.OrdinalIgnoreCase) && dir.Parent.Parent != null)
                {
                    var root = dir.Parent.Parent;
                    if (root.Parent != null && Directory.Exists(Path.Combine(root.Parent.FullName, "BepInEx"))) root = root.Parent;
                    return Path.Combine(root.FullName, "API翻译日志", project);
                }
            }
            // Dedicated server / test fixture: use the supplied root, not the process working directory.
            return Path.Combine(start.FullName, "API翻译日志", project);
        }
    }
}
