using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    // Only the ConfigurationManager OnGUI invocation owns this transform. Always restore
    // it in a finalizer, including ExitGUIException, so other mods retain their GUI state.
    internal static class ConfigurationManagerScale
    {
        private static float _scale = 1f;
        private static bool _drawing;
        private static int _lastWidth, _lastHeight;
        private static DropdownFrame _dropdown;
        private static readonly PropertyInfo WindowRect = AccessTools.Property(typeof(ConfigurationManager.ConfigurationManager), "SettingWindowRect");
        internal static int Width => (int)(Screen.width / (_drawing ? _scale : RequestedScale));
        internal static int Height => (int)(Screen.height / (_drawing ? _scale : RequestedScale));
        internal static float RequestedScale => FitScale(ConfigurationManagerZhCnPlugin.Instance?.Appearance?.GuiScale.Value ?? 1f, Screen.width, Screen.height);

        internal static float FitScale(float requested, int width, int height)
        {
            if (float.IsNaN(requested) || float.IsInfinity(requested)) requested = 1f;
            return Math.Max(1f, Math.Min(Math.Min(3f, requested), Math.Min(width / 720f, height / 540f)));
        }

        internal static Rect ToLogicalRect(Rect rect, float scale) =>
            new Rect(rect.x / scale, rect.y / scale, rect.width / scale, rect.height / scale);

        internal static Matrix4x4 ScaleMatrix(Matrix4x4 matrix, float scale) =>
            matrix * Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        internal struct DropdownFrame
        {
            internal Rect Window;
            internal Vector2 WindowMouse, ContentOffset;
            internal bool HasContentOffset;
        }

        internal static void BeginDropdownWindow(object manager, ref DropdownFrame previous)
        {
            previous = _dropdown;
            _dropdown = default(DropdownFrame);
            if ((_drawing ? _scale : RequestedScale) <= 1f || WindowRect == null) return;
            _dropdown.Window = (Rect)WindowRect.GetValue(manager, null);
        }

        internal static void EndDropdownWindow(DropdownFrame previous) { _dropdown = previous; }

        private static void BeforeSettingsScrollView()
        {
            if (_dropdown.Window.width > 0f && _dropdown.Window.height > 0f)
                _dropdown.WindowMouse = Event.current.mousePosition;
        }

        private static void AfterSettingsScrollView()
        {
            if (_dropdown.Window.width <= 0f || _dropdown.Window.height <= 0f) return;
            // The same event is expressed in the window and scroll content coordinate spaces.
            // Their difference includes both the viewport origin and the current scroll offset.
            _dropdown.ContentOffset = _dropdown.WindowMouse - Event.current.mousePosition;
            _dropdown.HasContentOffset = true;
        }

        internal static Vector2 ToDropdownAnchor(Rect button, Vector2 contentOffset) =>
            new Vector2(button.x, button.yMax) + contentOffset;

        internal static Rect FitDropdown(Rect popup, float buttonHeight, float windowWidth, float windowHeight)
        {
            var below = Mathf.Clamp(popup.y, 0f, windowHeight);
            var above = Mathf.Clamp(popup.y - buttonHeight, 0f, windowHeight);
            var roomBelow = windowHeight - below;
            var openAbove = roomBelow < Math.Min(buttonHeight, popup.height) && above > roomBelow;
            var height = Math.Min(popup.height, openAbove ? above : roomBelow);
            var width = Math.Min(windowWidth, popup.width + (height < popup.height ? 20f : 0f));
            return new Rect(Mathf.Clamp(popup.x, 0f, windowWidth - width), openAbove ? above - height : below, width, height);
        }

        private static Vector2 DropdownPoint(Vector2 point)
        {
            return _dropdown.HasContentOffset ? point + _dropdown.ContentOffset : GUIUtility.GUIToScreenPoint(point);
        }

        private static Vector2 DropdownAnchor(Vector2 stockAnchor, Rect button) =>
            _dropdown.HasContentOffset ? ToDropdownAnchor(button, _dropdown.ContentOffset) : GUIUtility.GUIToScreenPoint(stockAnchor);

        private static Rect DropdownRect(float x, float y, float width, float height, Rect button)
        {
            var rect = new Rect(x, y, width, height);
            return _dropdown.HasContentOffset ? FitDropdown(rect, button.height, _dropdown.Window.width, _dropdown.Window.height) : rect;
        }

        // The scaled rectangle is already fitted to the live window. Keep the stock clamp for 100%.
        private static int DropdownLimit(int limit) => _dropdown.HasContentOffset ? int.MaxValue : limit;

        private static Vector2 DropdownDrawPoint(Vector2 screen, Rect fitted) =>
            _dropdown.HasContentOffset ? fitted.position : GUIUtility.ScreenToGUIPoint(screen);

        internal static bool IsSettingsScrollView(CodeInstruction instruction) =>
            instruction.operand is MethodInfo method && method.DeclaringType == typeof(GUILayout) && method.Name == "BeginScrollView";

        internal static bool IsDropdownCall(CodeInstruction instruction, string name) =>
            instruction.operand is MethodInfo method && method.DeclaringType == typeof(GUIUtility) && method.Name == name;

        internal static bool IsDropdownField(CodeInstruction instruction, string name) =>
            instruction.operand is FieldInfo field && field.Name == name;

        internal static bool IsDropdownRect(CodeInstruction instruction) =>
            instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo ctor &&
            ctor.DeclaringType == typeof(Rect) && ctor.GetParameters().Length == 4;

        internal static IEnumerable<CodeInstruction> RewriteDropdown(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            if (original.Name == "SettingsWindow")
            {
                foreach (var instruction in instructions)
                {
                    if (IsSettingsScrollView(instruction))
                    {
                        yield return CodeInstruction.Call(typeof(ConfigurationManagerScale), nameof(BeforeSettingsScrollView))
                            .MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                        yield return instruction;
                        yield return CodeInstruction.Call(typeof(ConfigurationManagerScale), nameof(AfterSettingsScrollView));
                    }
                    else yield return instruction;
                }
                yield break;
            }

            var code = instructions.ToList();
            var show = original.Name == "Show";
            var buttonRect = show ? AccessTools.PropertyGetter(original.DeclaringType, "Rect") : null;
            var outerRect = show ? null : AccessTools.Field(original.DeclaringType, "outerRectScreen");
            var anchor = true;
            for (var index = 0; index < code.Count; index++)
            {
                var instruction = code[index];
                if (show && IsDropdownCall(instruction, "GUIToScreenPoint"))
                {
                    if (anchor)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                        yield return new CodeInstruction(OpCodes.Call, buttonRect);
                    }
                    instruction.operand = AccessTools.Method(typeof(ConfigurationManagerScale), anchor ? nameof(DropdownAnchor) : nameof(DropdownPoint));
                    anchor = false;
                }
                else if (show && IsDropdownRect(instruction) && index + 1 < code.Count && IsDropdownField(code[index + 1], "outerRectScreen"))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Call, buttonRect);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ConfigurationManagerScale), nameof(DropdownRect));
                }
                else if (!show && IsDropdownCall(instruction, "ScreenToGUIPoint"))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Ldfld, outerRect);
                    instruction.operand = AccessTools.Method(typeof(ConfigurationManagerScale), nameof(DropdownDrawPoint));
                }
                yield return instruction;
                if (show && IsDropdownField(instruction, "_windowYmax"))
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ConfigurationManagerScale), nameof(DropdownLimit)));
            }
        }

        internal struct State { internal Matrix4x4 Matrix; internal bool Active; }
        internal static void InvalidateLayout() { _lastWidth = _lastHeight = 0; }

        internal static void Begin(object manager, ref State state)
        {
            if (!(manager is ConfigurationManager.ConfigurationManager cm) || !cm.DisplayingWindow) return;
            state.Matrix = GUI.matrix;
            state.Active = true;
            _drawing = true;
            if (Event.current.type == EventType.Layout &&
                (_scale != RequestedScale || _lastWidth != Screen.width || _lastHeight != Screen.height))
            {
                _scale = RequestedScale;
                _lastWidth = Screen.width;
                _lastHeight = Screen.height;
                ConfigurationManagerMethods.Get("ConfigurationManager", "CalculateWindowRect").Invoke(manager, null);
            }
            GUI.matrix = ScaleMatrix(state.Matrix, _scale);
        }

        internal static void End(State state)
        {
            if (!state.Active) return;
            GUI.matrix = state.Matrix;
            _drawing = false;
        }

        private static Vector3 ScaleMouse(Vector3 value) => value / (_drawing ? _scale : RequestedScale);
        private static Vector2 ScaleMouseLegacy(Vector2 value) => value / (_drawing ? _scale : RequestedScale);

        internal static IEnumerable<CodeInstruction> RewriteCoordinates(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if (method?.DeclaringType == typeof(Screen) && (method.Name == "get_width" || method.Name == "get_height"))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.PropertyGetter(typeof(ConfigurationManagerScale), method.Name == "get_width" ? "Width" : "Height");
                }
                yield return instruction;
                // Mono uses BepInEx's input abstraction; IL2CPP uses Unity Input directly.
                if (method?.Name == "get_mousePosition" && method.DeclaringType != typeof(Event))
                {
                    if (method.ReturnType == typeof(Vector3))
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ConfigurationManagerScale), "ScaleMouse"));
                    else if (method.ReturnType == typeof(Vector2))
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ConfigurationManagerScale), "ScaleMouseLegacy"));
                }
            }
        }
    }

    [HarmonyPatch]
    internal static class ConfigurationManagerScaleRectPatch
    {
        private static MethodBase TargetMethod() => ConfigurationManagerMethods.Get("ConfigurationManager", "CalculateWindowRect");
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ConfigurationManagerScale.RewriteCoordinates(instructions);
    }

    [HarmonyPatch]
    internal static class ConfigurationManagerScaleDropdownPatch
    {
        private static MethodBase[] _targets;

        private static bool Prepare(MethodBase original)
        {
            if (original != null) return true;
            _targets = FindTargets().ToArray();
            return _targets.Length == 3;
        }

        private static IEnumerable<MethodBase> TargetMethods() => _targets ?? FindTargets();

        private static IEnumerable<MethodBase> FindTargets()
        {
            var window = ConfigurationManagerMethods.Get("ConfigurationManager", "SettingsWindow");
            if (window == null || PatchProcessor.GetOriginalInstructions(window).Count(ConfigurationManagerScale.IsSettingsScrollView) != 1)
                yield break;
            var show = ConfigurationManagerMethods.Get("Utilities.ComboBox", "Show");
            if (show == null || AccessTools.PropertyGetter(show.DeclaringType, "Rect") == null) yield break;
            var draw = show.DeclaringType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Select(type => AccessTools.DeclaredMethod(type, "<Show>b__0"))
                .FirstOrDefault(method => method != null && AccessTools.Field(method.DeclaringType, "outerRectScreen")?.FieldType == typeof(Rect));
            if (draw == null) yield break;
            // Install the scroll, hit-test and drawing hooks only when all three match.
            var code = PatchProcessor.GetOriginalInstructions(show).ToList();
            if (code.Count(instruction => ConfigurationManagerScale.IsDropdownCall(instruction, "GUIToScreenPoint")) != 2 ||
                code.Count(instruction => ConfigurationManagerScale.IsDropdownField(instruction, "_windowYmax")) != 2 ||
                !code.Zip(code.Skip(1), (first, next) => ConfigurationManagerScale.IsDropdownRect(first) &&
                    ConfigurationManagerScale.IsDropdownField(next, "outerRectScreen")).Any(match => match) ||
                PatchProcessor.GetOriginalInstructions(draw).Count(instruction => ConfigurationManagerScale.IsDropdownCall(instruction, "ScreenToGUIPoint")) != 1)
                yield break;
            yield return show;
            yield return draw;
            yield return window;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            ConfigurationManagerScale.RewriteDropdown(instructions, __originalMethod);
    }
}
