using System;

namespace Pa1.Core
{
    /// <summary>曲目规格：PCM → rate=采样率/bits=位深；DSD → rate=位速率/dsd=true。</summary>
    public sealed class TrackSpec
    {
        public string Fmt;        // WAV/PCM/FLOAT/FLAC/DSF/DFF/MP3/Vorbis/Opus/AIFF
        public int Rate;
        public int Bits;
        public int Channels;
        public bool Dsd;

        // DSF
        public long DataOffset;
        public long DataBytes;
        public long SampleCount;
        public int BlockSize;
        public long Id3Offset;

        // DFF
        public bool Compressed;   // DST 压缩（拒绝）
        public bool Incomplete;

        public TrackSpec Clone()
        {
            return (TrackSpec)MemberwiseClone();
        }

        public override string ToString()
        {
            return Dsd
                ? string.Format("DSD{0} {1}ch", Math.Max(1, Math.Round(Rate / 44100.0)), Channels)
                : string.Format("{0} {1}Hz {2}bit {3}ch", Fmt, Rate, Bits, Channels);
        }
    }

    /// <summary>容器探测：WAV / FLAC / DSF / DFF / MP3 / OGG(Vorbis|Opus) / AIFF。</summary>
    public static class FormatProbe
    {
        static string Tag(byte[] b, int o)
        {
            if (o + 3 >= b.Length) return "\0\0\0\0";
            return new string(new[] { (char)b[o], (char)b[o + 1], (char)b[o + 2], (char)b[o + 3] });
        }

        static ushort U16(byte[] b, int o, bool be = false)
        {
            return be ? (ushort)((b[o] << 8) | b[o + 1])
                      : (ushort)(b[o] | (b[o + 1] << 8));
        }

        static uint U32(byte[] b, int o, bool be = false)
        {
            return be
                ? (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3])
                : (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        static ulong U64(byte[] b, int o, bool be = false)
        {
            if (be)
            {
                ulong v = 0;
                for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
                return v;
            }
            return U32(b, o) | ((ulong)U32(b, o + 4) << 32);
        }

        /// <summary>从文件头字节窗口探测规格。head 建议 ≥ 262144 字节；失败返回 null（不阻塞导入）。</summary>
        public static TrackSpec Sniff(byte[] head, string name)
        {
            if (head == null || head.Length < 44) return null;
            try
            {
                /* ── WAV（RIFF）：fmt 块 ── */
                if (Tag(head, 0) == "RIFF" && Tag(head, 8) == "WAVE")
                {
                    int o = 12;
                    while (o + 8 <= head.Length)
                    {
                        string id = Tag(head, o);
                        uint sz = U32(head, o + 4);
                        if (id == "fmt ")
                        {
                            ushort fmtId = U16(head, o + 8);
                            return new TrackSpec
                            {
                                Fmt = fmtId == 1 ? "PCM" : fmtId == 3 ? "FLOAT" : "WAV",
                                Channels = U16(head, o + 10),
                                Rate = (int)U32(head, o + 12),
                                Bits = U16(head, o + 22),
                                Dsd = false,
                            };
                        }
                        if (id == "data") break;
                        o += 8 + (int)sz + (int)(sz & 1);
                    }
                    return null;
                }

                /* ── FLAC：STREAMINFO（偏移 8 起 34 字节；采样率 20 bit @10） ── */
                if (Tag(head, 0) == "fLaC")
                {
                    int b10 = head[8 + 10], b11 = head[8 + 11], b12 = head[8 + 12], b13 = head[8 + 13];
                    int rate = (b10 << 12) | (b11 << 4) | (b12 >> 4);
                    return new TrackSpec
                    {
                        Fmt = "FLAC",
                        Rate = rate,
                        Channels = ((b12 >> 1) & 7) + 1,
                        Bits = (((b12 & 1) << 4) | (b13 >> 4)) + 1,
                        Dsd = false,
                    };
                }

                /* ── DSF（Sony）：'DSD ' + 'fmt ' ── */
                if (Tag(head, 0) == "DSD " && Tag(head, 28) == "fmt ")
                {
                    return new TrackSpec
                    {
                        Fmt = "DSF",
                        Channels = (int)U32(head, 52),
                        Rate = (int)U32(head, 56),
                        Bits = (int)U32(head, 60) == 0 ? 1 : (int)U32(head, 60),
                        Dsd = true,
                        DataOffset = 92,
                        SampleCount = (long)U64(head, 64),
                        BlockSize = (int)U32(head, 72),
                        Id3Offset = (long)U64(head, 20),
                    };
                }

                /* ── DFF（DSDIFF）：FRM8 → PROP/SND（FS 采样率 u32 BE、CMPR 压缩）+ DSD 数据 ── */
                if (Tag(head, 0) == "FRM8" && Tag(head, 12) == "DSD ")
                {
                    int o = 16;
                    int rate = 0, channels = 2;
                    bool compressed = false;
                    while (o + 12 <= head.Length)
                    {
                        string id = Tag(head, o);
                        uint szHi = U32(head, o + 4, true), szLo = U32(head, o + 8, true);
                        if (szHi != 0) break;                    // 顶层块长 u64 BE，>4GB 不支持
                        int sz = (int)szLo;
                        if (id == "PROP" && Tag(head, o + 12) == "SND ")
                        {
                            int p = o + 16, pend = Math.Min(o + 12 + sz, head.Length);
                            while (p + 8 <= pend)
                            {
                                string pid = Tag(head, p);
                                bool is64 = U32(head, p + 4, true) == 0;
                                int psz = is64 ? (int)U32(head, p + 8, true) : (int)U32(head, p + 4, true);
                                int hdr = is64 ? 12 : 8;
                                if (pid == "FS  ") rate = (int)U32(head, p + hdr, true);
                                else if (pid == "CHNL") channels = U16(head, p + hdr, true);
                                else if (pid == "CMPR") compressed = Tag(head, p + hdr) == "DST ";
                                p += hdr + psz + (psz & 1);
                            }
                        }
                        if (id == "DSD ")
                        {
                            return new TrackSpec
                            {
                                Fmt = "DFF",
                                Rate = rate,
                                Channels = channels,
                                Bits = 1,
                                Dsd = true,
                                DataOffset = o + 12,
                                DataBytes = sz,
                                Compressed = compressed,
                            };
                        }
                        o += 12 + sz + (sz & 1);
                    }
                    return new TrackSpec { Fmt = "DFF", Rate = rate, Channels = channels, Bits = 1, Dsd = true, Compressed = compressed, Incomplete = true };
                }

                /* ── MP3：跳过 ID3，找帧同步头 ── */
                if ((head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) || (head[0] == 'I' && head[1] == 'D' && head[2] == '3'))
                {
                    int o = 0;
                    if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                    {
                        int sz = ((head[6] & 0x7f) << 21) | ((head[7] & 0x7f) << 14) | ((head[8] & 0x7f) << 7) | (head[9] & 0x7f);
                        o = Math.Min(10 + sz, head.Length - 4);
                    }
                    for (int i = o; i < head.Length - 4; i++)
                    {
                        if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0) continue;
                        int verBits = (head[i + 1] >> 3) & 3;       /* 3=MPEG1 2=MPEG2 0=MPEG2.5 */
                        int layer = (head[i + 1] >> 1) & 3;
                        if (verBits == 1 || layer == 0) continue;
                        int srIdx = (head[i + 2] >> 2) & 3;
                        if (srIdx == 3) continue;
                        int rate;
                        if (verBits == 3) rate = new[] { 44100, 48000, 32000 }[srIdx];
                        else if (verBits == 2) rate = new[] { 22050, 24000, 16000 }[srIdx];
                        else rate = new[] { 11025, 12000, 8000 }[srIdx];
                        return new TrackSpec
                        {
                            Fmt = "MP3",
                            Rate = rate,
                            Bits = 16,
                            Channels = ((head[i + 3] >> 6) & 3) == 3 ? 1 : 2,
                            Dsd = false,
                        };
                    }
                    return null;
                }

                /* ── OGG / Opus：首个页面负载 ── */
                if (Tag(head, 0) == "OggS")
                {
                    int segs = head[26];
                    int p = 27 + segs;
                    if (Tag(head, p + 1) == "orbis" && head[p] == 1)     /* '\x01vorbis' */
                    {
                        return new TrackSpec
                        {
                            Fmt = "Vorbis",
                            Rate = (int)U32(head, p + 11),
                            Bits = 16,
                            Channels = head[p + 10],
                            Dsd = false,
                        };
                    }
                    if (Tag(head, p) == "Opus" && Tag(head, p + 4) == "Head")
                    {
                        return new TrackSpec
                        {
                            Fmt = "Opus",
                            Rate = (int)U32(head, p + 12) == 0 ? 48000 : (int)U32(head, p + 12),
                            Bits = 16,
                            Channels = head[p + 9],
                            Dsd = false,
                        };
                    }
                    return null;
                }

                /* ── AIFF：COMM 块，80-bit IEEE 扩展采样率 ── */
                if (Tag(head, 0) == "FORM" && (Tag(head, 8) == "AIFF" || Tag(head, 8) == "AIFC"))
                {
                    int o = 12;
                    while (o + 8 <= head.Length)
                    {
                        string id = Tag(head, o);
                        uint sz = U32(head, o + 4, true);
                        if (id == "COMM")
                        {
                            int channels = U16(head, o + 8, true);
                            int bits = U16(head, o + 14, true);
                            int exp = U16(head, o + 16, true);
                            ulong mant = U64(head, o + 18, true);
                            double rate = exp != 0 ? mant * Math.Pow(2, exp - 16383 - 63) : 0;
                            return new TrackSpec
                            {
                                Fmt = "AIFF",
                                Rate = (int)Math.Round(rate),
                                Bits = bits,
                                Channels = channels,
                                Dsd = false,
                            };
                        }
                        o += 8 + (int)sz + (int)(sz & 1);
                    }
                }
            }
            catch { /* 探测失败不阻塞导入 */ }
            return null;
        }

        public static string KhzLabel(int rate)
        {
            double k = rate / 1000.0;
            return (Math.Abs(k - Math.Round(k)) > 1e-9 ? k.ToString("0.0") : ((int)k).ToString()) + " kHz";
        }

        public static string SpecLabel(TrackSpec spec)
        {
            if (spec == null) return "";
            if (spec.Dsd)
            {
                int n = (int)Math.Round(spec.Rate / 44100.0);
                return n > 1 ? "DSD" + n : "DSD";
            }
            return KhzLabel(spec.Rate) + (spec.Bits > 0 ? " · " + spec.Bits + "-bit" : "");
        }
    }
}
