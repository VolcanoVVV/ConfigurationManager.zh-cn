using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using ConfigurationManager;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    [BepInPlugin("ConfigurationManager.zh-cn", "Volcano.ConfigurationManager中文选项拓展", "1.0.0")]
    public class ConfigurationManagerZhCnPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            JsoncLocalization.Initialize(Logger);
            UiLocalization.Initialize(Logger);

            try
            {
                var harmony = new Harmony("ConfigurationManager.zh-cn");
                harmony.PatchAll(typeof(ConfigurationManagerZhCnPlugin).Assembly);
                Logger.LogInfo("ConfigurationManager.zh-cn ready.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to apply Harmony patches: " + ex);
            }
        }
    }

    [HarmonyPatch]
    internal static class SettingSearcherPatches
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("ConfigurationManager.SettingSearcher:CollectSettings");
        }

        private static void Prefix()
        {
            foreach (var plugin in PluginFinder.FindPlugins())
                JsoncLocalization.ApplyPluginName(plugin);
        }

        private static void Postfix(ref IEnumerable<SettingEntryBase> results)
        {
            if (results == null) return;

            var list = results as IList<SettingEntryBase> ?? results.ToList();
            var pluginMap = PluginFinder.FindPlugins()
                .GroupBy(p => p.Info.Metadata.GUID)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var group in list.GroupBy(s => s.PluginInfo != null ? s.PluginInfo.GUID : null))
            {
                if (string.IsNullOrEmpty(group.Key)) continue;

                BaseUnityPlugin plugin;
                if (!pluginMap.TryGetValue(group.Key, out plugin)) continue;

                JsoncLocalization.ApplyLocalization(plugin, group);
            }

            results = list;
        }
    }

    [HarmonyPatch]
    internal static class SettingFieldDrawerObjectToGuiContentPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("ConfigurationManager.SettingFieldDrawer:ObjectToGuiContent");
        }

        private static bool Prefix(object x, ref GUIContent __result)
        {
            var enumValue = x as Enum;
            if (enumValue == null)
                return true;

            var localized = JsoncLocalization.GetEnumLocalizedDescription(enumValue.GetType(), enumValue.ToString());
            if (string.IsNullOrEmpty(localized))
                return true;

            __result = new GUIContent(localized);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SettingFieldDrawerFlagsPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("ConfigurationManager.SettingFieldDrawer:DrawFlagsField");
        }

        private static bool Prefix(SettingEntryBase setting, System.Collections.IList enumValues, int maxWidth)
        {
            if (setting == null || enumValues == null)
                return true;

            try
            {
                var currentValue = Convert.ToInt64(setting.Get());
                var values = enumValues.Cast<Enum>()
                    .Select(x => new
                    {
                        name = JsoncLocalization.GetEnumLocalizedDescription(setting.SettingType, x.ToString()) ?? x.ToString(),
                        val = Convert.ToInt64(x)
                    })
                    .ToArray();

                GUILayout.BeginVertical(GUILayout.MaxWidth(maxWidth));
                {
                    for (var index = 0; index < values.Length;)
                    {
                        GUILayout.BeginHorizontal();
                        {
                            var currentWidth = 0;
                            for (; index < values.Length; index++)
                            {
                                var value = values[index];

                                if (value.val != 0)
                                {
                                    var textDimension = (int)GUI.skin.toggle.CalcSize(new GUIContent(value.name)).x;
                                    currentWidth += textDimension;
                                    if (currentWidth > maxWidth)
                                        break;

                                    GUI.changed = false;
                                    var newVal = GUILayout.Toggle((currentValue & value.val) == value.val, value.name,
                                        GUILayout.ExpandWidth(false));
                                    if (GUI.changed)
                                    {
                                        var newValue = newVal ? currentValue | value.val : currentValue & ~value.val;
                                        setting.Set(Enum.ToObject(setting.SettingType, newValue));
                                    }
                                }
                            }
                        }
                        GUILayout.EndHorizontal();
                    }

                    GUI.changed = false;
                }
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                return false;
            }
            catch (Exception ex)
            {
                JsoncLocalization.LogError("DrawFlagsField failed: " + ex.Message);
                return true;
            }
        }
    }
    
            [HarmonyPatch]
    internal static class UiTextPatches
    {
        private static readonly HashSet<string> _uiExclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "searchBox",
            "searchBox "
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return new[]
            {
                AccessTools.Method("ConfigurationManager.ConfigurationManager:OnGUI"),
                AccessTools.Method("ConfigurationManager.ConfigurationManager:SettingsWindow"),
                AccessTools.Method("ConfigurationManager.ConfigurationManager:DrawTips"),
                AccessTools.Method("ConfigurationManager.ConfigurationManager:DrawWindowHeader"),
                AccessTools.Method("ConfigurationManager.ConfigurationManager:DrawSingleSetting"),
                AccessTools.Method("ConfigurationManager.ConfigurationManager:DrawSinglePlugin"),
                AccessTools.Method("ConfigurationManager.ConfigurationManager:DrawDefaultButton"),
                AccessTools.Method("ConfigurationManager.SettingFieldDrawer:DrawBoolField"),
                AccessTools.Method("ConfigurationManager.SettingFieldDrawer:DrawKeyCode"),
                AccessTools.Method("ConfigurationManager.SettingFieldDrawer:DrawKeyboardShortcut")
            }.Where(m => m != null);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getMethod = AccessTools.Method(typeof(UiLocalization), "Get");
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr)
                {
                    yield return instruction;
                    if (_uiExclude.Contains(instruction.operand as string))
                        continue;
                    yield return new CodeInstruction(OpCodes.Call, getMethod);
                    continue;
                }

                yield return instruction;
            }
        }
    }

internal static class PluginFinder
    {
        internal static BaseUnityPlugin[] FindPlugins()
        {
            try
            {
                var chain = Chainloader.PluginInfos.Values.Select(x => x.Instance);
                var scene = UnityEngine.Object.FindObjectsOfType(typeof(BaseUnityPlugin)).Cast<BaseUnityPlugin>();
                return chain.Union(scene).ToArray();
            }
            catch
            {
                return new BaseUnityPlugin[0];
            }
        }
    }
}
