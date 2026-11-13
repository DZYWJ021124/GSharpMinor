using System;

namespace Pa1.Core
{
    /// <summary>
    /// 原地复数 FFT（radix-2 Cooley-Tukey，N = 2^n）。
    /// 供频谱分析替代 Web Audio AnalyserNode；配 AnalyzerMath.BlackmanWindow/MagnitudesToDb 使用。
    /// </summary>
    public static class Fft
    {
        /// <summary>就地变换 re/im（长度 n，n 须为 2 的幂）。inverse=false 正变换（无归一）。</summary>
        public static void Transform(double[] re, double[] im, bool inverse = false)
        {
            int n = re.Length;
            if (n != im.Length || n < 2 || (n & (n - 1)) != 0)
                throw new ArgumentException("FFT size must be power of two");

            /* 位反转置换 */
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double tr = re[i]; re[i] = re[j]; re[j] = tr;
                    double ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double curRe = 1, curIm = 0;
                    int half = len >> 1;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = i + k + half;
                        double vRe = re[b] * curRe - im[b] * curIm;
                        double vIm = re[b] * curIm + im[b] * curRe;
                        re[b] = re[a] - vRe; im[b] = im[a] - vIm;
                        re[a] += vRe; im[a] += vIm;
                        double nRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nRe;
                    }
                }
            }
        }
    }
}
