using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pa1.App
{
    /// <summary>
    /// 连续曲率圆角矩形（squircle / 超椭圆）几何。
    /// WPF CornerRadius 是圆弧，弧与直边交界处曲率突变（仅 G1 连续）；
    /// 本几何角部采用超椭圆 |x|^n + |y|^n = r^n（n≈4.6），曲率沿轮廓连续过渡，
    /// 即 iOS / Figma squircle 的连续曲率观感。
    /// </summary>
    public static class Squircle
    {
        const double N = 4.6;         // 超椭圆指数：2=圆弧；≥4 起呈连续曲率观感
        const int Seg = 20;           // 每角采样段数（200% 缩放下每段 ≪1px，肉眼平滑）

        /// <summary>构建 w×h、角半径 r 的连续曲率圆角矩形几何（原点 0,0）。
        /// topOnly=true：仅上两角圆角（贴窗口顶栏带），下两角方角。</summary>
        public static Geometry Create(double w, double h, double r, bool topOnly = false)
        {
            if (w <= 0 || h <= 0) return Geometry.Empty;
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
            if (r < 0.5) return new RectangleGeometry(new Rect(0, 0, w, h));

            double x1 = w, y1 = h;
            var g = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(x1 - r, 0), true, false);
                Corner(c, x1 - r, r, r, 0);          // 右上（超椭圆）
                if (topOnly)
                {
                    c.LineTo(new Point(x1, y1), true, false);   // 右边直落底
                    c.LineTo(new Point(0, y1), true, false);    // 底边方角
                    c.LineTo(new Point(0, r), true, false);     // 左边上行
                }
                else
                {
                    c.LineTo(new Point(x1, y1 - r), true, false);
                    Corner(c, x1 - r, y1 - r, r, 1);     // 右下
                    c.LineTo(new Point(r, y1), true, false);
                    Corner(c, r, y1 - r, r, 2);          // 左下
                    c.LineTo(new Point(0, r), true, false);
                }
                Corner(c, r, r, r, 3);               // 左上（超椭圆）
            }
            g.Freeze();
            return g;
        }

        /// <summary>超椭圆角部采样（顺时针行进，t: 0→π/2）；q: 0=TR 1=BR 2=BL 3=TL。</summary>
        static void Corner(StreamGeometryContext c, double cx, double cy, double r, int q)
        {
            double e = 2.0 / N;
            for (int i = 1; i <= Seg; i++)
            {
                double t = (Math.PI / 2) * i / Seg;
                double s = Math.Pow(Math.Sin(t), e);   // sin^(2/n)
                double co = Math.Pow(Math.Cos(t), e);  // cos^(2/n)
                Point p;
                switch (q)
                {
                    case 0: p = new Point(cx + r * s, cy - r * co); break;
                    case 1: p = new Point(cx + r * co, cy + r * s); break;
                    case 2: p = new Point(cx - r * s, cy + r * co); break;
                    default: p = new Point(cx - r * co, cy - r * s); break;
                }
                c.LineTo(p, true, false);
            }
        }
    }

    /// <summary>
    /// 连续曲率圆角矩形边框：Drop-in 替代 Border + CornerRadius（圆弧）。
    /// XAML 直接沿用 CornerRadius/BorderThickness/Background/BorderBrush 属性，
    /// 角部改按超椭圆（Squircle）绘制，曲率连续。
    /// </summary>
    public class SquircleBorder : Border
    {
        /// <summary>仅上两角圆角（顶栏带贴窗口上缘时保持 squircle 角），下方方角。</summary>
        public static readonly DependencyProperty TopOnlyProperty =
            DependencyProperty.Register("TopOnly", typeof(bool), typeof(SquircleBorder),
                new PropertyMetadata(false));
        public bool TopOnly
        {
            get { return (bool)GetValue(TopOnlyProperty); }
            set { SetValue(TopOnlyProperty, value); }
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            /* TopOnly 时裁剪子元素到带形：窗口按钮贴右上角时其透明底角不越出 squircle */
            if (TopOnly && arrangeSize.Width > 0 && arrangeSize.Height > 0)
                Clip = Squircle.Create(arrangeSize.Width, arrangeSize.Height, CornerRadius.TopLeft, true);
            return base.ArrangeOverride(arrangeSize);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double r = CornerRadius.TopLeft;               // XAML 均匀圆角
            double bt = BorderThickness.Left;              // 均匀描边
            if (Background != null)
                dc.DrawGeometry(Background, null, Squircle.Create(w, h, r, TopOnly));

            if (BorderBrush != null && bt > 0)
            {
                /* 描边居中于轮廓：几何内缩 bt/2 */
                double inset = bt / 2;
                dc.PushTransform(new TranslateTransform(inset, inset));
                dc.DrawGeometry(null, new Pen(BorderBrush, bt) { Brush = BorderBrush },
                    Squircle.Create(w - bt, h - bt, Math.Max(0, r - inset)));
                dc.Pop();
            }
        }
    }
}
