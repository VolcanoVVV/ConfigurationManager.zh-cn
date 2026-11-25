using System.IO;
using BepInEx;
using TranslationFoundation;

namespace ConfigurationManagerZhCn
{
    internal static class ClientGlossary
    {
        internal static string DirectoryPath => Path.Combine(Paths.PluginPath, "zh-cn", "glossary");
        internal static string FilePath => Path.Combine(DirectoryPath, "eft.zh-CN.tsv");
        internal static TranslationGlossary Load() => TranslationGlossary.Load(DirectoryPath);
    }
}
