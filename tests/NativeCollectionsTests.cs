using System;
using System.Collections.Generic;
using ConfigurationManagerZhCn;

internal static partial class Program
{
    private static void CheckNativeLocaleAdapter()
    {
        var manager = new NativeManager();
        NativeManager._instance = manager;
        manager._locales["ch"] = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
        manager._locales["ch"].Add("old", "旧覆盖");
        manager._locales["ch"].Add("untouched", "其他模组译文");
        manager._locales["en"] = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
        manager._locales["en"].Add("value", "English");
        var prior = manager._locales["ch"];
        Check(ReferenceEquals(ClientLocaleRefresher.ReadRuntimeMember(typeof(NativeManager), null, "_instance"), manager),
            "native static field wrappers are read as properties");
        var message = ClientLocaleRefresher.ApplyToManager(typeof(NativeManager), manager, true,
            new Dictionary<string, string> { ["value"] = "中文" }, new[] { "old" }, "ch");
        Check(message.Contains("已合并") && manager._locales["ch"]["value"] == "中文" && !manager._locales["ch"].ContainsKey("old"),
            "native dictionary update and remove support optional third parameter and property-backed locale state");
        Check(prior.ContainsKey("old") && !ReferenceEquals(prior, manager._locales["ch"]), "removal follows the current dictionary after update replaces its instance");
        Check(manager._locales["ch"]["untouched"] == "其他模组译文", "replacing native locale dictionary preserves unrelated translations");
        Check(manager._locales["en"]["value"] == "English" && !manager.LastMainLocale && manager._onLocaleUpdatedEvent.Calls == 1,
            "native locale merge preserves English, uses declared default boolean and signals refresh");
        manager.Culture = "en";
        ClientLocaleRefresher.ApplyToManager(typeof(NativeManager), manager, true,
            new Dictionary<string, string> { ["value"] = "新中文" }, Array.Empty<string>(), "ch");
        Check(manager._onLocaleUpdatedEvent.Calls == 1 && manager._locales["en"]["value"] == "English", "English active culture does not receive a Chinese refresh event");
        var rejected = ClientLocaleRefresher.ApplyToManager(typeof(NativeManager), manager, true,
            new Dictionary<string, string> { ["value"] = "错误覆盖" }, Array.Empty<string>(), "en");
        Check(rejected.Contains("拒绝") && manager._locales["en"]["value"] == "English", "server response cannot retarget the English locale");
    }
    private sealed class NativeEvent { internal int Calls; public void Invoke() { Calls++; } }
    private sealed class NativeManager
    {
        public static NativeManager _instance { get; set; }
        public string Culture { get; set; } = "ch";
        public Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.Dictionary<string, string>> _locales { get; } =
            new Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.Dictionary<string, string>>();
        public NativeEvent _onLocaleUpdatedEvent { get; } = new NativeEvent();
        internal bool LastMainLocale;
        public void UpdateLocales(string culture, Il2CppSystem.Collections.Generic.Dictionary<string, string> values, bool isMainLocale = false)
        {
            LastMainLocale = isMainLocale;
            var replacement = new Il2CppSystem.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in values.ValuesForFixture) replacement[pair.Key] = pair.Value;
            _locales[culture] = replacement;
        }
    }
}

// API-shaped stand-in only; no native runtime is initialized by these regression tests.
namespace Il2CppSystem.Collections.Generic
{
    public sealed class Dictionary<TKey, TValue>
    {
        internal readonly System.Collections.Generic.Dictionary<TKey, TValue> ValuesForFixture = new System.Collections.Generic.Dictionary<TKey, TValue>();
        public TValue this[TKey key] { get => ValuesForFixture[key]; set => ValuesForFixture[key] = value; }
        public void Add(TKey key, TValue value) { ValuesForFixture.Add(key, value); }
        public bool ContainsKey(TKey key) => ValuesForFixture.ContainsKey(key);
        public bool Remove(TKey key) => ValuesForFixture.Remove(key);
        public System.Collections.Generic.Dictionary<TKey, TValue>.Enumerator GetEnumerator() => ValuesForFixture.GetEnumerator();
    }
}
