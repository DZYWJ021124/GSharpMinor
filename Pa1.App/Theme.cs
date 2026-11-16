using System.Windows.Media;
using SkiaSharp;

namespace Pa1.App
{
    /// <summary>深浅双主题色板（与 phosphor-pa1.html THEMES/CSS 变量对齐）。</summary>
    public sealed class ThemePal
    {
        public string Name;
        /* 屏幕（Skia） */
        public SKColor Grid0, Grid, AxisText, Tick, TickText;
        public SKColor BarGlow, BarBase, BarTop, BarMid, BarBot;
        public SKColor PeakLine, PeakDot, Idle, Hold, Hatch;
        /* 窗体（WPF） */
        public Color WBg, WPanel, WPanel2, WLine, WText, WMuted, WPhos, WPhosDim, WAmber;
        public Color WTrackBg, WThumbRing, WReadoutBg, WShadow, WWnd, WTopBar;

        public static readonly ThemePal Dark = new ThemePal
        {
            Name = "dark",
            /* Dark 频谱采用低眩光磷光层次：柱体保留辨识度，峰值与斜纹退后一级，
               避免近白高光和大面积密纹在黑底上叠加成刺眼亮斑。Light 色板不变。 */
            Grid0 = Rgba(61, 255, 133, 0.18f), Grid = Rgba(61, 255, 133, 0.08f),
            AxisText = Rgba(61, 255, 133, 0.50f),
            Tick = Rgba(61, 255, 133, 0.18f), TickText = Rgba(61, 255, 133, 0.42f),
            BarGlow = Rgba(61, 255, 133, 0.45f), BarBase = Rgba(30, 180, 90, 0.9f),
            BarTop = Rgb(0x72, 0xdd, 0xa4), BarMid = Rgb(0x38, 0xd6, 0x7d), BarBot = Rgb(0x16, 0x8a, 0x49),
            PeakLine = Rgba(188, 232, 207, 0.42f), PeakDot = Rgba(199, 239, 216, 0.76f),
            Idle = Rgba(61, 255, 133, 0.22f), Hold = Rgba(255, 179, 64, 0.85f),
            Hatch = Rgba(157, 164, 153, 0.38f),
            WBg = C(0x0b, 0x0d, 0x08), WPanel = C(0x12, 0x14, 0x0d), WPanel2 = C(0x17, 0x1a, 0x11),
            WLine = C(0x2c, 0x2f, 0x24), WText = C(0xe9, 0xe7, 0xdc), WMuted = C(0x8a, 0x88, 0x7b),
            WPhos = C(0x3d, 0xff, 0x85), WPhosDim = C(0x2c, 0xa3, 0x5c), WAmber = C(0xff, 0xb3, 0x40),
            WTrackBg = C(0x22, 0x24, 0x1b), WThumbRing = C(0x10, 0x12, 0x07),
            WReadoutBg = C(0x04, 0x06, 0x03), WShadow = C(0x00, 0x00, 0x00),
            /* 窗口控制符：Dark 底上比 WMuted 更浅（更易辨认） */
            WWnd = C(0xb5, 0xb2, 0xa3),
            /* 第一排背景带：比 WBg 更浅 */
            WTopBar = C(0x1a, 0x1d, 0x14),
        };

        public static readonly ThemePal Light = new ThemePal
        {
            Name = "light",
            Grid0 = Rgba(12, 122, 61, 0.32f), Grid = Rgba(12, 122, 61, 0.15f),
            AxisText = Rgba(12, 122, 61, 0.8f),
            Tick = Rgba(12, 122, 61, 0.32f), TickText = Rgba(12, 122, 61, 0.62f),
            BarGlow = Rgba(12, 122, 61, 0.16f), BarBase = Rgba(20, 120, 60, 0.8f),
            BarTop = Rgb(0x0a, 0x5c, 0x2e), BarMid = Rgb(0x0c, 0x7a, 0x3d), BarBot = Rgb(0x7f, 0xc9, 0x9a),
            PeakLine = Rgba(10, 92, 46, 0.55f), PeakDot = Rgba(10, 92, 46, 0.85f),
            Idle = Rgba(12, 122, 61, 0.30f), Hold = Rgba(180, 83, 9, 0.9f),
            Hatch = Rgba(90, 110, 90, 0.38f),
            WBg = C(0xf2, 0xef, 0xe4), WPanel = C(0xe9, 0xe5, 0xd6), WPanel2 = C(0xe1, 0xdc, 0xc9),
            WLine = C(0xc9, 0xc4, 0xaf), WText = C(0x23, 0x26, 0x1c), WMuted = C(0x67, 0x64, 0x4f),
            WPhos = C(0x0c, 0x7a, 0x3d), WPhosDim = C(0x0c, 0x7a, 0x3d), WAmber = C(0xb4, 0x53, 0x09),
            WTrackBg = C(0xd9, 0xd4, 0xbf), WThumbRing = C(0xf7, 0xf4, 0xe9),
            WReadoutBg = C(0xfa, 0xf8, 0xf0), WShadow = C(0x80, 0x72, 0x48),
            /* 窗口控制符：Light 底上比 WMuted 更深（更易辨认） */
            WWnd = C(0x3a, 0x38, 0x29),
            /* 第一排背景带：比 WBg 更深 */
            WTopBar = C(0xd8, 0xd2, 0xbc),
        };

        static SKColor Rgb(byte r, byte g, byte b) { return new SKColor(r, g, b); }
        static SKColor Rgba(byte r, byte g, byte b, float a) { return new SKColor(r, g, b, (byte)(a * 255)); }
        static Color C(byte r, byte g, byte b) { return Color.FromRgb(r, g, b); }
    }

    /// <summary>当前主题（窗口与 Skia 屏共用）。</summary>
    public static class Theme
    {
        public static ThemePal Cur = ThemePal.Dark;
        public static void Toggle()
        {
            Cur = ReferenceEquals(Cur, ThemePal.Dark) ? ThemePal.Light : ThemePal.Dark;
        }
        public static void Set(string name)
        {
            Cur = name == "light" ? ThemePal.Light : ThemePal.Dark;
        }
    }
}
