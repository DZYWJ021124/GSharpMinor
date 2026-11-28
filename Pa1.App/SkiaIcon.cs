using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Pa1.App
{
    /// <summary>窗口控制图标（最小化 / 最大化 / 还原 / 关闭）：Skia 按设备像素画成位图。
    ///
    /// 之前用 WPF Path：StrokeThickness=1.2 在 200% 下是 2.4 物理像素，跨在像素边界上，
    /// 抗锯齿把每个笔画糊成 3 列灰——三个图标的视觉重量因此不一致（最小化那根横线看着最细，
    /// 关闭的斜线看着最粗）。这里把线宽取整成整物理像素、把轴对齐的线中心吸附到像素格，
    /// 三个图标严格同画布 10×10、同线宽、同像素栅格，视觉大小才真正统一。</summary>
    internal sealed class SkiaIcon : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            "Kind", typeof(string), typeof(SkiaIcon),
            new FrameworkPropertyMetadata("min",
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            "Foreground", typeof(Brush), typeof(SkiaIcon),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

        public static readonly DependencyProperty BoxSizeProperty = DependencyProperty.Register(
            "BoxSize", typeof(double), typeof(SkiaIcon),
            new FrameworkPropertyMetadata(10.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

        public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
            "StrokeWidth", typeof(double), typeof(SkiaIcon),
            new FrameworkPropertyMetadata(1.0,
                FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

        public string Kind
        {
            get { return (string)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public double BoxSize
        {
            get { return (double)GetValue(BoxSizeProperty); }
            set { SetValue(BoxSizeProperty, value); }
        }

        public double StrokeWidth
        {
            get { return (double)GetValue(StrokeWidthProperty); }
            set { SetValue(StrokeWidthProperty, value); }
        }

        BitmapSource _bitmap;
        string _sig;
        double _scaleOverride;
        bool _hasScaleOverride;

        static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ic = (SkiaIcon)d;
            ic._sig = null;
            ic.InvalidateMeasure();
            ic.InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            _scaleOverride = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0;
            _hasScaleOverride = true;
            _sig = null;
            InvalidateMeasure();
            InvalidateVisual();
            base.OnDpiChanged(oldDpi, newDpi);
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

        protected override Size MeasureOverride(Size availableSize)
        {
            double box = BoxSize > 0 ? BoxSize : 10.0;
            EnsureBitmap();
            return new Size(box, box);
        }

        protected override void OnRender(DrawingContext dc)
        {
            EnsureBitmap();
            double box = BoxSize > 0 ? BoxSize : 10.0;
            if (_bitmap != null) dc.DrawImage(_bitmap, new Rect(0, 0, box, box));
        }

        /// <summary>轴对齐笔画吸附：偶数线宽落在整数像素中心，奇数线宽落在半像素中心。</summary>
        static float Snap(float px, int strokePx)
        {
            return (strokePx & 1) == 0 ? (float)Math.Round(px) : (float)(Math.Floor(px) + 0.5);
        }

        void EnsureBitmap()
        {
            double scale = Scale;
            double box = BoxSize > 0 ? BoxSize : 10.0;
            string kind = Kind ?? "min";
            Color color = BrushColor(Foreground);
            double sw = StrokeWidth > 0 ? StrokeWidth : 1.0;
            string sig = kind + "|" + box.ToString("R") + "|" + color + "|" + sw.ToString("R") + "|" + scale.ToString("R");
            if (_sig == sig && _bitmap != null) return;
            _sig = sig;
            _bitmap = null;

            try
            {
                int pw = (int)Math.Round(box * scale);
                int ph = pw;
                if (pw <= 0) return;
                int strokePx = Math.Max(1, (int)Math.Round(sw * scale));

                using (var bmp = new SKBitmap(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(bmp))
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = strokePx;
                    paint.StrokeCap = SKStrokeCap.Butt;
                    paint.StrokeJoin = SKStrokeJoin.Miter;
                    paint.Color = new SKColor(color.R, color.G, color.B, color.A);

                    canvas.Clear(SKColors.Transparent);

                    float lo = Snap(1f * (float)scale, strokePx);
                    float hi = Snap(9f * (float)scale, strokePx);
                    float mid = Snap(5f * (float)scale, strokePx);

                    switch (kind)
                    {
                        case "max":
                            canvas.DrawRect(lo, lo, hi - lo, hi - lo, paint);
                            break;
                        case "restore":
                            /* 前景小矩形（左下全画）+ 背景矩形的顶/右边（右下角止于前景右上角），
                               与 Win11 还原图标同构：两个完整矩形叠画会糊成一团。 */
                            canvas.DrawRect(lo, lo + 2 * (float)scale, 6 * (float)scale, 6 * (float)scale, paint);
                            using (var back = new SKPath())
                            {
                                back.MoveTo(lo + 2 * (float)scale, lo);
                                back.LineTo(hi, lo);
                                back.LineTo(hi, lo + 6 * (float)scale);
                                back.LineTo(lo + 6 * (float)scale, lo + 6 * (float)scale);
                                canvas.DrawPath(back, paint);
                            }
                            break;
                        case "close":
                            canvas.DrawLine(lo, lo, hi, hi, paint);
                            canvas.DrawLine(hi, lo, lo, hi, paint);
                            break;
                        default: /* min */
                            canvas.DrawLine(lo, mid, hi, mid, paint);
                            break;
                    }

                    _bitmap = ToBitmapSource(bmp, scale);
                }
            }
            catch { _bitmap = null; }
        }

        static Color BrushColor(Brush brush)
        {
            var sb = brush as SolidColorBrush;
            return sb != null ? sb.Color : Colors.Black;
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
    }
}
