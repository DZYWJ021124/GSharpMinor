using System;

namespace Pa1.Core
{
    /// <summary>硬件判定降档：探测设备输出率，决定 NATIVE / FULL SPEC / DOWN。</summary>
    public sealed class OutputPlan
    {
        public int Rate;
        public int Bits;
        public string Mode;   // 'native' | 'full' | 'downsample'

        public string Label
        {
            get
            {
                string prefix = Mode == "downsample" ? "DOWN · "
                    : Mode == "native" ? "NATIVE · "
                    : Mode == "dop" ? "DoP · "
                    : Mode == "nativedsd" ? "NATIVE DSD · "
                    : "FULL SPEC · ";
                return prefix + FormatProbe.KhzLabel(Rate) + " · " + Bits + "-bit";
            }
        }
    }

    public static class OutputPlanner
    {
        /// <summary>44.1 族 / 48 族归属判定。</summary>
        public static int FamilyRate(int r)
        {
            double q44 = r / 44100.0, q48 = r / 48000.0;
            return Math.Abs(q48 - Math.Round(q48)) < Math.Abs(q44 - Math.Round(q44)) ? 48000 : 44100;
        }

        /// <summary>
        /// 规划输出链路：
        ///   r ≤ hwRate → 原生/全规格不降档（r == hwRate 记 'native'，否则 'full'）；
        ///   否则降档 familyRate（44.1 族 → 44100/16，48 族 → 48000/16，且降档目标也须 ≤ hwRate 族内适配）。
        /// DSD 同规则：原生 DSD64 → 176.4k/24、DSD128/256 → 352.8k/24，降档 → 44.1k/16。
        /// hwRate = 0 表示未知（按不降档处理）。
        /// </summary>
        public static OutputPlan Plan(TrackSpec spec, int hwRate, int hwBits)
        {
            int r, bits;
            if (spec != null && spec.Dsd)
            {
                r = DsdDecoder.OutRate(spec.Rate);
                bits = 24;
            }
            else
            {
                r = spec != null && spec.Rate > 0 ? spec.Rate : 48000;
                bits = spec != null && spec.Bits > 0 ? spec.Bits : 16;
            }

            if (hwRate <= 0 || r <= hwRate)
                return new OutputPlan { Rate = r, Bits = bits, Mode = (hwRate > 0 && r == hwRate) ? "native" : "full" };

            int down = FamilyRate(r);
            if (hwRate > 0 && down > hwRate) down = FamilyRate(hwRate);
            return new OutputPlan { Rate = down, Bits = 16, Mode = "downsample" };
        }

        /// <summary>DoP 载波率安全表（历史门槛，现仅作速率参考）：DSD64/128 = 176.4/352.8 kHz 稳。
        /// DSD256 = 705.6 kHz 载波曾在此门槛下被拦去 PCM；现按「不降档」全速率直发（LoadFile 不再
        /// 调用本方法），设备是否真锁定 705.6k 由 DopSupported 探测 + 实听（DAC 指示灯）确认。</summary>
        public static bool IsSafeDopCarrier(int dsdBitRate)
        {
            return DopPacker.CarrierRate(dsdBitRate) <= 352800;
        }

        /// <summary>DoP 规划：24-bit 载波，率 = DSD 比特率/16（位流直通，不降采样）。
        /// 仅当独占模式探测到设备支持该载波率时使用；共享模式混音器会破坏标记，不得走 DoP。</summary>
        public static OutputPlan PlanDop(int dsdBitRate)
        {
            return new OutputPlan { Rate = DopPacker.CarrierRate(dsdBitRate), Bits = 24, Mode = "dop" };
        }

        /// <summary>Native DSD 规划：位流按 DSD_U32_LE（slotBits=32，率 = 比特率/32）或
        /// DSD_U16_LE（slotBits=16，率 = 比特率/16）直排，不套 DoP 标记。
        /// 仅独占 + 设备驱动接受 DSD 子格式时可用（Windows 无标准通道，厂商驱动才有）。</summary>
        public static OutputPlan PlanDsdNative(int dsdBitRate, int slotBits)
        {
            return new OutputPlan
            {
                Rate = slotBits >= 32 ? DsdNativePacker.CarrierRateU32(dsdBitRate) : DsdNativePacker.CarrierRateU16(dsdBitRate),
                Bits = slotBits >= 32 ? 32 : 16,
                Mode = "nativedsd",
            };
        }
    }
}
