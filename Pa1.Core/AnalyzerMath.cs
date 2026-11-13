using System;

namespace Pa1.Core
{
    /// <summary>
    /// 频谱分析数学（与 phosphor-pa1.html 对齐）：
    ///   20 Hz – 24 kHz 对数轴 × 160 频段；DB_CEIL=0，DB_FLOOR 运行时可切换（-60/-72/-96）；
    ///   频段取值 max（段内最大 bin + 抛物线峰值插值，对齐 foobar 实测）/ sum / avg；
    ///   窄带（不足 2 bin）用插值谱值。
    /// 输入 freqData 为每 bin 的 dB 值（Web Audio getFloatFrequencyData 语义：
    ///   20·log10(|X[k]|/N)，已含 Blackman 窗 α=0.16）。
    /// </summary>
    public static class AnalyzerMath
    {
        /// <summary>频谱显示下限（dB）。运行时三档切换：-60 / -72 / -96（16bit 理论底噪）。</summary>
        public static double DbFloor = -96;
        public const double DbCeil = 0;
        public const int FftSize = 8192;
        public const int BandN = 160;
        public const int FMax = 24000, FMin = 20;

        /// <summary>窗补偿：0 dBFS 满幅正弦经 Blackman（CG=0.42）+ |X|/N 归一 → −13.5556 dB，补 +13.5556 对齐 dBFS。</summary>
        public const double DbWindowComp = 13.5556;  // 20*log10(2/0.42)

        public static readonly double LogSpan = Math.Log10((double)FMax / FMin);

        /// <summary>f → 0..1（对数轴）。</summary>
        public static double FToX(double f)
        {
            return Math.Log10(Math.Max(FMin, f) / (double)FMin) / LogSpan;
        }

        /// <summary>0..1 → f。</summary>
        public static double XToF(double t)
        {
            return FMin * Math.Pow((double)FMax / FMin, t);
        }

        /// <summary>对数频段表：20 Hz … 24 kHz（x 均匀 → 柱等宽）。</summary>
        public static void BuildBands(double[] loOut, double[] hiOut, double[] fOut)
        {
            for (int k = 0; k < BandN; k++)
            {
                loOut[k] = XToF(k / (double)BandN);
                hiOut[k] = XToF((k + 1) / (double)BandN);
                fOut[k] = XToF((k + 0.5) / (double)BandN);
            }
        }

        public static double DbToPow(double db) { return Math.Pow(10, db / 10); }
        public static double PowToDb(double p) { return 10 * Math.Log10(p + 1e-20); }

        /// <summary>分数 bin 插值功率。</summary>
        public static double BinPow(float[] freqData, double b)
        {
            int n = freqData.Length;
            if (b <= 1) return DbToPow(freqData[1]);
            if (b >= n - 1) return DbToPow(freqData[n - 1]);
            int i = (int)Math.Floor(b);
            double f = b - i;
            return DbToPow(freqData[i]) * (1 - f) + DbToPow(freqData[i + 1]) * f;
        }

        /// <summary>
        /// 频段取值。bandMode: 'max' | 'sum' | 'avg'。
        /// lo/hi 为分数 bin 边界；off 为校准偏移（dB）。
        /// </summary>
        public static double BandValue(float[] freqData, double lo, double hi, string bandMode, double off)
        {
            int n = freqData.Length;
            double width = hi - lo;

            /* 窄带（不足 2 bin）：插值谱值 —— 对齐 foobar "smooth lower frequencies" */
            if (width < 2)
                return PowToDb(BinPow(freqData, (lo + hi) * 0.5)) + off;

            if (bandMode == "max")
            {
                double max = double.NegativeInfinity;
                int maxK = -1;
                int s = (int)Math.Ceiling(lo), e = (int)Math.Floor(hi);
                for (int k = s; k <= e; k++)
                {
                    if (k >= 1 && k < n && freqData[k] > max) { max = freqData[k]; maxK = k; }
                }
                /* 抛物线峰值插值：恢复栅栏效应损失 */
                if (maxK > 1 && maxK < n - 1)
                {
                    double pa = DbToPow(freqData[maxK - 1]);
                    double pb = DbToPow(freqData[maxK]);
                    double pc = DbToPow(freqData[maxK + 1]);
                    double den = pa - 2 * pb + pc;
                    if (den < 0)
                    {
                        double p = 0.5 * (pa - pc) / den;
                        if (p > -1 && p < 1)
                        {
                            double pk = pb - 0.25 * (pa - pc) * p;
                            if (pk > 0 && PowToDb(pk) > max) max = PowToDb(pk);
                        }
                    }
                }
                double mid = PowToDb(BinPow(freqData, (lo + hi) * 0.5));
                if (mid > max) max = mid;
                return max + off;
            }

            /* 线性功率域按 bin 覆盖宽度加权求和（bandpower），再转回 dB */
            double sum = 0;
            int i0 = Math.Max(1, (int)Math.Floor(lo));
            int i1 = Math.Min(n - 2, (int)Math.Ceiling(hi));
            for (int i = i0; i <= i1; i++)
            {
                double ov = Math.Min(hi, i + 1) - Math.Max(lo, i);
                if (ov > 0) sum += DbToPow(freqData[i]) * ov;
            }
            return PowToDb(bandMode == "avg" ? sum / width : sum) + off;
        }

        /// <summary>Blackman 窗（α=0.16，与 Web Audio AnalyserNode 一致）。CG = 0.42。</summary>
        public static double[] BlackmanWindow(int n)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double r = 2.0 * i / (n - 1) - 1;
                w[i] = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (n - 1));
            }
            return w;
        }

        /// <summary>
        /// 幅度谱 → dB（Web Audio getFloatFrequencyData 语义：20·log10(|X[k]|/N)）。
        /// re/im 为加窗后时域信号的 FFT 结果（长度 N，re/im 各 N）。
        /// </summary>
        public static void MagnitudesToDb(float[] re, float[] im, float[] dbOut)
        {
            int n = re.Length;
            double inv = 1.0 / n;
            for (int k = 0; k < n; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * inv;
                dbOut[k] = (float)(20 * Math.Log10(mag + 1e-20));
            }
        }
    }
}
