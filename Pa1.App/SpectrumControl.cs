using System;
using System.Windows;
using System.Windows.Media;
using Pa1.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Pa1.App
{
    /// <summary>
    /// 频谱分析屏（SkiaSharp 移植 phosphor-pa1.html canvas 绘制）：
    /// 网格 / 斜纹带 / 160 柱对数频谱 / 峰值保持线 / 刻度 / AWAITING SIGNAL。
    /// </summary>
    public sealed class SpectrumControl : SkiaSharp.Views.WPF.SKElement
    {
        public float[] Disp = new float[AnalyzerMath.BandN];
        public float[] Peak = new float[AnalyzerMath.BandN];
        double[] _lo, _hi, _fc;
        bool _frozen;
        public bool HasContent;
        public string IdleText = "AWAITING SIGNAL";

        /* 规范对数频率轴（对齐 ISO 226 等响曲线图式）：
           次栅格 2~9× 真对数间距（淡），主栅格 100/1K/10K（重），标签取 1-2-5 序列 */
        static readonly (double f, string label, bool major)[] FreqGrid = BuildFreqGrid();

        static (double f, string label, bool major)[] BuildFreqGrid()
        {
            var list = new System.Collections.Generic.List<(double f, string label, bool major)>();
            for (int dec = -1; dec <= 4; dec++)
            {
                for (int m = 1; m <= 9; m++)
                {
                    double f = m * Math.Pow(10, dec);
                    if (f < 20 || f > 24000) continue;
                    string label = (m == 1 || m == 2 || m == 5)
                        ? (f >= 1000 ? (f / 1000).ToString("0.#") + "K" : ((int)f).ToString())
                        : null;
                    list.Add((f, label, m == 1));
                }
            }
            return list.ToArray();
        }

        const double DbCeil = AnalyzerMath.DbCeil;
        static double DbFloor => AnalyzerMath.DbFloor;   /* 运行时三档可切换（-60/-72/-96） */
        const int BandN = AnalyzerMath.BandN;
        const double PadTop = 16, PadRight = 62, PadBottom = 30, PadLeft = 10;

        public SpectrumControl()
        {
            _lo = new double[BandN]; _hi = new double[BandN]; _fc = new double[BandN];
            AnalyzerMath.BuildBands(_lo, _hi, _fc);
            for (int i = 0; i < BandN; i++) { Disp[i] = (float)DbFloor; Peak[i] = (float)DbFloor; }
            PaintSurface += OnPaint;
            /* 逻辑像素渲染 + WPF 放大：200% DPI 下画布面积降 4 倍，帧率瓶颈在像素填充 */
            try { IgnorePixelScaling = true; } catch { }
        }

        public void SetFrozen(bool f) { _frozen = f; }

        double DbToY(double db, double h)
        {
            double t = (db - DbFloor) / (DbCeil - DbFloor);
            return PadTop + (1 - t) * (h - (PadTop + PadBottom));
        }

        /* f(t) = P(u+v ≤ t)：(u,v) 均匀分布于单位像素方块 [0,1]² 时 u+v 的累积分布。
           45° 条纹带 |u+v-δ| ≤ h 与像素的精确相交面积 = f(δ+h) - f(δ-h) */
        static double HatchArea(double t)
        {
            if (t <= 0) return 0;
            if (t >= 2) return 1;
            return t < 1 ? t * t / 2 : 1 - (2 - t) * (2 - t) / 2;
        }

        int _painting;
        void OnPaint(object s, SKPaintSurfaceEventArgs e)
        {
            /* PrintWindow/重绘可能重入渲染回调——单飞保护 */
            if (System.Threading.Interlocked.Exchange(ref _painting, 1) == 1) return;
            try { PaintCore(e); }
            catch { /* 渲染异常不杀进程 */ }
            finally { System.Threading.Interlocked.Exchange(ref _painting, 0); }
        }

        void PaintCore(SKPaintSurfaceEventArgs e)
        {
            var c = e.Surface.Canvas;
            var info = e.Info;
            double W = info.Width, H = info.Height;
            EnsureNativeCache();
            c.Clear(SKColors.Transparent);

            double x0 = PadLeft, areaW = W - (PadLeft + PadRight);
            /* 连续自适应几何：160 根柱始终铺满可用轴宽，宽度随窗口逐像素变化。
               不再量化整数槽位（会在宽度阈值处阶梯跳变）；柱体开启 AA，用亚像素
               覆盖率平滑分数宽度，同时保持每根柱的几何宽度与间隙严格一致。 */
            double step = areaW / BandN;
            double gap = Math.Max(0.65, step * 0.22);
            double bw = Math.Max(0.5, step - gap);
            double barInset = (step - bw) / 2;

            /* 网格：dB 横线 + 对数频率纵栅格（主栅格 100/1K/10K 重线，2~9× 次栅格淡线） */
            foreach (var g in FreqGrid)
            {
                float x = (float)(x0 + AnalyzerMath.FToX(g.f) * areaW);
                c.DrawLine(x, (float)PadTop, x, (float)(H - PadBottom + 4), g.major ? _pGrid0 : _pGrid);
            }
            /* 10dB 主栅格：距基线不足 5dB 的刻度跳过（-72 档时 -70 会与基线挤在一起） */
            for (int db = 0; db > DbFloor + 5; db -= 10)
            {
                double y = DbToY(db, H);
                c.DrawLine((float)x0, (float)y, (float)(x0 + areaW + 4), (float)y, db == 0 ? _pGrid0 : _pGrid);
                c.DrawText((db == 0 ? "-00" : db.ToString()) + "dB", (float)(W - PadRight + 12), (float)(y + 4), _pAxisTx);
            }
            /* 基线（下限档位）：非 10 倍数时单独画线标注（-72/-96）；-60 时与 -60 刻度重合单独画也无碍 */
            {
                double y = DbToY(DbFloor, H);
                c.DrawLine((float)x0, (float)y, (float)(x0 + areaW + 4), (float)y, _pGrid0);
                c.DrawText(((int)DbFloor) + "dB", (float)(W - PadRight + 12), (float)(y + 4), _pAxisTx);
            }

            /* 频率刻度：1-2-5 序列，真对数位置 */
            {
                double axisY = H - PadBottom + 16;
                foreach (var g in FreqGrid)
                {
                    if (g.label == null) continue;
                    double x = x0 + AnalyzerMath.FToX(g.f) * areaW;
                    float w = _pTickTx.MeasureText(g.label);
                    float tx = (float)(x - w / 2);
                    if (tx < x0 - 2) tx = (float)(x0 - 2);          /* 左缘钳制 */
                    if (tx + w > x0 + areaW + 8) continue;          /* 右缘放不下则丢 */
                    c.DrawRect((float)(x - 0.5), (float)(axisY - 13), 1, 6, _pTick);
                    c.DrawText(g.label, tx, (float)axisY, _pTickTx);
                }
            }

            /* 斜纹带（disp 与 peak 之间）：图案着色器一次填充（O(面积) 单遍）。
               位图瓦片相位特意避开瓦片角（条纹中心 x+y=p/2、3p/2 过边中点），
               条纹是超出边界被裁切的填充平行四边形——无端点 AA 圆帽 → 无珠串颗粒感 */
            {
                var path = _hatchPath;
                path.Reset();
                path.MoveTo((float)x0, (float)DbToY(Disp[0], H));
                for (int i = 1; i < BandN; i++) path.LineTo((float)(x0 + step * (i + 0.5)), (float)DbToY(Disp[i], H));
                path.LineTo((float)(x0 + areaW), (float)DbToY(Disp[BandN - 1], H));
                path.LineTo((float)(x0 + areaW), (float)DbToY(Peak[BandN - 1], H));
                for (int i = BandN - 1; i >= 0; i--) path.LineTo((float)(x0 + step * (i + 0.5)), (float)DbToY(Peak[i], H));
                path.LineTo((float)x0, (float)DbToY(Peak[0], H));
                path.Close();
                c.DrawPath(path, _pHatch);
            }

            /* 频谱柱：干净渐变柱体（外扩矩形假辉光已被否掉——柱条旁的色块毫无意义且劣化观感） */
            double baseY = DbToY(DbFloor, H);
            for (int i = 0; i < BandN; i++)
            {
                double y = DbToY(Disp[i], H);
                float h = (float)Math.Max(2, baseY - y);
                float bx = (float)(x0 + step * i + barInset);
                /* 画布矩阵缩放到柱体（单位渐变 (0,0)-(0,1) 随 CTM 映射）——
                   不用 WithLocalMatrix：每帧 160 个临时 SKShader 泄漏曾把帧率拖衰 */
                c.Save();
                c.Translate(bx, (float)y);
                c.Scale((float)bw, h);
                c.DrawRect(0, 0, 1, 1, _pFill);
                c.Restore();
            }

            /* 峰值线 + 峰点 */
            {
                var pl = _peakPath;
                pl.Reset();
                pl.MoveTo((float)x0, (float)DbToY(Peak[0], H));
                for (int i = 1; i < BandN; i++) pl.LineTo((float)(x0 + step * (i + 0.5)), (float)DbToY(Peak[i], H));
                pl.LineTo((float)(x0 + areaW), (float)DbToY(Peak[BandN - 1], H));
                c.DrawPath(pl, _pPeakLine);
                for (int i = 0; i < BandN; i++)
                {
                    float py = (float)DbToY(Peak[i], H);
                    if (py < baseY - 2)
                        c.DrawRect((float)(x0 + step * i + barInset), py, (float)bw, 1.5f, _pPeakDot);
                }
            }

            /* 空闲提示：墨迹中心落在 1kHz 栅格线上（用户定：字样中心与 1k 标识线重合），
               Y 垂直居中绘图区、与电平档位无关。X 用 GetTextBounds 墨迹边界而非
               MeasureText 前进宽度（尾侧边距会把墨迹偏左 ~1DIP）；Y 按字体度量校正。 */
            if (!HasContent)
            {
                var ib = new SKRect();
                _pIdle.MeasureText(IdleText, ref ib);   /* 墨迹边界（含首尾边距） */
                var fm = _pIdle.FontMetrics;
                double cx = x0 + AnalyzerMath.FToX(1000) * areaW, cy = PadTop + (H - (PadTop + PadBottom)) / 2;
                float y = (float)(cy - (fm.Ascent + fm.Descent) / 2);
                c.DrawText(IdleText, (float)(cx - (ib.Left + ib.Right) / 2), y, _pIdle);
            }

            /* HOLD 徽标 */
            if (_frozen && HasContent)
            {
                c.DrawText("HOLD", (float)x0 + 6, (float)(PadTop + 14), _pHold);
            }
        }

        static SKTypeface _mono;
        static SKTypeface Mono()
        {
            if (_mono == null) _mono = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;
            return _mono;
        }

        /* 原生对象缓存：主题变更时重建。每帧 new SKPaint×11 + SKPath×2 曾是 native 分配churn */
        SKShader _unitGrad;
        SKBitmap _hatchTile;
        SKShader _hatchShader;
        SKPaint _pGrid0, _pGrid, _pAxisTx, _pTick, _pTickTx, _pHatch, _pFill, _pPeakLine, _pPeakDot, _pIdle, _pHold;
        SKPath _hatchPath, _peakPath;
        ThemePal _gradTheme;

        void EnsureNativeCache()
        {
            if (_gradTheme == Theme.Cur && _unitGrad != null) return;
            if (_unitGrad != null) _unitGrad.Dispose();
            if (_hatchShader != null) _hatchShader.Dispose();
            if (_hatchTile != null) _hatchTile.Dispose();
            if (_pGrid0 != null) _pGrid0.Dispose();
            if (_pGrid != null) _pGrid.Dispose();
            if (_pAxisTx != null) _pAxisTx.Dispose();
            if (_pTick != null) _pTick.Dispose();
            if (_pTickTx != null) _pTickTx.Dispose();
            if (_pHatch != null) _pHatch.Dispose();
            if (_pFill != null) _pFill.Dispose();
            if (_pPeakLine != null) _pPeakLine.Dispose();
            if (_pPeakDot != null) _pPeakDot.Dispose();
            if (_pIdle != null) _pIdle.Dispose();
            if (_pHold != null) _pHold.Dispose();
            var T = Theme.Cur;
            _unitGrad = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, 1),
                new[] { T.BarTop, T.BarMid, T.BarBot }, null, SKShaderTileMode.Clamp);
            /* 斜纹瓦片 6×6：解析式逐像素覆盖率（单位像素方块与 45° 条纹带的精确相交面积）。
               条纹族 x+y ≡ 3 (mod 6)，覆盖只依赖 (x+y) mod 6 → Repeat 平铺数学上无缝；
               25% 裙边像素填充对角链楼梯缺口 → 无珠串颗粒。
               （旧 DrawLine 栅格化丢 AA 裙边：只剩 ~70% 主像素对角点链、角点相接，
               200% DPI 下呈 2×2 物理块珠串感） */
            _hatchTile = new SKBitmap(6, 6);
            {
                var hc = T.Hatch;
                const double hw = 0.7071;          /* 1px 线宽在 d=x+y 空间的半宽（w·√2/2） */
                for (int y = 0; y < 6; y++)
                    for (int x = 0; x < 6; x++)
                    {
                        double d0 = x + y, cov = 0;
                        for (int k = -1; k <= 1; k++)
                        {
                            double L = 3 + 6 * k;
                            cov += HatchArea(L - d0 + hw) - HatchArea(L - d0 - hw);
                        }
                        if (cov > 1) cov = 1;
                        _hatchTile.SetPixel(x, y, new SKColor(hc.Red, hc.Green, hc.Blue,
                            (byte)Math.Round(hc.Alpha * cov)));
                    }
            }
            _hatchShader = SKShader.CreateBitmap(_hatchTile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            _pGrid0 = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = T.Grid0 };
            _pGrid = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = T.Grid };
            _pAxisTx = new SKPaint { Color = T.AxisText, TextSize = 11, IsAntialias = true, Typeface = Mono() };
            _pTick = new SKPaint { Color = T.Tick };
            _pTickTx = new SKPaint { Color = T.TickText, TextSize = 10, IsAntialias = true, Typeface = Mono() };
            _pHatch = new SKPaint { Style = SKPaintStyle.Fill, Shader = _hatchShader };
            _pFill = new SKPaint { Style = SKPaintStyle.Fill, Shader = _unitGrad, IsAntialias = true };
            _pPeakLine = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = T.PeakLine };
            _pPeakDot = new SKPaint { Color = T.PeakDot, IsAntialias = true };
            _pIdle = new SKPaint { Color = T.Idle, TextSize = 13, IsAntialias = true, Typeface = Mono() };
            _pHold = new SKPaint { Color = T.Hold, TextSize = 11, IsAntialias = true, Typeface = Mono() };
            if (_hatchPath == null) _hatchPath = new SKPath();
            if (_peakPath == null) _peakPath = new SKPath();
            _gradTheme = Theme.Cur;
        }
    }
}
