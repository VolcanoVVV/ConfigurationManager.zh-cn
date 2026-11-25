using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using ConfigurationManagerZhCn;
using HarmonyLib;
using UnityEngine;

internal static partial class Program
{
    private static void CheckScalePatches()
    {
        var window = new Rect(760f, 160f, 2240f, 1800f);
        var scale = ConfigurationManagerScale.FitScale(2f, 3840, 2160);
        var logical = ConfigurationManagerScale.ToLogicalRect(window, scale);
        Check(logical == new Rect(380f, 80f, 1120f, 900f),
            "BetterBepInEx anchor converts 4K pixels to 200 percent workbench coordinates");
        var matrix = ConfigurationManagerScale.ScaleMatrix(Matrix4x4.identity, scale);
        Check(matrix.m00 == 2f && matrix.m11 == 2f && matrix.m22 == 1f,
            "BetterBepInEx workbench GUI transform doubles planar sizes at 200 percent");
        var drawer = TranslationDrawerLayout.Calculate(logical, 3840 / scale, 2160 / scale);
        Check(drawer.xMin >= 0f && drawer.xMax * scale <= 3840f && drawer.yMax * scale <= 2160f,
            "scaled BetterBepInEx drawer stays on screen at 4K");

        foreach (var factor in new[] { 1f, 1.5f, 2f, 3f })
        {
            var button = new Rect(324, 300, 280, 20);
            var windowOrigin = new Vector2(640, 180);
            var scrollOrigin = new Vector2(6, 42);
            var scrollPosition = new Vector2(0, 212);
            var screenPointer = windowOrigin + new Vector2(400, 176) * factor;
            var windowPointer = (screenPointer - windowOrigin) / factor;
            var contentPointer = windowPointer - scrollOrigin + scrollPosition;
            var offset = windowPointer - contentPointer;
            var anchor = ConfigurationManagerScale.ToDropdownAnchor(button, offset);
            var popup = ConfigurationManagerScale.FitDropdown(new Rect(anchor.x, anchor.y, button.width, 80), button.height, 650, 540);
            Check(popup == new Rect(330, 150, 280, 80) && popup.Contains(contentPointer + offset),
                "four-option popup lands directly under the scrolled button and matches its mouse hitbox at " + factor);
        }
        var below = ConfigurationManagerScale.FitDropdown(new Rect(330, 140, 240, 100), 20, 600, 540);
        Check(below == new Rect(330, 140, 240, 100), "short popup fits below its button");
        var scrollBelow = ConfigurationManagerScale.FitDropdown(new Rect(330, 490, 240, 80), 20, 600, 540);
        Check(scrollBelow == new Rect(330, 490, 260, 50), "a partially clipped list still opens below and can scroll");
        var above = ConfigurationManagerScale.FitDropdown(new Rect(450, 530, 145, 660), 20, 600, 540);
        Check(above.x >= 0 && above.xMax <= 600 && above.y >= 0 && above.yMax == 510 &&
            above.height == 510 && above.width > 145, "fallback opens upwards only when a row cannot fit below");
        var clipped = ConfigurationManagerScale.FitDropdown(new Rect(330, 600, 240, 120), 20, 600, 540);
        Check(clipped.height > 0 && clipped.yMax <= 540, "past-bottom anchors cannot produce a negative popup height");
        var findTargets = typeof(ConfigurationManagerScaleDropdownPatch).GetMethod("TargetMethods", BindingFlags.Static | BindingFlags.NonPublic);
        var popupHooks = ((IEnumerable<MethodBase>)findTargets.Invoke(null, null)).ToArray();
        Check(popupHooks.Length == 3, "native ComboBox schedules scroll, anchor and deferred draw patches together");
        var buildPopup = ConfigurationManagerScale.RewriteDropdown(PatchProcessor.GetOriginalInstructions(popupHooks[0]), popupHooks[0]).ToList();
        var drawPopup = ConfigurationManagerScale.RewriteDropdown(PatchProcessor.GetOriginalInstructions(popupHooks[1]), popupHooks[1]).ToList();
        var windowPopup = ConfigurationManagerScale.RewriteDropdown(PatchProcessor.GetOriginalInstructions(popupHooks[2]), popupHooks[2]).ToList();
        var scrollIndex = windowPopup.FindIndex(ConfigurationManagerScale.IsSettingsScrollView);
        Check(scrollIndex > 0 && scrollIndex + 1 < windowPopup.Count &&
            windowPopup[scrollIndex - 1].operand is MethodInfo before && before.Name == "BeforeSettingsScrollView" &&
            windowPopup[scrollIndex + 1].operand is MethodInfo after && after.Name == "AfterSettingsScrollView",
            "the same event's coordinates are sampled on each side of the native settings scroll-view call");
        Check(buildPopup.Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(ConfigurationManagerScale)) == 5 &&
            !buildPopup.Any(i => ConfigurationManagerScale.IsDropdownCall(i, "GUIToScreenPoint")) &&
            drawPopup.Any(i => i.operand is MethodInfo m && m.Name == "DropdownDrawPoint") &&
            drawPopup.Any(i => i.operand is MethodInfo m && m.Name == "SelectionGrid"),
            "popup transpiler uses fitted coordinates and retains native selection");
        foreach (var name in new[] { "OnGUI", "CalculateWindowRect" })
        {
            var method = ConfigurationManagerMethods.Get("ConfigurationManager", name);
            Check(method != null, "supported ConfigurationManager exposes scale hook " + name);
            var original = PatchProcessor.GetOriginalInstructions(method).ToList();
            var screenCalls = original.Count(instruction => instruction.operand is MethodInfo info && info.DeclaringType == typeof(Screen));
            var rewritten = ConfigurationManagerScale.RewriteCoordinates(original).ToList();
            Check((name == "OnGUI" || screenCalls > 0) && !rewritten.Any(instruction => instruction.operand is MethodInfo info && info.DeclaringType == typeof(Screen)),
                "scale patch rewrites native screen coordinates in " + name);
            if (name == "OnGUI")
            {
                var mouseCalls = original.Count(instruction => instruction.operand is MethodInfo info && info.Name == "get_mousePosition" && info.DeclaringType?.FullName != "UnityEngine.Event");
                Check(mouseCalls > 0 && rewritten.Count(instruction => instruction.operand is MethodInfo info && info.DeclaringType == typeof(ConfigurationManagerScale) && info.Name.StartsWith("ScaleMouse")) == mouseCalls,
                    "scale patch transforms every actual manager input abstraction call once");
            }
        }
    }
}
