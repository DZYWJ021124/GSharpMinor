using System;
using System.Collections.Generic;

namespace Pa1.Core
{
    /// <summary>
    /// EBU R128 / ITU-R BS.1770-4 响度测量：K 加权（高架预滤波 + RLB 高通）→ 400ms 窗
    /// 75% 重叠（100ms 步长）→ 绝对门限 -70 LUFS + 相对门限 -10 LU → 积分 LUFS；同时计采样峰值。
    /// 48k 系数与 BS.1770-4 发布常数一致（回归有锚点断言）；任意采样率按同原型公式现算。
    /// 用途：响度归一化增益 = NormalizeGainDb(lufs, peakDb, targetLufs)。
    /// </summary>
    public sealed class LoudnessMeter
    {
        public const double GateAbsolute = -70.0;       /* LUFS 绝对门限 */
        public const double RelGateOffset = -10.0;      /* 相对门限：无门功率均值 -10 LU */
        public const double MeterOffset = -0.691;       /* BS.1770-4 常数 */

        readonly int _rate, _ch;
        readonly int _stepSamples;                      /* 100ms 步长（帧数） */
        readonly Biquad[] _sh, _hp;                     /* 每声道 K 加权两级 */
        readonly double[] _stepSum;                     /* 当前步各声道平方和 */
        int _stepFill;
        readonly List<double> _stepPow = new List<double>();   /* 每 100ms 步的 Σ 声道均方（功率分子） */
        double _peakLin = 0;
        bool _finished;

        public LoudnessMeter(int sampleRate, int channels)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            _rate = sampleRate;
            _ch = Math.Max(1, channels);
            _stepSamples = Math.Max(1, (int)((long)sampleRate / 10));
            _stepSum = new double[_ch];
            var pre = KPreCoeffs(sampleRate);
            var rlb = KRlbCoeffs(sampleRate);
            _sh = new Biquad[_ch];
            _hp = new Biquad[_ch];
            for (int c = 0; c < _ch; c++)
            {
                _sh[c] = FromCoeffs(pre);
                _hp[c] = FromCoeffs(rlb);
            }
        }

        /// <summary>BS.1770-4 高架预滤波（f0=1681.97Hz，G=+3.99984dB，Q=0.7071752）→ {b0,b1,b2,a1,a2}。</summary>
        public static double[] KPreCoeffs(int fs)
        {
            const double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196;
            double K = Math.Tan(Math.PI * f0 / fs);
            double Vh = Math.Pow(10.0, G / 20.0), Vb = Math.Pow(Vh, 0.4996667741545416);
            double a0 = 1.0 + K / Q + K * K;
            return new double[]
            {
                (Vh + Vb * K / Q + K * K) / a0,
                2.0 * (K * K - Vh) / a0,
                (Vh - Vb * K / Q + K * K) / a0,
                2.0 * (K * K - 1.0) / a0,
                (1.0 - K / Q + K * K) / a0,
            };
        }

        /// <summary>BS.1770-4 RLB 高通（f0=38.135Hz，Q=0.500327）→ {b0,b1,b2,a1,a2}（b 固定 1,-2,1）。</summary>
        public static double[] KRlbCoeffs(int fs)
        {
            const double f0 = 38.13547087602444, Q = 0.5003270373238773;
            double K = Math.Tan(Math.PI * f0 / fs);
            double a0 = 1.0 + K / Q + K * K;
            return new double[]
            {
                1.0, -2.0, 1.0,
                2.0 * (K * K - 1.0) / a0,
                (1.0 - K / Q + K * K) / a0,
            };
        }

        struct Biquad
        {
            public double B0, B1, B2, A1, A2, Z1, Z2;
            public double Step(double x)
            {
                double y = B0 * x + Z1;
                Z1 = B1 * x - A1 * y + Z2;
                Z2 = B2 * x - A2 * y;
                return y;
            }
        }

        static Biquad FromCoeffs(double[] c)
        {
            return new Biquad { B0 = c[0], B1 = c[1], B2 = c[2], A1 = c[3], A2 = c[4] };
        }

        /// <summary>喂交错采样：data[offset..offset+frames*ch)。块大小任意，跨调用状态连续。</summary>
        public void Process(float[] data, int offset, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                int baseI = offset + f * _ch;
                for (int c = 0; c < _ch; c++)
                {
                    double x = data[baseI + c];
                    double a = x < 0 ? -x : x;
                    if (a > _peakLin) _peakLin = a;
                    double y = _hp[c].Step(_sh[c].Step(x));
                    _stepSum[c] += y * y;
                }
                if (++_stepFill >= _stepSamples) CloseStep();
            }
        }

        void CloseStep()
        {
            double pow = 0;
            for (int c = 0; c < _ch; c++) pow += _stepSum[c] / _stepFill;
            _stepPow.Add(pow);
            for (int c = 0; c < _ch; c++) _stepSum[c] = 0;
            _stepFill = 0;
        }

        /// <summary>收尾（残步并入）并执行双重门限。之后读 IntegratedLufs / PeakDb。</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_stepFill > 0) CloseStep();
        }

        static double PowToLoudness(double pow)
        {
            return pow > 0 ? MeterOffset + 10.0 * Math.Log10(pow) : double.NegativeInfinity;
        }

        /// <summary>积分响度（LUFS）。Finish 后有效；无有效窗返回 -120。</summary>
        public double IntegratedLufs
        {
            get
            {
                int n = _stepPow.Count;
                if (n == 0) return -120;
                /* 400ms 窗、75% 重叠（步长 100ms）；不足 4 步的短素材整段一窗 */
                var win = new List<double>();
                if (n >= 4)
                    for (int i = 0; i + 4 <= n; i++)
                        win.Add((_stepPow[i] + _stepPow[i + 1] + _stepPow[i + 2] + _stepPow[i + 3]) / 4.0);
                else
                {
                    double s = 0;
                    for (int i = 0; i < n; i++) s += _stepPow[i];
                    win.Add(s / n);
                }
                /* 绝对门限 */
                var pass = new List<double>();
                foreach (var p in win)
                {
                    double l = PowToLoudness(p);
                    if (l > GateAbsolute) pass.Add(p);
                }
                if (pass.Count == 0) return GateAbsolute;
                /* 相对门限：无门功率均值 -10 LU 以下的窗剔除 */
                double meanPow = 0;
                foreach (var p in pass) meanPow += p;
                meanPow /= pass.Count;
                double relGate = PowToLoudness(meanPow) + RelGateOffset;
                double sum = 0; int cnt = 0;
                foreach (var p in pass)
                {
                    if (PowToLoudness(p) > relGate) { sum += p; cnt++; }
                }
                if (cnt == 0) return GateAbsolute;
                return PowToLoudness(sum / cnt);
            }
        }

        /// <summary>采样峰值（dBFS）。Finish 前后皆可读。</summary>
        public double PeakDb
        {
            get { return _peakLin > 0 ? 20.0 * Math.Log10(_peakLin) : -120; }
        }

        /// <summary>
        /// 归一化增益（dB）：target − lufs，按峰值钳制防削波（applied ≤ −peakDb，峰值恰到 0dBFS）；
        /// 无峰值信息时 sanity 上限 +12dB。
        /// </summary>
        public static double NormalizeGainDb(double lufs, double peakDb, double targetLufs)
        {
            double g = targetLufs - lufs;
            if (!double.IsNaN(peakDb) && !double.IsInfinity(peakDb))
            {
                double maxG = -peakDb;
                if (g > maxG) g = maxG;
            }
            if (g > 12) g = 12;
            return g;
        }
    }
}
