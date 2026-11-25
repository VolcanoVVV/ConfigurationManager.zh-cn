using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    internal sealed class WorkbenchAppearance
    {
        internal static readonly string[] Presets = { "当前深蓝", "原版灰", "自定义" };
        internal static readonly string[] ColorNames = { "窗口背景", "卡片背景", "按钮底色", "强调色", "正文颜色", "次要文字", "抓取高亮" };
        internal static readonly string[] DarkColors = { "#131821", "#1C2430", "#293342", "#2E6EA8", "#E6F0FA", "#9EB3C9", "#FFD166" };
        internal static readonly string[] ClassicColors = { "#292B2E", "#34373B", "#464A4F", "#697786", "#EEEEEE", "#B8BCC2", "#FFD166" };
        internal readonly ConfigEntry<bool> ToolbarExpanded;
        internal readonly ConfigEntry<float> GuiScale;
        internal readonly ConfigEntry<string> Preset;
        internal readonly ConfigEntry<string>[] Colors;
        internal int Revision { get; private set; }

        internal static float DefaultScale(int width, int height) => width >= 3840 && height >= 2160 ? 2f : 1f;

        internal WorkbenchAppearance(ConfigFile config, float defaultScale = 1f)
        {
            ToolbarExpanded = config.Bind("工作台外观", "展开顶部工具栏", true, "默认展开；记住手动收起或展开的状态。收起后保留小型展开入口。");
            GuiScale = config.Bind("工作台外观", "F12 界面缩放", defaultScale,
                new ConfigDescription("缩放汉化工作台及其弹窗，原版 F12 设置窗口也跟随缩放。1 = 100%，4K 建议 2。小屏幕会限制实际缩放。",
                    new AcceptableValueRange<float>(1f, 3f)));
            Preset = config.Bind("工作台外观", "配色模板", Presets[0],
                new ConfigDescription("当前深蓝、贴合原版的灰色、自定义配色。仅影响汉化工作台。", new AcceptableValueList<string>(Presets)));
            Preset.SettingChanged += delegate { Revision++; };
            Colors = new ConfigEntry<string>[ColorNames.Length];
            for (var index = 0; index < Colors.Length; index++)
            {
                Colors[index] = config.Bind("工作台自定义颜色", ColorNames[index], DarkColors[index], "十六进制颜色 #RRGGBB；选择自定义模板后生效。");
                Colors[index].SettingChanged += delegate { Revision++; };
            }
        }

        internal string[] GetPalette()
        {
            if (Preset.Value != Presets[2]) return (string[])(Preset.Value == Presets[1] ? ClassicColors : DarkColors).Clone();
            var result = new string[Colors.Length];
            for (var index = 0; index < result.Length; index++)
            {
                Color color;
                result[index] = TryParse(Colors[index].Value, out color) ? ToHex(color) : DarkColors[index];
            }
            return result;
        }

        internal bool ApplyCustom(string[] values)
        {
            if (values == null || values.Length != Colors.Length) return false;
            var normalized = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                Color color;
                if (!TryParse(values[index], out color)) return false;
                normalized[index] = ToHex(color);
            }
            for (var index = 0; index < values.Length; index++) Colors[index].Value = normalized[index];
            Preset.Value = Presets[2];
            return true;
        }

        internal static bool TryParse(string value, out Color color)
        {
            color = new Color(1f, 1f, 1f, 1f);
            var hex = (value ?? string.Empty).Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal)) hex = hex.Substring(1);
            uint rgb;
            if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb)) return false;
            color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
            return true;
        }

        internal static string ToHex(Color color)
        {
            return "#" + Channel(color.r).ToString("X2") + Channel(color.g).ToString("X2") + Channel(color.b).ToString("X2");
        }

        private static int Channel(float value) { return (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 255); }
    }
}
