using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Pa1.App
{
    /// <summary>曲名文本：用 SkiaSharp 按设备像素直接栅格化，再以 1:1 物理像素贴回 WPF。
    ///
    /// 为什么不用 WPF TextBlock：WPF 的 Ideal + 灰度 AA 在 34px（17 DIP @200%）下，
    /// S/C 这类全曲线字形会呈明显的 1px 台阶（用户反馈的"颗粒感"）；
    /// 而 ClearType 又会带橙蓝彩边。Skia 的覆盖率抗锯齿在同一字号下曲线过渡更细，
    /// 且保持纯灰度（SubpixelText 只影响字形定位，不产生次像素颜色）。
    ///
    /// 另一个好处：位图按「字形路径的真实包围盒 + 四周留白」生成，下伸笔画（J/g/y）
    /// 不会被 WPF 行高/裁切容器切掉——这修掉了 J 底部看起来被砍的问题。</summary>
    internal sealed class SkiaTitle : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            "Text", typeof(string), typeof(SkiaTitle),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
            "FontSize", typeof(double), typeof(SkiaTitle),
            new FrameworkPropertyMetadata(17.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            "Foreground", typeof(Brush), typeof(SkiaTitle),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

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

        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        const double PadDip = 1.5;      /* 四周留白：曲线/下伸笔画留出抗锯齿渐变的空间 */

        static SKTypeface _typeface;
        static string _typefacePath;

        BitmapSource _bitmap;
        Size _dipSize;
        string _signature;
        bool _dirty = true;
        double _scaleOverride;      /* DPI 切换瞬间以 OnDpiChanged 的新值为准，避免合成器变换尚未更新 */
        bool _hasScaleOverride;

        static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SkiaTitle)d).MarkDirty();
        }

        void MarkDirty()
        {
            _dirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            /* 双模显示器切分辨率/缩放时，窗口的 DPI 会变；这里直接采用 WPF 给的新值，
               否则 PresentationSource 的变换可能还是旧的，位图会按旧比例生成（发糊或发大）。 */
            _scaleOverride = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0;
            _hasScaleOverride = true;
            _dirty = true;
            InvalidateMeasure();
            InvalidateVisual();
            base.OnDpiChanged(oldDpi, newDpi);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            EnsureBitmap();
            return _dipSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            EnsureBitmap();
            if (_bitmap != null)
                /* 位图四周各留了 PadDip 的空白（防抗锯齿切边）；绘制时把它挪到元素外，
                   让字形墨迹的左上角与旧 TextBlock 的排版原点重合，位置不因留白而偏移。 */
                dc.DrawImage(_bitmap, new Rect(-PadDip, -PadDip, _dipSize.Width, _dipSize.Height));
            else
                DrawFallback(dc, Text ?? "", FontSize);
        }

        static SKTypeface Typeface
        {
            get
            {
                try
                {
                    string path = TitleFont.FilePath;
                    if (string.IsNullOrEmpty(path)) return null;
                    if (_typeface == null || _typefacePath != path)
                    {
                        if (_typeface != null) { _typeface.Dispose(); _typeface = null; }
                        _typeface = SKTypeface.FromFile(path);
                        _typefacePath = _typeface != null ? path : null;
                    }
                    return _typeface;
                }
                catch { return null; }
            }
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
            return sb != null ? sb.Color : Colors.Black;
        }

        void EnsureBitmap()
        {
            double scale = Scale;
            string text = Text ?? "";
            double size = FontSize > 0 ? FontSize : 17.0;
            Color color = ColorOf(Foreground);
            string sig = text + "|" + size.ToString("R", CultureInfo.InvariantCulture) + "|" +
                         color.ToString() + "|" + scale.ToString("R", CultureInfo.InvariantCulture);
            if (!_dirty && _bitmap != null && sig == _signature) return;
            _dirty = false;
            _signature = sig;
            _bitmap = null;

            SKTypeface tf = Typeface;
            if (tf == null || text.Length == 0)
            {
                _dipSize = MeasureFallback(text, size);
                return;
            }

            SKPath path = null;
            SKFontMetrics fm;
            try
            {
                using (var measurePaint = new SKPaint())
                {
                    measurePaint.Typeface = tf;
                    measurePaint.TextSize = (float)size;
                    measurePaint.IsAntialias = true;
                    measurePaint.SubpixelText = true;
                    measurePaint.LcdRenderText = false;
                    measurePaint.HintingLevel = SKPaintHinting.NoHinting;
                    path = measurePaint.GetTextPath(text, 0, 0);
                    fm = measurePaint.FontMetrics;
                }
                SKRect b = path != null ? path.Bounds : SKRect.Empty;

                /* 高度按字体行高（ascent+descent+leading）算，而不是按字形包围盒：
                   同一字号下所有曲名的行高一致，切换曲目时副标题/卡片不会上下跳。
                   字形超出行高（个别组合字形）时才按实际包围盒扩展。 */
                double ascent = Math.Max(0, -fm.Ascent);
                double descent = Math.Max(0, fm.Descent);
                double leading = Math.Max(0, fm.Leading);
                double topExtent = Math.Max(ascent, -b.Top);
                double bottomExtent = Math.Max(descent + leading, b.Bottom);

                double dipW = Math.Max(1.0, b.Width + PadDip * 2);
                double dipH = Math.Max(1.0, topExtent + bottomExtent + PadDip * 2);
                _dipSize = new Size(dipW, dipH);

                int pw = (int)Math.Ceiling(dipW * scale);
                int ph = (int)Math.Ceiling(dipH * scale);
                if (pw <= 0 || ph <= 0 || pw > 16384 || ph > 2048) return;

                using (var bmp = new SKBitmap(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(bmp))
                using (var paint = new SKPaint())
                {
                    paint.Typeface = tf;
                    paint.TextSize = (float)(size * scale);
                    paint.IsAntialias = true;
                    paint.SubpixelText = true;
                    paint.LcdRenderText = false;
                    paint.HintingLevel = SKPaintHinting.NoHinting;
                    paint.Color = new SKColor(color.R, color.G, color.B, color.A);

                    canvas.Clear(SKColors.Transparent);
                    /* 横向按字形包围盒左缘对齐 PadDip；纵向把基线放在「PadDip + topExtent」处，
                       与行高一致（不是贴字形顶部），这样不同曲目的基线位置相同。 */
                    canvas.Translate((float)((PadDip - b.Left) * scale), (float)((PadDip + topExtent) * scale));
                    canvas.DrawText(text, 0, 0, paint);

                    _bitmap = ToBitmapSource(bmp, scale);
                    /* 位图尺寸按整数像素取整后，DIP 尺寸回算成 像素/scale：
                       保证绘制时是 1:1 物理像素，不触发任何缩放重采样（否则会发糊）。 */
                    _dipSize = new Size(pw / scale, ph / scale);
                }
            }
            catch
            {
                _bitmap = null;
                _dipSize = MeasureFallback(text, size);
            }
            finally
            {
                if (path != null) path.Dispose();
            }
        }

        static BitmapSource ToBitmapSource(SKBitmap bmp, double scale)
        {
            int w = bmp.Width, h = bmp.Height, stride = bmp.RowBytes;
            var buffer = new byte[stride * h];
            Marshal.Copy(bmp.GetPixels(), buffer, 0, buffer.Length);
            double dpi = 96.0 * scale;      /* DIP 尺寸 = 物理尺寸 / scale，保证 1:1 */
            BitmapSource bs = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Pbgra32, null, buffer, stride);
            bs.Freeze();
            return bs;
        }

        /* Skia 不可用（字体解压失败/原生库异常）时的回退：仍用 WPF 文本，保证功能不断。 */
        static FormattedText CreateFallback(string text, double size, Brush brush, double dpi)
        {
            return new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), size, brush ?? Brushes.Black, null,
                TextFormattingMode.Ideal, dpi > 0 ? dpi : 1.0);
        }

        Size MeasureFallback(string text, double size)
        {
            try
            {
                var ft = CreateFallback(text, size, Foreground, Scale);
                return new Size(Math.Max(1, ft.WidthIncludingTrailingWhitespace + PadDip * 2),
                                Math.Max(1, ft.Height + PadDip * 2));
            }
            catch { return new Size(1, size * 1.4); }
        }

        void DrawFallback(DrawingContext dc, string text, double size)
        {
            try
            {
                var ft = CreateFallback(text, size, Foreground, Scale);
                dc.DrawText(ft, new Point(PadDip, PadDip));
            }
            catch { }
        }

        /// <summary>与渲染完全同一套度量，供轮播算行程/文稿宽度用（DIP，含两侧留白）。</summary>
        public static double MeasureTextWidth(string text, double size)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            SKTypeface tf = Typeface;
            if (tf == null) return 0;
            try
            {
                using (var paint = new SKPaint())
                {
                    paint.Typeface = tf;
                    paint.TextSize = (float)(size > 0 ? size : 17.0);
                    paint.SubpixelText = true;
                    paint.IsAntialias = true;
                    paint.HintingLevel = SKPaintHinting.NoHinting;
                    using (SKPath path = paint.GetTextPath(text, 0, 0))
                        return Math.Max(0, path.Bounds.Width) + PadDip * 2;
                }
            }
            catch { return 0; }
        }
    }
}
