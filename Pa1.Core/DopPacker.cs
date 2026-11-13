using System;

namespace Pa1.Core
{
    /// <summary>
    /// DoP 1.1（DSD over PCM）打包：把 DSD 原始位流封装进 24-bit PCM 载波帧，
    /// 供 WASAPI 独占模式 bit-perfect 送进 DAC（DAC 检测标记交替后切 DSD 硬解，不再降采样）。
    ///
    /// 帧布局（DoP Open Standard v1.1，每帧每声道 3 字节，24-bit 小端）：
    ///   线上字节 = [payload 低字节][payload 高字节][marker]
    ///   payload 位 15 = 本帧最早 DSD 位 t0，位 0 = 最晚 t15（t0 紧邻 marker 侧）
    ///   marker = 0x05 / 0xFA 帧间交替，同帧各声道相同
    /// 位序：DSF 每字节 LSB-first（t0 在 bit0）→ 字节位反转；DFF MSB-first → 直取。
    /// 载波率 = DSD 比特率 / 16（DSD64→176.4k、DSD128→352.8k、DSD256→705.6k）。
    /// DoP 要求 bit-perfect：链路上不得有音量/重采样/任何样本改动。
    /// </summary>
    public static class DopPacker
    {
        public const byte MarkerA = 0x05;
        public const byte MarkerB = 0xFA;

        /// <summary>载波 PCM 率 = DSD 比特率 / 16。</summary>
        public static int CarrierRate(int dsdBitRate) { return dsdBitRate / 16; }

        static readonly byte[] Rev = BuildRev();

        static byte[] BuildRev()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int r = 0;
                for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) r |= 1 << (7 - b);
                t[i] = (byte)r;
            }
            return t;
        }

        /// <summary>字节位反转（DSF LSB-first → DoP 时间序）。</summary>
        public static byte Rev8(byte b) { return Rev[b]; }

        /// <summary>
        /// 把平面缓冲（ch-major：c0 的 bytesPerCh 字节 ‖ c1 的 bytesPerCh 字节 …）的前 frames 帧
        /// 打成 DoP 交错帧写入 dst（每帧 frames*Channels*3 字节）。markerPhase 逐帧翻转并回写。
        /// </summary>
        public static int Pack(byte[] dst, byte[] flat, int bytesPerCh, int frames, int channels, bool lsbFirst, ref bool markerPhase)
        {
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                byte marker = markerPhase ? MarkerB : MarkerA;
                for (int c = 0; c < channels; c++)
                {
                    int p = c * bytesPerCh + f * 2;
                    byte b0 = flat[p], b1 = flat[p + 1];
                    /* t0 → payload 位 15：DSF 反转后置于高字节；DFF（t0 在 bit7）直取即高字节 */
                    int w = lsbFirst
                        ? (Rev[b0] << 8) | Rev[b1]
                        : (b0 << 8) | b1;
                    dst[o++] = (byte)(w & 0xFF);
                    dst[o++] = (byte)((w >> 8) & 0xFF);
                    dst[o++] = marker;
                }
                markerPhase = !markerPhase;
            }
            return frames;
        }

        /// <summary>打 frames 帧 DSD 静音（0x69 位型）写入 dst[offset..]：起播前导让 DAC 无声期锁定
        /// DoP 标记（防切 DSD 爆音）、欠载补给保流连续。marker 严格交替且跨调用相位连续。</summary>
        public static int PackSilence(byte[] dst, int offset, int frames, int channels, ref bool markerPhase)
        {
            int o = offset;
            for (int f = 0; f < frames; f++)
            {
                byte marker = markerPhase ? MarkerB : MarkerA;
                for (int c = 0; c < channels; c++)
                {
                    dst[o++] = 0x69; dst[o++] = 0x69; dst[o++] = marker;
                }
                markerPhase = !markerPhase;
            }
            return o - offset;
        }
    }
}
