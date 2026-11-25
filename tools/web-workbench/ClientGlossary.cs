namespace ConfigurationManagerZhCn;

internal static class ClientGlossary
{
    internal static string DirectoryPath { get; set; }
    internal static TranslationFoundation.TranslationGlossary Load() =>
        TranslationFoundation.TranslationGlossary.Load(DirectoryPath);
}
