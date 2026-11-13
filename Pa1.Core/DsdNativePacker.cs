using System;

namespace Pa1.Core
{
    /// <summary>
    /// Native DSD 位流打包：把 DSD 原始位流按 DSD_U32_LE / DSD_U16_LE 容器直排，
    /// 供 WASAPI 独占模式以 WAVEFORMATEXTENSIBLE DSD 子格式送进 DAC（不套 DoP 标记、不转 PCM）。
    ///
    /// 帧布局（每帧每声道 4 或 2 字节，承载 32 / 16 个 DSD 位）：
    ///   容器为无符号整数、小端写入；最早位 t0 置于容器数值最高位（与 DoP payload 时间序一致：
    ///   DoP 的 16 位 payload 即 t0 在 bit15 的同一约定，仅去掉标记换容器）。
    /// 位序：DSF 每字节 LSB-first（t0 在 bit0）→ 字节位反转后入容器；DFF MSB-first → 直取。
    /// 载波率 = DSD 比特率 / 每帧位数（U32：/32；U16：/16）。
    /// 静音用 0x00 填充：一旦设备/驱动不识别 DSD 子格式而按 PCM 解读，0x00 仍是真静音
    /// （DSD 侧只是极短恒定位型，远好于 0x69 被误读成 +82% 直流）。
    ///
    /// 注意：Windows 没有 Microsoft 定义的 Native DSD 传输标准；厂商驱动（Thesycon/XMOS 系）
    /// 的 Native DSD 走 ASIO 私有协议。此处的 WASAPI DSD 子格式属厂商约定，须设备驱动支持，
    /// 探测不支持就不得发送（位流被当 PCM 解读 = 满幅刺耳噪声）。
    /// </summary>
    public static class DsdNativePacker
    {
        /// <summary>U32 容器载波率 = DSD 比特率 / 32。</summary>
        public static int CarrierRateU32(int dsdBitRate) { return dsdBitRate / 32; }

        /// <summary>U16 容器载波率 = DSD 比特率 / 16（与 DoP 载波率相同，仅无标记）。</summary>
        public static int CarrierRateU16(int dsdBitRate) { return dsdBitRate / 16; }

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

        /// <summary>
        /// 打 DSD_U32_LE 帧：平面缓冲（ch-major）前 frames 帧 → dst，每帧每声道 4 字节。
        /// t0 → 容器数值 bit31，数值小端落盘。
        /// </summary>
        public static int Pack32(byte[] dst, byte[] flat, int bytesPerCh, int frames, int channels, bool lsbFirst)
        {
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int p = c * bytesPerCh + f * 4;
                    byte b0 = flat[p], b1 = flat[p + 1], b2 = flat[p + 2], b3 = flat[p + 3];
                    uint w = lsbFirst
                        ? ((uint)Rev[b0] << 24) | ((uint)Rev[b1] << 16) | ((uint)Rev[b2] << 8) | Rev[b3]
                        : ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3;
                    dst[o++] = (byte)(w & 0xFF);
                    dst[o++] = (byte)((w >> 8) & 0xFF);
                    dst[o++] = (byte)((w >> 16) & 0xFF);
                    dst[o++] = (byte)((w >> 24) & 0xFF);
                }
            }
            return frames;
        }

        /// <summary>
        /// 打 DSD_U16_LE 帧：每帧每声道 2 字节。t0 → 容器数值 bit15（与 DoP payload 一致），小端落盘。
        /// </summary>
        public static int Pack16(byte[] dst, byte[] flat, int bytesPerCh, int frames, int channels, bool lsbFirst)
        {
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int p = c * bytesPerCh + f * 2;
                    byte b0 = flat[p], b1 = flat[p + 1];
                    int w = lsbFirst
                        ? (Rev[b0] << 8) | Rev[b1]
                        : (b0 << 8) | b1;
                    dst[o++] = (byte)(w & 0xFF);
                    dst[o++] = (byte)((w >> 8) & 0xFF);
                }
            }
            return frames;
        }

        /// <summary>打 frames 帧全零静音（slotBytes = 4 或 2）写入 dst[offset..]：起播前导/欠载补给。
        /// 0x00 在误按 PCM 解读时是真静音；DSD 侧为极短恒定位型。</summary>
        public static int PackSilence(byte[] dst, int offset, int frames, int channels, int slotBytes)
        {
            int n = frames * channels * slotBytes;
            for (int i = 0; i < n; i++) dst[offset + i] = 0;
            return n;
        }
    }
}
