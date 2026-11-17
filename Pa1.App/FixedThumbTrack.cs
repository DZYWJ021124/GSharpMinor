using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Pa1.App
{
    /// <summary>
    /// Track 变体：thumb「最矮 26px，内容少按视口比例变长」。
    ///
    /// 背景：ScrollBar 在 OnPreApplyTemplate 里把视口尺寸绑给 PART_Track
    /// （BindToTemplatedParent），Track 便走 ComputeScrollBarLengths，按
    /// 「视口/内容」比例算 thumb 槽位——内容多时槽位只有 ~13px，而该路径完全
    /// 不看 Thumb.MinHeight：26px 的 thumb 被塞进 13px 槽位，遭 WPF 布局
    /// 裁剪成「上圆下平」的形变。
    ///
    /// 做法：ViewportSize 的有效值恒 coerce 成 NaN（真实视口另存 _realViewport），
    /// Track 走 ComputeSliderLengths：thumb 长度 = Thumb.DesiredSize。把
    /// clamp(轨道长 × 视口/内容, 26, 轨道长) 写进 Thumb.Height/Width——
    /// 内容多时定格在最矮 26px，内容少时按比例变长；槽位即 thumb 自身尺寸，
    /// 任何长度都不存在「元素大于槽位」的布局裁剪。
    /// 内容总长 = (Maximum - Minimum) + ViewportSize（ScrollBar 语义：
    /// Maximum-Minimum = 内容长 - 视口长）。Value→位置映射两条路径同算法，
    /// 比例 thumb 下与原生滚动条逐像素一致（offset = trackLen × value/extent）。
    /// </summary>
    public sealed class FixedThumbTrack : Track
    {
        double _realViewport = double.NaN;   /* coerce 前的真实视口尺寸 */

        static FixedThumbTrack()
        {
            ViewportSizeProperty.OverrideMetadata(
                typeof(FixedThumbTrack),
                new FrameworkPropertyMetadata(
                    double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsArrange,
                    null,
                    (d, baseValue) =>
                    {
                        var t = (FixedThumbTrack)d;
                        t._realViewport = baseValue is double dv ? dv : double.NaN;
                        t.SyncThumb();
                        return double.NaN;
                    }));
        }

        public FixedThumbTrack()
        {
            SizeChanged += (_, __) => SyncThumb();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == MaximumProperty || e.Property == MinimumProperty
                || e.Property == OrientationProperty)
                SyncThumb();
        }

        void SyncThumb()
        {
            var thumb = Thumb;
            if (thumb == null) return;
            bool vert = Orientation == WpfOrientation.Vertical;
            double trackLen = vert ? ActualHeight : ActualWidth;
            if (trackLen <= 0) return;                     /* 尺寸未定，等 SizeChanged */

            double vp = _realViewport;
            double len;
            if (double.IsNaN(vp) || !(vp > 0))
                len = 26;                                  /* 视口未知：最矮 26 */
            else
            {
                double extent = (Maximum - Minimum) + vp;  /* 内容总长 */
                len = extent > 0 ? Math.Min(1.0, vp / extent) * trackLen : trackLen;
            }
            len = Math.Max(26, len);                       /* 最矮 26 */
            len = Math.Min(trackLen, len);                 /* 轨道不足 26 时随轨道 */

            if (vert)
            {
                if (double.IsNaN(thumb.Height) || Math.Abs(thumb.Height - len) > 0.5)
                    thumb.Height = len;
            }
            else
            {
                if (double.IsNaN(thumb.Width) || Math.Abs(thumb.Width - len) > 0.5)
                    thumb.Width = len;
            }
        }
    }
}
