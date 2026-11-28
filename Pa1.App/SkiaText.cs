using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Pa1.App
{
    /// <summary>通用 UI 文本：SkiaSharp 按设备像素栅格化 → 1:1 物理像素位图贴回 WPF。
    ///
    /// 为什么不用 TextBlock：窗口是分层窗口（AllowsTransparency=True，为了保连续曲率外轮廓），
    /// 分层窗口没有不透明底色，系统不允许 ClearType——WPF 只能退回灰度 AA，且 WPF 的
    /// 灰度 AA 不做次像素定位、不做灰度伽马校正，小字号英文/数字笔画会一列深一列浅（"糊"）。
    /// Skia 走覆盖率 AA + SubpixelText 定位，同一字号下笔画粗细一致，观感和 VS Code 的
    /// 灰度渲染基本一致（VS Code 能开 ClearType 是因为它窗口不透明）。
    ///
    /// 与 TextBlock 的差异（有意为之）：
    ///  · 高度按字体行高（ascent+descent+leading）算，同字号行高恒定，不会因字形包围盒抖动；
    ///  · 四周留 PadDip 空白并把位图挪到元素外，字形墨迹位置与排版原点一致；
    ///  · 文本含主字体缺字的字符（CJK、▾、❚ 等）时按字符切 run，缺字部分走系统字体回退链。</summary>
    internal sealed class SkiaText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            "Text", typeof(string), typeof(SkiaText),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
            "FontSize", typeof(double), typeof(SkiaText),
            new FrameworkPropertyMetadata(12.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
            "FontWeight", typeof(FontWeight), typeof(SkiaText),
            new FrameworkPropertyMetadata(FontWeights.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            "Foreground", typeof(Brush), typeof(SkiaText),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
            "TextTrimming", typeof(TextTrimming), typeof(SkiaText),
            new FrameworkPropertyMetadata(TextTrimming.None,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public double FontSize
        {
            get { return (double)GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        public FontWeight FontWeight
        {
            get { return (FontWeight)GetValue(FontWeightProperty); }
            set { SetValue(FontWeightProperty, value); }
        }

        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public TextTrimming TextTrimming
        {
            get { return (TextTrimming)GetValue(TextTrimmingProperty); }
            set { SetValue(TextTrimmingProperty, value); }
        }

        const double PadDip = 1.5;

        BitmapSource _bitmap;
        Size _dipSize;
        Size _bmpSize;
        string _signature;
        bool _dirty = true;
        double _lastAvail = double.PositiveInfinity;
        double _scaleOverride;
        bool _hasScaleOverride;

        /* ── 字体缓存：主字重两份 + 缺字回退表 ── */
        static SKTypeface _regular, _bold;
        static bool _loadTried;
        static readonly Dictionary<int, SKTypeface> FallbackCache = new Dictionary<int, SKTypeface>();
        static readonly Dictionary<string, bool> CoverCache = new Dictionary<string, bool>();

        static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SkiaText)d).MarkDirty();
        }

        void MarkDirty()
        {
            _dirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            _scaleOverride = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0;
            _hasScaleOverride = true;
            _dirty = true;
            InvalidateMeasure();
            InvalidateVisual();
            base.OnDpiChanged(oldDpi, newDpi);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            Build(availableSize.Width);
            return _dipSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            Build(_lastAvail);
            if (_bitmap != null)
                /* 位图四周各留 PadDip 空白（防抗锯齿切边）；绘制时把它挪到元素外，
                   字形墨迹的左上角仍与排版原点重合，位置不因留白而偏移。 */
                dc.DrawImage(_bitmap, new Rect(-PadDip, -PadDip, _bmpSize.Width, _bmpSize.Height));
            else
                DrawFallback(dc);
        }

        double Scale
        {
            get
            {
                if (_hasScaleOverride && _scaleOverride > 0) return _scaleOverride;
                try
                {
                    var src = PresentationSource.FromVisual(this);
                    if (src != null && src.CompositionTarget != null)
                    {
                        double s = src.CompositionTarget.TransformToDevice.M11;
                        if (s > 0) return s;
                    }
                }
                catch { }
                try { return VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
                return 1.0;
            }
        }

        static Color ColorOf(Brush brush)
        {
            var sb = brush as SolidColorBrush;
            if (sb != null) return sb.Color;
            if (brush == null) return Colors.Black;
            try
            {
                var conv = new BrushConverter();
                var b2 = conv.ConvertFromString(brush.ToString()) as SolidColorBrush;
                if (b2 != null) return b2.Color;
            }
            catch { }
            return Colors.Black;
        }

        /* ══════════ 字体 ══════════ */

        static SKTypeface LoadFromResource(string file)
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/Fonts/" + file, UriKind.Absolute);
                var sri = Application.GetResourceStream(uri);
                if (sri == null) return null;
                using (var ms = new MemoryStream())
                {
                    sri.Stream.CopyTo(ms);
                    byte[] data = ms.ToArray();
                    return SKTypeface.FromData(SKData.CreateCopy(data));
                }
            }
            catch { return null; }
        }

        static void EnsureFonts()
        {
            if (_loadTried) return;
            _loadTried = true;
            _regular = LoadFromResource("Pa1SansSC-Regular.ttf");
            _bold = LoadFromResource("Pa1SansSC-Bold.ttf");
        }

        static SKTypeface Primary(bool bold)
        {
            EnsureFonts();
            SKTypeface tf = bold ? _bold : _regular;
            return tf ?? _regular;
        }

        /// <summary>主字体是否覆盖该码位（缓存）。</summary>
        static bool Covers(SKTypeface tf, int cp)
        {
            if (tf == null) return false;
            string key = tf.FamilyName + "|" + tf.FontWeight.ToString(CultureInfo.InvariantCulture) + "|" +
                         tf.FontStyle.Slant.ToString() + "|" + cp.ToString(CultureInfo.InvariantCulture);
            bool ok;
            if (CoverCache.TryGetValue(key, out ok)) return ok;
            try { ok = tf.ContainsGlyph(cp); }
            catch { try { ok = tf.GetGlyph(cp) != 0; } catch { ok = false; } }
            CoverCache[key] = ok;
            return ok;
        }

        /// <summary>缺字回退：优先本机无衬线中文字体，再退回系统匹配（与 WPF 的一模一样）。</summary>
        static SKTypeface Fallback(int cp)
        {
            SKTypeface tf;
            if (FallbackCache.TryGetValue(cp, out tf) && tf != null) return tf;
            tf = null;
            string[] prefer = { "Noto Sans SC", "Source Han Sans SC", "Microsoft YaHei UI", "Segoe UI Symbol", "Segoe UI" };
            foreach (string fam in prefer)
            {
                var cand = SKTypeface.FromFamilyName(fam);
                if (cand != null && Covers(cand, cp)) { tf = cand; break; }
                if (cand != null) cand.Dispose();
            }
            if (tf == null)
            {
                try { tf = SKFontManager.Default.MatchCharacter((char)cp); } catch { tf = null; }
            }
            if (tf == null) tf = SKTypeface.Default;
            FallbackCache[cp] = tf;
            return tf;
        }

        /* ══════════ 排版 ══════════ */

        sealed class Run
        {
            public SKTypeface Tf;
            public string S;
            public float Advance;       /* 设备像素 */
        }

        sealed class Layout
        {
            public List<Run> Runs = new List<Run>();
            public double Width;        /* DIP */
            public double Height;       /* DIP */
            public double TopExtent;    /* DIP，基线以上 */
            public double Asc;          /* DIP */
        }

        /* 按「主字体是否覆盖」把字符串切成 run 序列（相邻同字体合并）。 */
        static List<KeyValuePair<SKTypeface, string>> SplitRuns(SKTypeface primary, string text)
        {
            var list = new List<KeyValuePair<SKTypeface, string>>();
            if (string.IsNullOrEmpty(text)) return list;
            var cur = new System.Text.StringBuilder();
            SKTypeface curTf = null;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                SKTypeface tf = Covers(primary, cp) ? primary : Fallback(cp);
                if (curTf != null && !ReferenceEquals(tf, curTf))
                {
                    list.Add(new KeyValuePair<SKTypeface, string>(curTf, cur.ToString()));
                    cur.Length = 0;
                }
                curTf = tf;
                cur.Append(text[i]);
                if (cp > 0xFFFF && i + 1 < text.Length) { cur.Append(text[i + 1]); i++; }
            }
            if (curTf != null && cur.Length > 0)
                list.Add(new KeyValuePair<SKTypeface, string>(curTf, cur.ToString()));
            return list;
        }

        static Layout Compose(SKTypeface primary, string text, double size, double scale, double availDip, TextTrimming trimming)
        {
            var lay = new Layout();
            List<KeyValuePair<SKTypeface, string>> parts = SplitRuns(primary, text);
            double availPx = double.IsInfinity(availDip) ? double.MaxValue : Math.Max(0, availDip * scale);

            /* 省略号：先按整串量宽，超宽则二分前缀 + "…" */
            if (trimming != TextTrimming.None && !double.IsInfinity(availDip))
            {
                double full = MeasureParts(parts, size * scale);
                if (full > availPx)
                {
                    string src = text;
                    int lo = 0, hi = src.Length;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        var cand = SplitRuns(primary, src.Substring(0, mid) + "…");
                        if (MeasureParts(cand, size * scale) <= availPx) lo = mid; else hi = mid - 1;
                    }
                    parts = SplitRuns(primary, lo > 0 ? src.Substring(0, lo) + "…" : "…");
                }
            }

            double asc = 0, desc = 0, lead = 0;
            foreach (var p in parts)
            {
                using (var paint = new SKPaint())
                {
                    paint.Typeface = p.Key;
                    paint.TextSize = (float)(size * scale);
                    paint.SubpixelText = true;
                    paint.IsAntialias = true;
                    paint.HintingLevel = SKPaintHinting.NoHinting;
                    var fm = paint.FontMetrics;
                    asc = Math.Max(asc, Math.Max(0, -fm.Ascent));
                    desc = Math.Max(desc, Math.Max(0, fm.Descent));
                    lead = Math.Max(lead, Math.Max(0, fm.Leading));
                    lay.Runs.Add(new Run { Tf = p.Key, S = p.Value, Advance = paint.MeasureText(p.Value) });
                }
            }
            if (parts.Count == 0)
            {
                using (var paint = new SKPaint())
                {
                    paint.Typeface = primary;
                    paint.TextSize = (float)(size * scale);
                    paint.SubpixelText = true;
                    var fm = paint.FontMetrics;
                    asc = Math.Max(0, -fm.Ascent);
                    desc = Math.Max(0, fm.Descent);
                    lead = Math.Max(0, fm.Leading);
                }
            }

            double wPx = 0;
            foreach (var r in lay.Runs) wPx += r.Advance;

            lay.Asc = asc / scale;
            lay.TopExtent = asc / scale;
            lay.Width = wPx / scale;                            /* 不含留白 */
            lay.Height = (asc + desc + lead) / scale;
            return lay;
        }

        static double MeasureParts(List<KeyValuePair<SKTypeface, string>> parts, double textSizePx)
        {
            double w = 0;
            using (var paint = new SKPaint())
            {
                paint.SubpixelText = true;
                paint.IsAntialias = true;
                paint.HintingLevel = SKPaintHinting.NoHinting;
                paint.TextSize = (float)textSizePx;
                foreach (var p in parts)
                {
                    paint.Typeface = p.Key;
                    w += paint.MeasureText(p.Value);
                }
            }
            return w;
        }

        void Build(double availDip)
        {
            _lastAvail = availDip;
            double scale = Scale;
            string text = Text ?? "";
            double size = FontSize > 0 ? FontSize : 12.0;
            bool bold = FontWeight.ToOpenTypeWeight() >= 600;
            Color color = ColorOf(Foreground);

            string sig = text + "|" + size.ToString("R", CultureInfo.InvariantCulture) + "|" + bold + "|" +
                         color.ToString() + "|" + TextTrimming + "|" +
                         (double.IsInfinity(availDip) ? "inf" : Math.Round(availDip, 2).ToString("R", CultureInfo.InvariantCulture)) + "|" +
                         scale.ToString("R", CultureInfo.InvariantCulture);
            if (!_dirty && sig == _signature) return;
            _dirty = false;
            _signature = sig;
            _bitmap = null;

            SKTypeface primary = Primary(bold);
            if (primary == null)
            {
                _dipSize = MeasureFallback(text, size);
                return;
            }

            try
            {
                var lay = Compose(primary, text, size, scale, availDip, TextTrimming);

                /* 布局尺寸 = 纯排版尺寸（字宽 + 行高），与 TextBlock 一致：
                   四周留白只加在位图上（防抗锯齿切边），不能进测量——否则每个标签高 3 DIP，
                   层层叠加会把行高/按钮高全部顶大，频谱区被挤矮（dB 刻度挤成一团）。 */
                _dipSize = new Size(Math.Max(0.0, lay.Width), Math.Max(0.0, lay.Height));

                double bwDip = Math.Max(1.0, lay.Width + PadDip * 2);
                double bhDip = Math.Max(1.0, lay.Height + PadDip * 2);
                int pw = (int)Math.Ceiling(bwDip * scale);
                int ph = (int)Math.Ceiling(bhDip * scale);
                if (pw <= 0 || ph <= 0 || pw > 16384 || ph > 2048)
                {
                    _dipSize = new Size(1, 1);
                    return;
                }

                using (var bmp = new SKBitmap(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(bmp))
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.SubpixelText = true;
                    paint.LcdRenderText = false;
                    paint.HintingLevel = SKPaintHinting.NoHinting;
                    paint.TextSize = (float)(size * scale);
                    paint.Style = SKPaintStyle.Fill;
                    paint.StrokeWidth = 1;
                    paint.Color = new SKColor(color.R, color.G, color.B, color.A);

                    canvas.Clear(SKColors.Transparent);
                    canvas.Translate((float)(PadDip * scale), (float)((PadDip + lay.TopExtent) * scale));
                    float x = 0;
                    foreach (var r in lay.Runs)
                    {
                        paint.Typeface = r.Tf;
                        canvas.DrawText(r.S, x, 0, paint);
                        x += r.Advance;
                    }

                    _bitmap = ToBitmapSource(bmp, scale);
                    /* 位图 DIP 尺寸 = 像素数/scale（1:1 物理像素贴图，避免重采样发糊） */
                    _bmpSize = new Size(pw / scale, ph / scale);
                }
            }
            catch
            {
                _bitmap = null;
                _dipSize = MeasureFallback(text, size);
            }
        }

        static BitmapSource ToBitmapSource(SKBitmap bmp, double scale)
        {
            int w = bmp.Width, h = bmp.Height, stride = bmp.RowBytes;
            var buffer = new byte[stride * h];
            Marshal.Copy(bmp.GetPixels(), buffer, 0, buffer.Length);
            double dpi = 96.0 * scale;
            BitmapSource bs = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Pbgra32, null, buffer, stride);
            bs.Freeze();
            return bs;
        }

        static FormattedText CreateFallback(string text, double size, Brush brush, double dpi)
        {
            return new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush ?? Brushes.Black, null,
                TextFormattingMode.Ideal, dpi > 0 ? dpi : 1.0);
        }

        Size MeasureFallback(string text, double size)
        {
            try
            {
                var ft = CreateFallback(text, size, Foreground, Scale);
                return new Size(Math.Max(1, ft.WidthIncludingTrailingWhitespace),
                                Math.Max(1, ft.Height));
            }
            catch { return new Size(1, size * 1.4); }
        }

        void DrawFallback(DrawingContext dc)
        {
            try
            {
                var ft = CreateFallback(Text ?? "", FontSize > 0 ? FontSize : 12.0, Foreground, Scale);
                dc.DrawText(ft, new Point(0, 0));
            }
            catch { }
        }

        /* ══════════ 给布局用：量宽（DIP，含留白） ══════════ */

        public static double MeasureWidth(string text, double size, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            SKTypeface primary = Primary(bold);
            if (primary == null) return 0;
            try
            {
                var lay = Compose(primary, text, size, 1.0, double.PositiveInfinity, TextTrimming.None);
                return lay.Width + PadDip * 2;
            }
            catch { return 0; }
        }
    }
}
