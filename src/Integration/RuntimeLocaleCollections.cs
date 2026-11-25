using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ConfigurationManagerZhCn
{
    // Keep native collection conversion in one adapter; never cast IL2CPP dictionaries to CLR interfaces.
    internal static class RuntimeLocaleCollections
    {
        internal static bool IsStringDictionary(Type type)
        {
            if (type == typeof(Dictionary<string, string>)) return true;
            return type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Il2CppSystem.Collections.Generic.Dictionary`2" &&
                type.GetGenericArguments()[0] == typeof(string) && type.GetGenericArguments()[1] == typeof(string);
        }
        internal static object Create(Type type, IDictionary<string, string> values)
        {
            if (type == typeof(Dictionary<string, string>)) return new Dictionary<string, string>(values, StringComparer.Ordinal);
            if (!IsStringDictionary(type)) throw new InvalidOperationException("未识别的本地化字典类型，已停止合并。");
            var add = type.GetMethod("Add", new[] { typeof(string), typeof(string) });
            if (add == null) throw new InvalidOperationException("本地化字典缺少 Add 接口。");
            var result = Activator.CreateInstance(type);
            foreach (var pair in values) add.Invoke(result, new object[] { pair.Key, pair.Value });
            return result;
        }
        internal static Dictionary<string, string> Snapshot(object dictionary)
        {
            var managed = dictionary as IDictionary<string, string>;
            if (managed != null) return new Dictionary<string, string>(managed, StringComparer.Ordinal);
            if (dictionary == null || !IsStringDictionary(dictionary.GetType()))
                throw new InvalidOperationException("中文语言字典尚未就绪或类型未受支持。");
            var getEnumerator = dictionary.GetType().GetMethod("GetEnumerator", Type.EmptyTypes);
            if (getEnumerator == null) throw new InvalidOperationException("无法枚举当前版本的中文字典，已停止刷新。");
            var enumerator = getEnumerator.Invoke(dictionary, null);
            var moveNext = enumerator.GetType().GetMethod("MoveNext", Type.EmptyTypes);
            var current = enumerator.GetType().GetProperty("Current");
            if (moveNext == null || current == null) throw new InvalidOperationException("本地字典枚举接口不受支持。");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                while ((bool)moveNext.Invoke(enumerator, null))
                {
                    var pair = current.GetValue(enumerator, null);
                    var key = pair.GetType().GetProperty("Key");
                    var value = pair.GetType().GetProperty("Value");
                    if (key == null || value == null) throw new InvalidOperationException("字典项结构不受支持。");
                    result[(string)key.GetValue(pair, null)] = (string)value.GetValue(pair, null);
                }
            }
            finally { (enumerator as IDisposable)?.Dispose(); }
            return result;
        }
        internal static object Find(object dictionary, string key)
        {
            var managed = dictionary as IDictionary;
            if (managed != null) return managed.Contains(key) ? managed[key] : null;
            if (dictionary == null) return null;
            var type = dictionary.GetType();
            var contains = type.GetMethod("ContainsKey", new[] { typeof(string) });
            var indexer = type.GetProperty("Item", new[] { typeof(string) });
            return contains != null && indexer != null && (bool)contains.Invoke(dictionary, new object[] { key })
                ? indexer.GetValue(dictionary, new object[] { key }) : null;
        }
        internal static bool CanRemove(object dictionary) => dictionary is IDictionary<string, string> ||
            dictionary != null && dictionary.GetType().GetMethod("Remove", new[] { typeof(string) }) != null;
        internal static void Remove(object dictionary, string key)
        {
            var managed = dictionary as IDictionary<string, string>;
            if (managed != null) { managed.Remove(key); return; }
            dictionary.GetType().GetMethod("Remove", new[] { typeof(string) }).Invoke(dictionary, new object[] { key });
        }
    }
}
