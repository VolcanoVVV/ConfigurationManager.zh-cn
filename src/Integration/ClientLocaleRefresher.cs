using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ConfigurationManagerZhCn
{
    internal sealed class LocaleRefreshResult
    {
        internal bool Applied;
        internal string Message;
    }
    internal static class ClientLocaleRefresher
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static Task<string> ApplyAsync(IDictionary<string, string> values, IEnumerable<string> removedKeys, string locale)
        {
            var snapshot = new Dictionary<string, string>(values ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            var removed = (removedKeys ?? Enumerable.Empty<string>()).ToArray();
            return MainThreadDispatcher.InvokeAsync(() => Apply(snapshot, removed, locale));
        }

        internal static Task<LocaleRefreshResult> ApplyCheckedAsync(IDictionary<string, string> values, IEnumerable<string> removedKeys, string locale, Func<bool> isCurrent)
        {
            var snapshot = new Dictionary<string, string>(values, StringComparer.Ordinal);
            var removed = removedKeys.ToArray();
            return MainThreadDispatcher.InvokeAsync(() =>
            {
                if (!isCurrent()) throw new OperationCanceledException();
                bool applied;
                var message = Apply(snapshot, removed, locale, out applied);
                return new LocaleRefreshResult { Applied=applied, Message=message };
            });
        }
        private static string Apply(IDictionary<string, string> values, string[] removedKeys, string locale)
        {
            bool applied;
            return Apply(values, removedKeys, locale, out applied);
        }
        private static string Apply(IDictionary<string, string> values, string[] removedKeys, string locale, out bool applied)
        {
            applied = false;
            if (values.Count == 0 && removedKeys.Length == 0)
            {
                applied = true;
                return "客户端没有收到需要合并的文本。";
            }
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == "Assembly-CSharp");
                if (assembly == null) return "当前游戏没有 EFT 本地化程序集，已跳过客户端刷新。";
                var modern = assembly.GetType("EFT.LocalizationManager", false);
                var type = modern ?? assembly.GetType("LocaleManagerClass", false);
                if (type == null) return "未识别当前版本的本地化管理器，请重新打开相关页面。";
                var manager = ReadRuntimeMember(type, null, modern != null ? "_instance" : "LocaleManagerClass_1");
                if (manager == null) return "本地化管理器尚未初始化，请进入主菜单后再刷新。";
                return ApplyToManagerChecked(type, manager, modern != null, values, removedKeys, locale, out applied);
            }
            catch (Exception ex)
            {
                return "客户端热刷新失败（服务端译文仍已生效）：" + ex.GetBaseException().Message;
            }
        }

        internal static object ReadRuntimeMember(Type type, object instance, string name)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            var field = type.GetField(name, flags);
            if (field != null) return field.GetValue(instance);
            // IL2CPP interop represents native fields as managed properties.
            var property = type.GetProperty(name, flags);
            return property != null && property.CanRead ? property.GetValue(instance, null) : null;
        }

        internal static string ApplyToManager(Type type, object manager, bool modern, IDictionary<string, string> values, string[] removedKeys, string locale)
        {
            bool applied;
            return ApplyToManagerChecked(type, manager, modern, values, removedKeys, locale, out applied);
        }
        private static string ApplyToManagerChecked(Type type, object manager, bool modern, IDictionary<string, string> values, string[] removedKeys, string locale, out bool applied)
        {
            applied = false;
            if (!string.IsNullOrWhiteSpace(locale) && !string.Equals(locale, "ch", StringComparison.OrdinalIgnoreCase))
                return "服务端声明的目标不是中文语言表，已拒绝客户端合并。";
            try
            {
                var cultureProperty = type.GetProperty(modern ? "Culture" : "String_0", AllInstance);
                var culture = cultureProperty == null ? null : cultureProperty.GetValue(manager, null) as string;
                var update = type.GetMethods(AllInstance).Where(method => method.Name == "UpdateLocales").FirstOrDefault(method =>
                {
                    var args = method.GetParameters();
                    return args.Length >= 2 && args.Length <= 3 && args[0].ParameterType == typeof(string) &&
                        RuntimeLocaleCollections.IsStringDictionary(args[1].ParameterType) &&
                        (args.Length == 2 || args[2].ParameterType == typeof(bool) && args[2].IsOptional);
                });
                if (string.IsNullOrWhiteSpace(culture) || update == null)
                    return "当前版本不支持安全的运行时合并，请重新进入游戏。";
                // Server translations belong to ch; never overwrite a player's English locale.
                var targetCulture = string.IsNullOrWhiteSpace(locale) ? "ch" : locale;
                var locales = ReadRuntimeMember(type, manager, modern ? "_locales" : "Dictionary_4");
                var target = RuntimeLocaleCollections.Find(locales, targetCulture);
                if (removedKeys.Length > 0 && !RuntimeLocaleCollections.CanRemove(target))
                    return "客户端无法安全移除旧译文，请重新进入游戏。";
                var parameters = update.GetParameters();
                // Some versions replace the dictionary, others merge it. Supply a complete
                // snapshot so either behavior preserves unrelated native and mod translations.
                var merged = RuntimeLocaleCollections.Snapshot(target);
                foreach (var pair in values) merged[pair.Key] = pair.Value;
                foreach (var key in removedKeys) merged.Remove(key);
                var payload = RuntimeLocaleCollections.Create(parameters[1].ParameterType, merged);
                update.Invoke(manager, parameters.Length == 2 ? new object[] { targetCulture, payload } :
                    new object[] { targetCulture, payload, parameters[2].DefaultValue });
                target = RuntimeLocaleCollections.Find(ReadRuntimeMember(type, manager, modern ? "_locales" : "Dictionary_4"), targetCulture);
                if (removedKeys.Length > 0 && !RuntimeLocaleCollections.CanRemove(target))
                    return "合并完成，但当前版本无法安全移除旧译文，请重新进入游戏。";
                foreach (var key in removedKeys) RuntimeLocaleCollections.Remove(target, key);
                applied = true;
                if (!string.Equals(culture, targetCulture, StringComparison.OrdinalIgnoreCase))
                    return "已更新中文语言字典；切换到中文后显示。";
                var localeEvent = ReadRuntimeMember(type, manager, modern ? "_onLocaleUpdatedEvent" : "BindableEvent_0");
                var notify = localeEvent == null ? null : localeEvent.GetType().GetMethod("Invoke", Type.EmptyTypes);
                if (notify != null) notify.Invoke(localeEvent, null);
                return "客户端已合并最新译文并发送界面刷新事件；个别已打开页面可能需要重新进入。";
            }
            catch (Exception ex)
            {
                return "客户端热刷新失败（服务端译文仍已生效）：" + ex.GetBaseException().Message;
            }
        }
    }
}
