using System.Collections.Generic;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    // A private skin keeps the workbench styling out of plugin-owned controls.
    internal static class TranslationPanelTheme
    {
        private static readonly List<Texture2D> Textures = new List<Texture2D>();
        private static GUISkin _source;
        private static int _revision = -1;
        private static Texture2D _checkOff, _checkOn;
        internal static GUISkin Skin;
        internal static GUIStyle Title, Muted, Section, Primary, SecondaryAction, DangerAction, Tab, Toolbar, Notice, Selected, PluginButton, SelectedPlugin,
            StatusLabel, ConnectionStatus, RichPreview, PickerRow, PickerToggle;
        internal static Color PickerOutlineColor;
        internal static Color CaptureColor;
        internal static bool LightCards;

        private static RectOffset Offset()
        {
            return new RectOffset();
        }

        private static RectOffset Offset(int left, int right, int top, int bottom)
        {
            var offset = new RectOffset();
            offset.left = left;
            offset.right = right;
            offset.top = top;
            offset.bottom = bottom;
            return offset;
        }

        internal static void Ensure(GUISkin source, WorkbenchAppearance appearance)
        {
            if (Skin != null && _source == source && _revision == appearance.Revision)
                return;
            Dispose();
            _source = source;
            _revision = appearance.Revision;
            Skin = Object.Instantiate(source);
            Skin.hideFlags = HideFlags.HideAndDontSave;
            var palette = appearance.GetPalette();
            var colors = new Color[palette.Length];
            for (var index = 0; index < colors.Length; index++) WorkbenchAppearance.TryParse(palette[index], out colors[index]);
            var surface = Background(colors[0]);
            var card = Background(colors[1]);
            var control = Background(colors[2]);
            var hover = Background(Color.Lerp(colors[2], colors[4], 0.15f));
            var accent = Background(colors[3]);
            var accentHover = Background(Color.Lerp(colors[3], colors[4], 0.15f));
            var text = colors[4];
            var accentText = colors[3].grayscale > 0.6f ? Color.black : Color.white;
            CaptureColor = colors[6];
            PickerOutlineColor = colors[3];
            LightCards = colors[1].grayscale > 0.55f;

            Skin.label = new GUIStyle(source.label) { fontSize = 13, wordWrap = true,
                padding = Offset(0, 0, 4, 4), margin = Offset(2, 2, 2, 2) };
            Skin.label.normal.textColor = text;
            Skin.box = new GUIStyle(Skin.label) { padding = Offset(12, 12, 10, 10),
                margin = Offset(0, 0, 4, 8), border = Offset(5, 5, 5, 5) };
            Skin.box.normal.background = card;
            var font = source.label.font ?? source.font;
            Skin.button = new GUIStyle { font = font, fontSize = 13, fixedHeight = 30,
                alignment = TextAnchor.MiddleCenter, stretchWidth = true,
                wordWrap = false, padding = Offset(10, 10, 4, 4),
                margin = Offset(3, 3, 3, 3), border = Offset(5, 5, 5, 5) };
            SetStates(Skin.button, control, hover, accent, text);
            Skin.textField = new GUIStyle(source.textField) { fontSize = 13, fixedHeight = 30,
                padding = Offset(9, 9, 6, 5), margin = Offset(2, 2, 3, 5),
                border = Offset(5, 5, 5, 5), stretchWidth = true };
            SetStates(Skin.textField, surface, control, control, text);
            Skin.textArea = new GUIStyle(Skin.textField) { fixedHeight = 0, wordWrap = true,
                alignment = TextAnchor.UpperLeft, padding = Offset(10, 10, 8, 8) };
            Skin.toggle = new GUIStyle { font = font, fontSize = 13, wordWrap = true,
                alignment = TextAnchor.MiddleLeft, imagePosition = ImagePosition.ImageLeft,
                padding = Offset(3, 3, 4, 4), margin = Offset(3, 3, 3, 3), stretchWidth = true };
            SetStates(Skin.toggle, null, null, null, text);
            Skin.toggle.hover.textColor = Skin.toggle.onHover.textColor = Color.white;
            _checkOff = CheckTexture(colors[2], colors[5], text, false);
            _checkOn = CheckTexture(colors[3], colors[3], accentText, true);
            Skin.window = new GUIStyle(Skin.box) { padding = Offset(16, 16, 14, 14),
                margin = Offset(), contentOffset = Vector2.zero };
            SetStates(Skin.window, surface, surface, surface, text);
            Title = new GUIStyle(Skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            Section = new GUIStyle(Skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            ConnectionStatus = new GUIStyle(Section) { wordWrap = false };
            ConnectionStatus.normal.textColor = colors[3];
            Muted = new GUIStyle(Skin.label) { fontSize = 12 };
            Muted.normal.textColor = colors[5];
            StatusLabel = new GUIStyle(Muted);
            StatusLabel.normal.textColor = Color.white;
            SecondaryAction = new GUIStyle(Skin.button) { fixedHeight = 36 };
            Primary = new GUIStyle(SecondaryAction) { fontStyle = FontStyle.Bold };
            SetStates(Primary, accent, accentHover, accentHover, accentText);
            DangerAction = new GUIStyle(Primary);
            SetStates(DangerAction, Background(new Color32(211, 33, 57, 255)), Background(new Color32(222, 41, 64, 255)),
                Background(new Color32(185, 25, 46, 255)), Color.white);
            Tab = new GUIStyle(Skin.button) { fixedHeight = 44, wordWrap = true,
                padding = Offset(3, 3, 4, 4), fontSize = 12 };
            Selected = new GUIStyle(Skin.button);
            SetStates(Selected, accent, accentHover, accentHover, accentText);
            PluginButton = new GUIStyle(Skin.button) { wordWrap = true, fixedHeight = 0 };
            SelectedPlugin = new GUIStyle(Selected) { wordWrap = true, fixedHeight = 0 };
            PickerRow = new GUIStyle(Skin.label) { alignment = TextAnchor.MiddleLeft, padding = Offset(10, 10, 7, 7),
                margin = Offset(0, 0, 2, 2) };
            SetStates(PickerRow, null, null, null, text);
            PickerToggle = new GUIStyle(Skin.toggle) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
            Toolbar = new GUIStyle(Skin.box) { padding = Offset(7, 7, 4, 4),
                margin = Offset(0, 0, 0, 7) };
            Notice = new GUIStyle(Skin.box) { fontSize = 12 };
            Notice.normal.textColor = colors[5];
            RichPreview = new GUIStyle(Skin.label)
            {
                richText = true,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = Offset(6, 6, 4, 4)
            };
        }

        internal static bool Toggle(bool value, string label, params GUILayoutOption[] options) =>
            Toggle(value, new GUIContent(label), Skin.toggle, options);

        internal static bool Toggle(bool value, GUIContent label, params GUILayoutOption[] options) =>
            Toggle(value, label, Skin.toggle, options);

        internal static bool Toggle(bool value, GUIContent label, GUIStyle style, params GUILayoutOption[] options)
        {
            var content = new GUIContent(label.text, value ? _checkOn : _checkOff, label.tooltip);
            return GUILayout.Toggle(value, content, style, options);
        }

        private static Texture2D CheckTexture(Color fill, Color border, Color mark, bool selected)
        {
            const int size = 16;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            var pixels = new Color[size * size];
            for (var y = 1; y < size - 1; y++)
            for (var x = 1; x < size - 1; x++)
                pixels[y * size + x] = x == 1 || y == 1 || x == size - 2 || y == size - 2 ? border : fill;
            if (selected)
            {
                for (var x = 4; x <= 12; x++)
                {
                    var y = x <= 7 ? 10 - x : x - 4;
                    pixels[y * size + x] = pixels[(y + 1) * size + x] = mark;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            Textures.Add(texture);
            return texture;
        }

        private static void SetStates(GUIStyle style, Texture2D normal, Texture2D hover,
            Texture2D active, Color text)
        {
            style.normal.background = normal;
            style.hover.background = hover;
            style.active.background = style.focused.background = active;
            style.onNormal.background = style.onHover.background = style.onActive.background = style.onFocused.background = active;
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = text;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = text;
        }

        private static Texture2D Background(Color color)
        {
            const int size = 12;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = Mathf.Max(4f - x, x - 7f, 0f);
                var dy = Mathf.Max(4f - y, y - 7f, 0f);
                var pixel = color;
                pixel.a = Mathf.Clamp01(4.5f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * size + x] = pixel;
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            Textures.Add(texture);
            return texture;
        }

        internal static void Dispose()
        {
            if (Skin != null) Object.Destroy(Skin);
            foreach (var texture in Textures) Object.Destroy(texture);
            Textures.Clear();
            Skin = null;
            _source = null;
            _revision = -1;
        }
    }

    internal static class TranslationDrawerLayout
    {
        // Prefer docking beside the manager; on smaller screens overlay its right edge.
        internal static Rect Calculate(Rect manager, float screenWidth, float screenHeight)
        {
            const float margin = 8f;
            var width = System.Math.Min(420f, System.Math.Max(1f, screenWidth - margin * 2f));
            var height = System.Math.Min(System.Math.Max(520f, manager.height), System.Math.Max(1f, screenHeight - margin * 2f));
            var x = manager.xMax + margin;
            if (x + width > screenWidth - margin)
                x = manager.xMin - margin - width >= margin
                    ? manager.xMin - margin - width
                    : manager.xMax - width;
            x = System.Math.Max(margin, System.Math.Min(x, screenWidth - margin - width));
            var y = System.Math.Max(margin, System.Math.Min(manager.y, screenHeight - margin - height));
            return new Rect(x, y, width, height);
        }
    }
}
