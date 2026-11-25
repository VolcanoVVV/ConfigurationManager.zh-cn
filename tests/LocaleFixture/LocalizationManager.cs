using System;
using System.Collections.Generic;

// Test fixture only: the reflection surface used by the actual plugin DLL.
namespace EFT
{
    public sealed class LocalizationManager
    {
        public static LocalizationManager _instance = new LocalizationManager();
        public string Culture { get; set; } = "ch";
        public Dictionary<string, Dictionary<string, string>> _locales = new Dictionary<string, Dictionary<string, string>>
        {
            ["ch"] = new Dictionary<string, string>(), ["en"] = new Dictionary<string, string>()
        };
        public LocaleEvent _onLocaleUpdatedEvent = new LocaleEvent();
        public int MergeThread;
        public void UpdateLocales(string culture, Dictionary<string, string> values)
        {
            MergeThread = Environment.CurrentManagedThreadId;
            foreach (var pair in values) _locales[culture][pair.Key] = pair.Value;
        }
    }
    public sealed class LocaleEvent
    {
        public int Count, LastThread;
        public void Invoke() { Count++; LastThread = Environment.CurrentManagedThreadId; }
    }
}
