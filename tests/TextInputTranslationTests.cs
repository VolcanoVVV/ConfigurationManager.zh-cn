using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using ConfigurationManager;
using ConfigurationManagerZhCn;

internal static partial class Program
{
    private static async Task CheckTextInputTranslations()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cm-text-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var server = new MockServer())
            {
                var config = new ConfigFile(Path.Combine(folder, "input.cfg"), false);
                var cases = new[] { "", "Default overlay message", "C:\\private\\config.json", "{\"token\":\"private-default\"}" };
                foreach (var defaultText in cases)
                {
                    var value = config.Bind("Overlay", "Text" + Array.IndexOf(cases, defaultText), defaultText, "Text shown in the overlay");
                    value.Value = defaultText.Length == 0 ? "" : "private-current-text";
                    var setting = (SettingEntryBase)Activator.CreateInstance(typeof(SettingEntryBase).Assembly.GetType("ConfigurationManager.ConfigSettingEntry"), new object[] { value, null });
                    SetSettingText(setting, "Category", "Overlay");
                    SetSettingText(setting, "DispName", "Overlay message");
                    var snapshot = CollectionSnapshot(setting);
                    snapshot.Guid = "textinput." + Array.IndexOf(cases, defaultText);
                    var items = snapshot.CreateWorkItems();
                    Check(items.Any(item => item.Field == "DispName") && items.Any(item => item.Field == "Category") && items.Any(item => item.Field == "Description"),
                        "empty or populated text input still collects all three metadata fields");
                    SetCurrent(items.ToArray());
                    var result = await TranslationClients.TranslateAiAsync(items, new TranslationProviderDefinition { Name = "textbox-test", ApiKeyRequired = false },
                        server.Url, "model", "", "zh", "", 20, 10, null, CancellationToken.None);
                    Check(result.Failures.Count == 0 && result.Values.Count == items.Count, "text input metadata survives HTTP response mapping and validation");
                    JsoncLocalization.SaveLayer(snapshot.Guid, TranslationLayer.AI, items, result.Values);
                    Check(items.All(item => JsoncLocalization.GetStoredValue(item, TranslationLayer.AI) == "译文"), "text input metadata translations are saved and readable");
                    Check(value.Value == (defaultText.Length == 0 ? "" : "private-current-text") && (string)value.DefaultValue == defaultText,
                        "translation never overwrites a text input's current or default value");
                }
                Check(server.Requests.All(request => !request.Body.Contains("private-current-text") && !request.Body.Contains("private-default") && !request.Body.Contains("Default overlay message")),
                    "API never receives text box contents or default values");
            }
        }
        finally { Directory.Delete(folder, true); }
    }
}
