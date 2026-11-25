using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ConfigurationManager;
using HarmonyLib;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    [HarmonyPatch]
    internal static class PluginCaptureContextPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("ConfigurationManager", "DrawSinglePlugin");
        }

        private static void Prefix(object __0)
        {
            TranslationPanel.BeginPlugin(__0);
        }

        private static void Postfix()
        {
            TranslationPanel.EndPlugin();
        }
    }

    [HarmonyPatch]
    internal static class SettingCaptureContextPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("ConfigurationManager", "DrawSingleSetting");
        }

        private static void Prefix(SettingEntryBase __0)
        {
            TranslationPanel.BeginSetting(__0);
        }

        private static void Postfix()
        {
            TranslationPanel.EndSetting();
        }
    }

    [HarmonyPatch]
    internal static class SettingNameCapturePatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("ConfigurationManager", "DrawSettingName");
        }

        private static void Postfix(SettingEntryBase __0)
        {
            TranslationPanel.CaptureSettingName(__0, GUILayoutUtility.GetLastRect());
        }
    }

    [HarmonyPatch]
    internal static class CategoryCapturePatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawCategoryHeader");
        }

        private static void Postfix(string __0)
        {
            TranslationPanel.CaptureCategory(__0, GUILayoutUtility.GetLastRect());
        }
    }

    [HarmonyPatch]
    internal static class PluginHeaderCapturePatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawPluginHeader");
        }

        private static void Postfix(ref bool __result)
        {
            __result = TranslationPanel.CapturePluginHeader(__result, GUILayoutUtility.GetLastRect());
        }
    }

    [HarmonyPatch]
    internal static class CustomSettingValueContextPatch
    {
        private static MethodBase TargetMethod()
        {
            return ConfigurationManagerMethods.Get("SettingFieldDrawer", "DrawSettingValue");
        }

        private static void Prefix(SettingEntryBase __0)
        {
            TranslationPanel.BeginSettingValue(__0);
        }

        private static void Finalizer()
        {
            TranslationPanel.EndSettingValue();
        }
    }

    /// <summary>
    /// Localizes string/GUIContent buttons only while a plugin-owned custom setting drawer is active.
    /// In capture mode the original button is still drawn, but false is returned to the plugin so its
    /// action cannot run while the user is selecting it.
    /// </summary>
    [HarmonyPatch]
    internal static class CustomButtonCapturePatch
    {
        [ThreadStatic]
        private static bool _bypass;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var layoutButtons = typeof(GUILayout).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(method => method.Name == "Button" && method.ReturnType == typeof(bool) &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(GUIContent)));
            var terminalGuiButton = typeof(GUI).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method =>
                {
                    if (method.Name != "Button" || method.ReturnType != typeof(bool))
                        return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 4 && parameters[0].ParameterType == typeof(Rect) &&
                           parameters[1].ParameterType == typeof(int) && parameters[2].ParameterType == typeof(GUIContent);
                });
            return layoutButtons.Concat(terminalGuiButton).Cast<MethodBase>();
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (_bypass || !TranslationPanel.CanLocalizeCustomControl)
                return true;

            var parameters = __originalMethod.GetParameters();
            var textIndex = -1;
            string original = null;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType == typeof(string))
                {
                    textIndex = index;
                    original = __args[index] as string;
                    break;
                }
                if (parameters[index].ParameterType == typeof(GUIContent))
                {
                    textIndex = index;
                    var content = __args[index] as GUIContent;
                    original = content != null ? content.text : null;
                    break;
                }
            }

            if (textIndex < 0 || string.IsNullOrWhiteSpace(original))
                return true;

            var localized = TranslationPanel.LocalizeCustomButton(original);
            if (parameters[textIndex].ParameterType == typeof(string))
                __args[textIndex] = localized;
            else
            {
                var copy = new GUIContent((GUIContent)__args[textIndex]);
                copy.text = localized;
                __args[textIndex] = copy;
            }

            if (!TranslationPanel.ShouldInterceptCustomButton)
                return true;

            var previousChanged = GUI.changed;
            try
            {
                _bypass = true;
                var clicked = (bool)__originalMethod.Invoke(null, __args);
                var rect = __args.Length > 0 && __args[0] is Rect ? (Rect)__args[0] : GUILayoutUtility.GetLastRect();
                if (clicked)
                    TranslationPanel.CaptureCustomButton(original);
                else if (!GUI.enabled) TranslationPanel.CaptureCustomLabel(original, rect);
                TranslationPanel.HighlightCustomControl(original, rect);
            }
            finally
            {
                GUI.changed = previousChanged;
                _bypass = false;
            }

            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Makes passive text emitted by plugin-owned custom drawers visible to the same translation
    /// inventory as buttons and toggles. Restricting this patch to the active custom setting keeps
    /// ConfigurationManager's own labels and the workbench UI out of plugin translation files.
    /// </summary>
    [HarmonyPatch]
    internal static class CustomLabelCapturePatch
    {
        [ThreadStatic]
        private static bool _bypass;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var layoutLabels = typeof(GUILayout).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(method => method.Name == "Label" && method.ReturnType == typeof(void) &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(GUIContent)));
            var guiLabels = typeof(GUI).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(method => method.Name == "Label" && method.ReturnType == typeof(void) &&
                    method.GetParameters().Length > 1 && method.GetParameters()[0].ParameterType == typeof(Rect) &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(GUIContent)));
            return layoutLabels.Concat(guiLabels).Cast<MethodBase>();
        }

        private static void Prefix(MethodBase __originalMethod, object[] __args, out string __state)
        {
            __state = null;
            if (_bypass || !TranslationPanel.CanLocalizeCustomControl)
                return;

            var parameters = __originalMethod.GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                string original = null;
                if (parameters[index].ParameterType == typeof(string)) original = __args[index] as string;
                else if (parameters[index].ParameterType == typeof(GUIContent))
                {
                    var content = __args[index] as GUIContent;
                    original = content != null ? content.text : null;
                }
                else continue;

                if (string.IsNullOrWhiteSpace(original)) return;
                var localized = TranslationPanel.LocalizeCustomButton(original);
                if (parameters[index].ParameterType == typeof(string)) __args[index] = localized;
                else
                {
                    var copy = new GUIContent((GUIContent)__args[index]);
                    copy.text = localized;
                    __args[index] = copy;
                }
                __state = original;
                _bypass = true;
                return;
            }
        }

        private static void Postfix(object[] __args, string __state)
        {
            if (__state == null) return;
            try
            {
                var rect = __args.Length > 0 && __args[0] is Rect ? (Rect)__args[0] : GUILayoutUtility.GetLastRect();
                TranslationPanel.CaptureCustomLabel(__state, rect);
            }
            finally { _bypass = false; }
        }

        private static Exception Finalizer(Exception __exception, string __state)
        {
            if (__state != null) _bypass = false;
            return __exception;
        }
    }

    /// <summary>
    /// Handles custom toggles with the same safety rule as buttons. While grabbing, the toggle is
    /// drawn normally but its previous value is returned so selecting its text cannot change config.
    /// </summary>
    [HarmonyPatch]
    internal static class CustomToggleCapturePatch
    {
        [ThreadStatic]
        private static bool _bypass;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var layoutToggles = typeof(GUILayout).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(method => method.Name == "Toggle" && method.ReturnType == typeof(bool) &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(GUIContent)));
            var terminalGuiToggle = typeof(GUI).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method =>
                {
                    if (method.Name != "Toggle" || method.ReturnType != typeof(bool))
                        return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 5 && parameters[0].ParameterType == typeof(Rect) &&
                           parameters[1].ParameterType == typeof(int) && parameters[2].ParameterType == typeof(bool) &&
                           parameters[3].ParameterType == typeof(GUIContent);
                });
            return layoutToggles.Concat(terminalGuiToggle).Cast<MethodBase>();
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (_bypass || !TranslationPanel.CanLocalizeCustomControl)
                return true;

            var parameters = __originalMethod.GetParameters();
            var textIndex = -1;
            var valueIndex = -1;
            string original = null;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (valueIndex < 0 && parameters[index].ParameterType == typeof(bool))
                    valueIndex = index;
                if (textIndex >= 0)
                    continue;
                if (parameters[index].ParameterType == typeof(string))
                {
                    textIndex = index;
                    original = __args[index] as string;
                }
                else if (parameters[index].ParameterType == typeof(GUIContent))
                {
                    textIndex = index;
                    var content = __args[index] as GUIContent;
                    original = content != null ? content.text : null;
                }
            }

            if (textIndex < 0 || valueIndex < 0 || string.IsNullOrWhiteSpace(original))
                return true;

            var localized = TranslationPanel.LocalizeCustomButton(original);
            if (parameters[textIndex].ParameterType == typeof(string))
                __args[textIndex] = localized;
            else
            {
                var copy = new GUIContent((GUIContent)__args[textIndex]);
                copy.text = localized;
                __args[textIndex] = copy;
            }

            if (!TranslationPanel.ShouldInterceptCustomButton)
                return true;

            var previousValue = (bool)__args[valueIndex];
            var previousChanged = GUI.changed;
            try
            {
                _bypass = true;
                var drawnValue = (bool)__originalMethod.Invoke(null, __args);
                var rect = __args.Length > 0 && __args[0] is Rect ? (Rect)__args[0] : GUILayoutUtility.GetLastRect();
                if (drawnValue != previousValue)
                    TranslationPanel.CaptureCustomButton(original);
                else if (!GUI.enabled) TranslationPanel.CaptureCustomLabel(original, rect);
                TranslationPanel.HighlightCustomControl(original, rect);
            }
            finally
            {
                GUI.changed = previousChanged;
                _bypass = false;
            }

            __result = previousValue;
            return false;
        }
    }
}
