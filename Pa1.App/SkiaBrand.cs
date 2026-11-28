using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Pa1.App
{
    /// <summary>品牌标题「G# minor」：Skia 按设备像素栅格化，1:1 物理像素贴回 WPF。
    ///
    /// 为什么不用 TextBlock + DropShadowEffect：WPF 的 Effect 会把整段文字先渲染进中间位图再模糊，
    /// 72 物理像素的细斜体笔画会明显发虚（用户反馈"大标题有点模糊"）。这里把辉光也交给 Skia：
    /// 先画一层高斯模糊的绿色影子，再把清晰的字压在上面——样式（颜色/半径/透明度）与原来的
    /// DropShadowEffect 对齐，但文字本身不再经过 Effect 管线。
    ///
    /// G# minor 的排版保持原样：G 36 / # 24 上标（顶边与 G 顶边对齐）/ " minor" 36，Italic + Bold。</summary>
    internal sealed class SkiaBrand : FrameworkElement
    {
        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            "Foreground", typeof(Brush), typeof(SkiaBrand),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
            "GlowColor", typeof(Color), typeof(SkiaBrand),
            new FrameworkPropertyMetadata(Color.FromRgb(0x3d, 0xff, 0x85),
                FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty GlowOpacityProperty = DependencyProperty.Register(
            "GlowOpacity", typeof(double), typeof(SkiaBrand),
            new FrameworkPropertyMetadata(0.5,
                FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public static readonly DependencyProperty GlowRadiusProperty = DependencyProperty.Register(
            "GlowRadius", typeof(double), typeof(SkiaBrand),
            new FrameworkPropertyMetadata(22.0,
                FrameworkPropertyMetadataOptions.AffectsRender, OnVisualChanged));

        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public Color GlowColor
        {
            get { return (Color)GetValue(GlowColorProperty); }
            set { SetValue(GlowColorProperty, value); }
        }

        public double GlowOpacity
        {
            get { return (double)GetValue(GlowOpacityProperty); }
            set { SetValue(GlowOpacityProperty, value); }
        }

        public double GlowRadius
        {
            get { return (double)GetValue(GlowRadiusProperty); }
            set { SetValue(GlowRadiusProperty, value); }
        }

        const double BaseSize = 36;          /* 与 XAML 里 TextBlock.FontSize 一致 */
        const double HashSize = 24;          /* 与 "#" Run 的 FontSize 一致 */
        const double PadDip = 1.5;           /* 文字四周留白，避免抗锯齿贴边 */

        static SKTypeface _typeface;
        static SKData _typefaceData;
        static bool _typefaceTried;

        BitmapSource _bitmap;
        Size _dipSize;
        Rect _drawRect;
        string _signature;
        bool _dirty = true;
        double _scaleOverride;
        bool _hasScaleOverride;

        static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SkiaBrand)d).MarkDirty();
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
            EnsureBitmap();
            return _dipSize;
        }

        protected override void OnRender(DrawingContext dc)
        {
            EnsureBitmap();
            if (_bitmap != null)
                dc.DrawImage(_bitmap, _drawRect);
            else
                DrawFallback(dc);
        }

        static SKTypeface Typeface
        {
            get
            {
                if (_typefaceTried) return _typeface;
                _typefaceTried = true;
                try
                {
                    var res = Application.GetResourceStream(
                        new Uri("Assets/Fonts/CormorantGaramond-BoldItalic.ttf", UriKind.Relative));
                    if (res == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        res.Stream.CopyTo(ms);
                        _typefaceData = SKData.CreateCopy(ms.ToArray());
                    }
                    _typeface = SKTypeface.FromData(_typefaceData);
                }
                catch { _typeface = null; }
                return _typeface;
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
            Color color = ColorOf(Foreground);
            Color glow = GlowColor;
            double glowOpacity = Math.Max(0, Math.Min(1, GlowOpacity));
            double glowRadius = Math.Max(0, GlowRadius);
            string sig = color + "|" + glow + "|" + glowOpacity.ToString("R", CultureInfo.InvariantCulture) + "|" +
                         glowRadius.ToString("R", CultureInfo.InvariantCulture) + "|" +
                         scale.ToString("R", CultureInfo.InvariantCulture);
            if (!_dirty && _bitmap != null && sig == _signature) return;
            _dirty = false;
            _signature = sig;
            _bitmap = null;

            SKTypeface tf = Typeface;
            if (tf == null)
            {
                _dipSize = MeasureFallback();
                _drawRect = new Rect(0, 0, _dipSize.Width, _dipSize.Height);
                return;
            }

            try
            {
                float wG, wHash, wRest;
                SKFontMetrics fmMain, fmHash;
                using (var p = new SKPaint())
                {
                    p.Typeface = tf;
                    p.IsAntialias = true;
                    p.SubpixelText = true;
                    p.LcdRenderText = false;
                    p.HintingLevel = SKPaintHinting.NoHinting;

                    p.TextSize = (float)BaseSize;
                    fmMain = p.FontMetrics;
                    wG = p.MeasureText("G");

                    p.TextSize = (float)HashSize;
                    fmHash = p.FontMetrics;
                    wHash = p.MeasureText("#");

                    p.TextSize = (float)BaseSize;
                    wRest = p.MeasureText(" minor");
                }

                double ascent = Math.Max(0, -fmMain.Ascent);
                double descent = Math.Max(0, fmMain.Descent);
                double leading = Math.Max(0, fmMain.Leading);
                double hashAscent = Math.Max(0, -fmHash.Ascent);

                double textW = wG + wHash + wRest;              /* 不含留白的文字宽度 */
                double textH = ascent + descent + leading;

                /* 元素尺寸保持「文字行框」大小（和旧 TextBlock 的占位一致），辉光画在元素外面；
                   位图自己带 M 的边距，绘制时向左上偏移，保证文字墨迹仍从元素 0,0 开始。

                   留白/边距必须落在整物理像素上：固定 1.5 DIP 在 200% 下是 3px（整数，贴图 1:1），
                   100% 下是 1.5px，整张位图被放在半像素位置，WPF 双线性插值会把字糊一层。 */
                int padPx = Math.Max(1, (int)Math.Ceiling(PadDip * scale));
                double padDip = padPx / scale;
                int mPx = Math.Max(padPx, (int)Math.Ceiling((glowRadius + padDip) * scale));
                double M = mPx / scale;

                _dipSize = new Size(textW + padDip * 2, textH + padDip * 2);
                /* 位图内的文字墨迹起点在 (M + padDip)；绘制矩形再向左上退同样的量，
                   墨迹才落在元素 0,0，与旧 TextBlock 的排版原点一致。偏移·scale 恒为整数。 */
                _drawRect = new Rect(-(M + padDip), -(M + padDip),
                                     textW + padDip * 2 + M * 2, textH + padDip * 2 + M * 2);

                int pw = (int)Math.Ceiling(textW * scale) + 2 * (mPx + padPx);
                int ph = (int)Math.Ceiling(textH * scale) + 2 * (mPx + padPx);
                if (pw <= 0 || ph <= 0 || pw > 16384 || ph > 4096) return;

                /* 基线吸附整像素：小比例下水平笔画才不发灰 */
                double baseline = (padPx + Math.Round(ascent * scale)) / scale;
                double hashBaseline = baseline - (ascent - hashAscent);
                double x = padDip;

                using (var bmp = new SKBitmap(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.Transparent);
                    /* 位图内坐标 = 元素坐标 + M（绘制时整体偏移 -M） */
                    canvas.Translate(mPx + padPx, mPx + padPx);

                    if (glowOpacity > 0 && glowRadius > 0)
                    {
                        using (var shadow = NewPaint(tf, BaseSize * scale,
                                  Color.FromArgb((byte)Math.Round(255 * glowOpacity), glow.R, glow.G, glow.B)))
                        {
                            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal,
                                (float)(glowRadius / 3.0 * scale));
                            DrawRuns(canvas, shadow, scale, x, baseline, hashBaseline, wG, wHash);
                        }
                    }

                    using (var paint = NewPaint(tf, BaseSize * scale, color))
                        DrawRuns(canvas, paint, scale, x, baseline, hashBaseline, wG, wHash);

                    _bitmap = ToBitmapSource(bmp, scale);
                }
            }
            catch
            {
                _bitmap = null;
                _dipSize = MeasureFallback();
                _drawRect = new Rect(0, 0, _dipSize.Width, _dipSize.Height);
            }
        }

        void DrawRuns(SKCanvas canvas, SKPaint basePaint, double scale,
                      double x, double baseline, double hashBaseline, double wG, double wHash)
        {
            /* 三段各自 SetTextSize：G / # 上标 / " minor" */
            using (var paint = basePaint.Clone())
            {
                paint.TextSize = (float)(BaseSize * scale);
                canvas.DrawText("G", (float)(x * scale), (float)(baseline * scale), paint);
            }
            using (var paint = basePaint.Clone())
            {
                paint.TextSize = (float)(HashSize * scale);
                canvas.DrawText("#", (float)((x + wG) * scale), (float)(hashBaseline * scale), paint);
            }
            using (var paint = basePaint.Clone())
            {
                paint.TextSize = (float)(BaseSize * scale);
                canvas.DrawText(" minor", (float)((x + wG + wHash) * scale), (float)(baseline * scale), paint);
            }
        }

        static SKPaint NewPaint(SKTypeface tf, double sizePx, Color color)
        {
            return new SKPaint
            {
                Typeface = tf,
                TextSize = (float)sizePx,
                IsAntialias = true,
                SubpixelText = true,
                LcdRenderText = false,
                HintingLevel = SKPaintHinting.NoHinting,
                Color = new SKColor(color.R, color.G, color.B, color.A),
            };
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

        /* Skia 不可用时的回退：用 WPF 富文本还原（不再拆 Run，只保证有字）。 */
        FormattedText CreateFallback()
        {
            var ff = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Cormorant Garamond");
            return new FormattedText("G# minor", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(ff, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal),
                BaseSize, Foreground ?? Brushes.Black, null, TextFormattingMode.Ideal, Scale);
        }

        Size MeasureFallback()
        {
            try
            {
                var ft = CreateFallback();
                return new Size(ft.WidthIncludingTrailingWhitespace + PadDip * 2, ft.Height + PadDip * 2);
            }
            catch { return new Size(120, BaseSize * 1.4); }
        }

        void DrawFallback(DrawingContext dc)
        {
            try { dc.DrawText(CreateFallback(), new Point(PadDip, PadDip)); } catch { }
        }
    }
}
