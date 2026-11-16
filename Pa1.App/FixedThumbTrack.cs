using System.Windows;
using System.Windows.Controls.Primitives;

namespace Pa1.App
{
    /// <summary>
    /// Track 变体：ViewportSize 的有效值恒强制 NaN，走 Slider 的定长 thumb 路径。
    ///
    /// 背景：ScrollBar 在 OnPreApplyTemplate 里把视口尺寸绑给 PART_Track
    /// （BindToTemplatedParent），Track 便走 ComputeScrollBarLengths，按
    /// 「视口/内容」比例算 thumb 长度——内容多时槽位只有 ~13px，而该路径完全
    /// 不看 Thumb.MinHeight：26px 的 thumb 被塞进 13px 槽位，遭 WPF 布局
    /// 裁剪成「上圆下平」的形变。
    ///
    /// 这里把 ViewportSize coerce 成 NaN（无视绑定/外部赋值），Track 只走
    /// ComputeSliderLengths：thumb 长度 = Thumb.DesiredSize（即 MinHeight/
    /// MinWidth 规定的原始高度 26px）——直接采用原始高度，永不裁剪。
    /// Value→位置映射两条路径同算法，拖拽/翻页行为不受影响。
    /// </summary>
    public sealed class FixedThumbTrack : Track
    {
        static FixedThumbTrack()
        {
            ViewportSizeProperty.OverrideMetadata(
                typeof(FixedThumbTrack),
                new FrameworkPropertyMetadata(
                    double.NaN,
                    FrameworkPropertyMetadataOptions.AffectsArrange,
                    null,
                    (d, baseValue) => double.NaN));   /* 恒 NaN：无视 ScrollBar 推送的视口值 */
        }
    }
}
