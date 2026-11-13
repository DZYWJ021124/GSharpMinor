using System;
using System.Collections.Generic;
using System.IO;

namespace Pa1.Core
{
    /// <summary>
    /// DSD 解码器（DSF/DFF → PCM）。两级架构（对齐 dsd2pcm / saprobe-dsd 参考实现）：
    ///   1) 字节 LUT 8:1 抽取：96 抽头 Kaiser 低通，利用 1-bit 输入把每字节贡献
    ///      预计算成 12×256 表（Gesemann 法），逐输出只做 12 次查表加
    ///   2) 半带 2:1 级联：每级 63 抽头（sinc×Kaiser，偶位抽头天然为零），
    ///      直至目标采样率（原生 176.4/352.8 kHz，或硬件不支持时 44.1 kHz/16-bit）
    /// 容器：DSF = LSB-first、4096B×声道 块交错（fmt.bitsPerSample=8 时 MSB-first）；
    ///       DFF = MSB-first、逐字节声道交错；DST 压缩与 DSD512+ 明确拒绝。
    /// </summary>
    public static class DsdDecoder
    {
        /* ── 基础数学 ── */

        public static double BesselI0(double x)
        {
            double s = 1, t = 1;
            for (int k = 1; k < 40; k++)
            {
                t *= (x / (2 * k)) * (x / (2 * k));
                s += t;
                if (t < 1e-13 * s) break;
            }
            return s;
        }

        /// <summary>窗函数 sinc FIR：fcNorm = 通带边缘/采样率；atten = 阻带衰减 dB。</summary>
        public static double[] DesignLowpass(int taps, double fcNorm, double atten)
        {
            double beta = atten > 50 ? 0.1102 * (atten - 8.7)
                : atten >= 21 ? 0.5842 * Math.Pow(atten - 21, 0.4) + 0.07886 * (atten - 21) : 0;
            int M = taps - 1;
            double i0b = BesselI0(beta);
            var h = new double[taps];
            for (int n = 0; n < taps; n++)
            {
                double k = n - M / 2.0;
                double sinc = k == 0 ? 2 * fcNorm : Math.Sin(2 * Math.PI * fcNorm * k) / (Math.PI * k);
                double r = 2.0 * n / M - 1;
                h[n] = sinc * (BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b);
            }
            double sum = 0;
            for (int n = 0; n < taps; n++) sum += h[n];
            for (int n = 0; n < taps; n++) h[n] /= sum;
            return h;
        }

        /// <summary>96 抽头（12 字节）LUT：lut[j][byte] = 该字节对输出的贡献（tap 8j+b）。</summary>
        public static float[][] BuildLut(double[] h, bool lsbFirst)
        {
            var lut = new float[12][];
            for (int j = 0; j < 12; j++)
            {
                var tab = new float[256];
                for (int v = 0; v < 256; v++)
                {
                    float s = 0;
                    for (int b = 0; b < 8; b++)
                    {
                        int bit = (v >> (lsbFirst ? b : 7 - b)) & 1;
                        s += (float)((bit != 0 ? 1 : -1) * h[8 * j + b]);
                    }
                    tab[v] = s;
                }
                lut[j] = tab;
            }
            return lut;
        }

        /* ── 级 1：字节 LUT 8:1（状态 = 上批末 11 字节；窗口 12 字节步进 1 字节 → 抽 8 倍） ── */

        internal sealed class Stage1
        {
            readonly float[][] _lut;
            readonly int _channels;
            readonly byte[][] _carry;
            float[][] _outs;          /* 输出缓冲复用（精确尺寸命中即复用）——热路径零分配 */

            public Stage1(float[][] lut, int channels)
            {
                _lut = lut;
                _channels = channels;
                _carry = new byte[channels][];
                for (int c = 0; c < channels; c++) _carry[c] = new byte[11];
            }

            /// <summary>bytes: 平面布局（c0 全部, c1 全部…）；bytesPerCh = 每声道字节数。返回每声道输出。
            /// 缓冲复用契约：返回数组仅在下次 Push 前有效，消费方须立即取值/拷贝。</summary>
            public float[][] Push(byte[] bytes, int bytesPerCh)
            {
                int n = Math.Max(0, bytesPerCh);
                if (_outs == null) _outs = new float[_channels][];
                for (int c = 0; c < _channels; c++)
                {
                    int srcOff = c * bytesPerCh;
                    var cd = _carry[c];
                    if (_outs[c] == null || _outs[c].Length != n) _outs[c] = new float[n];
                    var o = _outs[c];
                    for (int m = 0; m < n; m++)
                    {
                        float s = 0;
                        for (int j = 0; j < 12; j++)
                        {
                            int bi = m + j;                    // 拼接流（carry 11 + src）下标
                            s += _lut[j][bi < 11 ? cd[bi] : bytes[srcOff + bi - 11]];
                        }
                        o[m] = s;
                    }
                    /* carry = 拼接流末 11 字节 */
                    if (bytesPerCh >= 11) Array.Copy(bytes, srcOff + bytesPerCh - 11, cd, 0, 11);
                    else if (bytesPerCh > 0)
                    {
                        Array.Copy(cd, bytesPerCh, cd, 0, 11 - bytesPerCh);
                        Array.Copy(bytes, srcOff, cd, 11 - bytesPerCh, bytesPerCh);
                    }
                }
                return _outs;
            }
        }

        /* ── 半带 2:1（63 抽头，偶位抽头天然为零；状态 = 上批末 62 输入） ── */

        internal sealed class Halfband
        {
            readonly double[] _h;
            readonly int _taps;
            readonly int _channels;
            readonly double[][] _carry;
            float[][] _outs;        /* 输出缓冲复用 */
            double[] _merge;        /* carry 拼接 scratch 复用（原每调用每声道 new double[total] 是 GC 大头） */

            /// <summary>h 为共享只读系数（设计缓存提供）；各实例仅持有状态 carry。</summary>
            public Halfband(int channels, double[] h)
            {
                _h = h;
                _taps = h.Length;
                _channels = channels;
                _carry = new double[channels][];
                for (int c = 0; c < channels; c++) _carry[c] = new double[_taps - 1];
            }

            /// <summary>2:1 抽取。缓冲复用契约：返回数组仅在下次 Push 前有效，消费方须立即取值/拷贝。</summary>
            public float[][] Push(float[][] planes)
            {
                if (_outs == null) _outs = new float[_channels][];
                int L = planes[0].Length;
                int total = (_taps - 1) + L;
                int nOut = Math.Max(0, (total - _taps) / 2 + 1);
                if (_merge == null || _merge.Length < total) _merge = new double[total];
                for (int c = 0; c < _channels; c++)
                {
                    var x = planes[c];
                    var cd = _carry[c];
                    if (_outs[c] == null || _outs[c].Length != nOut) _outs[c] = new float[nOut];
                    var output = _outs[c];
                    for (int m = 0; m < nOut; m++)
                    {
                        double s = 0;
                        int baseOff = 2 * m;
                        for (int k = 0; k < _taps; k++)
                        {
                            int i = baseOff + k;
                            s += _h[k] * (i < _taps - 1 ? cd[i] : x[i - (_taps - 1)]);
                        }
                        output[m] = (float)s;
                    }
                    var merged = _merge;
                    Array.Copy(cd, 0, merged, 0, _taps - 1);
                    for (int i = 0; i < L; i++) merged[_taps - 1 + i] = x[i];
                    Array.Copy(merged, total - (_taps - 1), cd, 0, _taps - 1);
                }
                return _outs;
            }
        }

        /* ── 容器解析 ── */

        public sealed class DsdMeta
        {
            public bool Ok;
            public string Error;
            public string Container;      // 'DSF' | 'DFF'
            public int Rate;              // 位速率
            public int Channels;
            public bool LsbFirst;
            public long SampleCount;
            public int BlockSize;
            public long DataOffset;
            public long DataBytes;
            public long Id3Offset;

            public DsdMeta Clone() { return (DsdMeta)MemberwiseClone(); }
        }

        static string Tag(byte[] b, int o)
        {
            return new string(new[] { (char)b[o], (char)b[o + 1], (char)b[o + 2], (char)b[o + 3] });
        }

        static uint U32(byte[] b, int o, bool be)
        {
            return be
                ? (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3])
                : (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        static ulong U64(byte[] b, int o, bool be)
        {
            if (be)
            {
                ulong v = 0;
                for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
                return v;
            }
            return U32(b, o, false) | ((ulong)U32(b, o + 4, false) << 32);
        }

        public static DsdMeta ParseDsf(byte[] head)
        {
            if (Tag(head, 0) != "DSD " || Tag(head, 28) != "fmt ")
                return new DsdMeta { Ok = false, Error = "NOT DSF" };
            int rate = (int)U32(head, 56, false);
            int bits = (int)U32(head, 60, false);
            if (bits == 0) bits = 1;
            int channels = (int)U32(head, 52, false);
            if (rate > 11289600)
                return new DsdMeta { Ok = false, Error = "DSD" + Math.Round(rate / 44100.0) + " UNSUPPORTED" };
            return new DsdMeta
            {
                Ok = true,
                Container = "DSF",
                Rate = rate,
                Channels = channels,
                LsbFirst = bits != 8,
                SampleCount = (long)U64(head, 64, false),
                BlockSize = (int)U32(head, 72, false) == 0 ? 4096 : (int)U32(head, 72, false),
                DataOffset = 92,
                Id3Offset = (long)U64(head, 20, false),
            };
        }

        public static DsdMeta ParseDff(byte[] head)
        {
            if (Tag(head, 0) != "FRM8" || Tag(head, 12) != "DSD ")
                return new DsdMeta { Ok = false, Error = "NOT DFF" };
            int o = 16, rate = 0, channels = 2;
            bool compressed = false;
            long dataOffset = -1, dataBytes = 0;
            while (o + 12 <= head.Length)
            {
                string id = Tag(head, o);
                uint szHi = U32(head, o + 4, true), szLo = U32(head, o + 8, true);
                if (szHi != 0) break;
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
                        else if (pid == "CHNL") channels = (head[p + hdr] << 8) | head[p + hdr + 1];
                        else if (pid == "CMPR") compressed = Tag(head, p + hdr) == "DST ";
                        p += hdr + psz + (psz & 1);
                    }
                }
                if (id == "DSD ")
                {
                    dataOffset = o + 12;
                    dataBytes = sz;
                }
                o += 12 + sz + (sz & 1);
            }
            if (compressed) return new DsdMeta { Ok = false, Error = "DST COMPRESSION UNSUPPORTED" };
            if (rate > 11289600)
                return new DsdMeta { Ok = false, Error = "DSD" + Math.Round(rate / 44100.0) + " UNSUPPORTED" };
            if (dataOffset < 0) return new DsdMeta { Ok = false, Error = "NO DSD CHUNK" };
            return new DsdMeta
            {
                Ok = true,
                Container = "DFF",
                Rate = rate,
                Channels = channels,
                LsbFirst = false,
                SampleCount = dataBytes * 8 / channels,
                DataOffset = dataOffset,
                DataBytes = dataBytes,
            };
        }

        /// <summary>真实 DSD 数据区末尾：DSF 尾部 ID3 标签不是音频，DFF 则受 DSD chunk 长度约束。
        /// 所有批式、流式和 DoP 路径共用此边界，避免把封面/标签字节解码成满幅噪声。</summary>
        public static long AudioDataEnd(DsdMeta meta, long fileLength)
        {
            if (meta == null || fileLength <= 0) return 0;
            long end = fileLength;
            if (meta.Container == "DSF" && meta.Id3Offset >= meta.DataOffset && meta.Id3Offset < end)
                end = meta.Id3Offset;
            if (meta.DataBytes > 0)
                end = Math.Min(end, meta.DataOffset + meta.DataBytes);
            return Math.Max(meta.DataOffset, end);
        }

        /* ── 输出率 / 抽取链规划 ── */

        /// <summary>原生解码输出率：DSD64 → 176.4k；DSD128/256 → 352.8k。</summary>
        public static int OutRate(int bitRate)
        {
            return bitRate <= 2822400 ? 176400 : 352800;
        }

        /// <summary>位速率 → 目标率的总抽取比（须为 8·2^k）。</summary>
        public static int PlanChain(int bitRate, int targetRate)
        {
            double dec = (double)bitRate / targetRate;
            double hb = Math.Log(dec / 8, 2);
            if (Math.Abs(dec - 8 * Math.Pow(2, Math.Round(hb))) > 1e-9 || hb < 0 || hb > 6 || Math.Abs(hb - Math.Round(hb)) > 1e-9)
                throw new InvalidOperationException("BAD TARGET RATE");
            return (int)Math.Round(hb);   // hbStages
        }

        /* ── 流式解码管线 ── */

        /// <summary>直流/次声阻断截止频率：DSD 位流常带直流偏置与次声（&lt;10Hz），
        /// 直推功放会发热并产生「电流声」般的嗡鸣；双极点高通只动次声区，20Hz 以上通带平坦。</summary>
        public const double DcFc = 5.0;

        public sealed class Pipeline
        {
            public float[][] Out;
            public long OutLen;
            public int TotalDec;

            internal Stage1 St1;
            internal Halfband[] Halves;
            internal int Channels;
            internal long Cursor;

            /* 直流阻断器状态：双极点 y=x−x1+R·y1 级联（每声道） */
            internal float DcR;
            internal float[] DcX1, DcY1, DcX2, DcY2;

            /* FeedBlock 平面拆分 scratch（每管线独占，线程安全：一管线一线程） */
            internal byte[] FlatScratch;

            /// <summary>喂一块交错字节（已拆平面），返回本块产出的各声道 PCM。
            /// Out != null 时同时写入整曲缓冲（批式解码）；流式播放传 allocOut:false 跳过。
            /// 缓冲复用契约：返回数组仅在下次 Feed 前有效，消费方须立即取值/拷贝。</summary>
            public float[][] Feed(byte[] bytes, int bytesPerCh)
            {
                float[][] planes = St1.Push(bytes, bytesPerCh);
                foreach (var hb in Halves) planes = hb.Push(planes);
                DcBlock(planes);
                if (Out != null)
                {
                    int n = planes[0].Length;
                    long take = Math.Min(n, OutLen - Cursor);
                    for (int c = 0; c < Channels; c++)
                        Array.Copy(planes[c], 0, Out[c], Cursor, (int)take);
                    Cursor += take;
                }
                return planes;
            }

            void DcBlock(float[][] planes)
            {
                if (planes == null || planes.Length == 0 || planes[0].Length == 0) return;
                int n = planes[0].Length;
                float R = DcR;
                for (int c = 0; c < Channels; c++)
                {
                    float[] p = planes[c];
                    float x1 = DcX1[c], y1 = DcY1[c], x2 = DcX2[c], y2 = DcY2[c];
                    for (int i = 0; i < n; i++)
                    {
                        float x = p[i];
                        float y = x - x1 + R * y1; x1 = x; y1 = y;
                        float z = y - x2 + R * y2; x2 = y; y2 = z;
                        p[i] = z;
                    }
                    /* 次常态（denormal）会让 x87/SSE 减速几十倍 → 音频线程卡顿，块末冲零 */
                    if (y1 > -1e-18f && y1 < 1e-18f) y1 = 0;
                    if (y2 > -1e-18f && y2 < 1e-18f) y2 = 0;
                    DcX1[c] = x1; DcY1[c] = y1; DcX2[c] = x2; DcY2[c] = y2;
                }
            }

            public long Done() { return Cursor; }
        }

        /// <summary>
        /// 系数设计缓存：LUT 与半带抽头只取决于（位速率, 目标率, 位序），
        /// 与声道数/文件无关。切歌、Seek 重建管线时免去重复 Kaiser 设计（各几十 ms）。
        /// </summary>
        sealed class CoefSet
        {
            public float[][] Lut;
            public double[] HbTaps;
            public int HbStages;
            public int TotalDec;
            public double Fc1;
        }

        static readonly object _coefLock = new object();
        static readonly Dictionary<string, CoefSet> _coefCache = new Dictionary<string, CoefSet>();

        static CoefSet GetCoefs(int bitRate, int targetRate, bool lsbFirst)
        {
            string key = bitRate + "|" + targetRate + "|" + (lsbFirst ? "L" : "M");
            lock (_coefLock)
            {
                CoefSet cs;
                if (_coefCache.TryGetValue(key, out cs)) return cs;

                int hbStages = PlanChain(bitRate, targetRate);
                double fS1 = bitRate / 8.0;
                double finalFc = 0.45 * targetRate;
                const int taps1 = 96;
                double dF = 0.067 * bitRate;            /* 96 抽头 @100dB 过渡宽估计 */
                double p1 = Math.Min(0.45 * fS1, Math.Max(finalFc, (fS1 - dF) / 2));
                double[] h1 = DesignLowpass(taps1, p1 / bitRate, 100);

                cs = new CoefSet
                {
                    Lut = BuildLut(h1, lsbFirst),
                    HbTaps = DesignLowpass(63, 0.25, 90),
                    HbStages = hbStages,
                    TotalDec = 8 * (1 << hbStages),
                    Fc1 = p1 / bitRate,
                };
                _coefCache[key] = cs;
                return cs;
            }
        }

        /// <summary>核心：构建流式解码管线。allocOut=false 时跳过整曲输出缓冲（流式播放省内存）。</summary>
        public static Pipeline MakePipeline(DsdMeta meta, int targetRate, bool allocOut = true)
        {
            int bitRate = meta.Rate, ch = meta.Channels;
            CoefSet cs = GetCoefs(bitRate, targetRate, meta.LsbFirst);
            int hbStages = cs.HbStages;
            int totalDec = cs.TotalDec;
            var st1 = new Stage1(cs.Lut, ch);            /* LUT 共享只读 */
            var halves = new Halfband[hbStages];
            for (int k = 0; k < hbStages; k++) halves[k] = new Halfband(ch, cs.HbTaps);
            long outLen = meta.SampleCount / totalDec;
            float[][] outBuf = null;
            if (allocOut)
            {
                outBuf = new float[ch][];
                for (int c = 0; c < ch; c++) outBuf[c] = new float[outLen];
            }
            float dcR = (float)Math.Exp(-2 * Math.PI * DcFc / (bitRate / (double)totalDec));
            return new Pipeline
            {
                Out = outBuf,
                OutLen = outLen,
                TotalDec = totalDec,
                St1 = st1,
                Halves = halves,
                Channels = ch,
                Cursor = 0,
                DcR = dcR,
                DcX1 = new float[ch], DcY1 = new float[ch],
                DcX2 = new float[ch], DcY2 = new float[ch],
            };
        }

        /// <summary>把容器交错块拆成平面（ch-major：c0 全部字节 ‖ c1 …），返回每声道字节数。
        /// flat 须 ≥ len。DSF 组交错 [c0 bs][c1 bs]…；DFF 字节交错 c0,c1,c0,c1…。解码与 DoP 打包共用。</summary>
        public static int SplitRaw(DsdMeta meta, byte[] buf, int len, byte[] flat)
        {
            int ch = meta.Channels;
            if (meta.Container == "DSF")
            {
                int bs = meta.BlockSize;
                int full = len / (bs * ch);
                int rem = len - full * bs * ch;
                int rpb = rem / ch;
                int total = full * bs + rpb;
                for (int g = 0; g < full; g++)
                    for (int c = 0; c < ch; c++)
                        Array.Copy(buf, (g * ch + c) * bs, flat, c * total + g * bs, bs);
                for (int c = 0; c < ch; c++)
                    Array.Copy(buf, full * bs * ch + c * rpb, flat, c * total + full * bs, rpb);
                return total;
            }
            else
            {
                int frames = len / ch;
                for (int c = 0; c < ch; c++)
                    for (int i = 0; i < frames; i++)
                        flat[c * frames + i] = buf[i * ch + c];
                return frames;
            }
        }

        /// <summary>把一个交错数据块拆成平面并喂给管线（批式/流式共用）。
        /// 平面 scratch 复用；返回数组仅在下次 FeedBlock 前有效（取值/拷贝后丢弃）。</summary>
        public static float[][] FeedBlock(Pipeline pipe, byte[] buf, int len, DsdMeta meta)
        {
            if (pipe.FlatScratch == null || pipe.FlatScratch.Length < len) pipe.FlatScratch = new byte[len];
            var flat = pipe.FlatScratch;
            int per = SplitRaw(meta, buf, len, flat);
            return pipe.Feed(flat, per);
        }

        /// <summary>文件级解码：按容器交错格式流式读，onProgress(0..1) 可选。</summary>
        public static Pipeline DecodeFile(string path, DsdMeta meta, int targetRate, Action<double> onProgress = null)
        {
            var pipe = MakePipeline(meta, targetRate);
            int ch = meta.Channels;
            bool isDsf = meta.Container == "DSF";
            int block = isDsf ? meta.BlockSize * ch : 4096 * ch;
            long dataEnd = AudioDataEnd(meta, new FileInfo(path).Length);

            using (var fs = File.OpenRead(path))
            {
                long pos = meta.DataOffset;
                fs.Position = pos;
                while (pos < dataEnd)
                {
                    int len = (int)Math.Min(block, dataEnd - pos);
                    var buf = new byte[len];
                    int got = 0;
                    while (got < len)
                    {
                        int r = fs.Read(buf, got, len - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got < len) { var b2 = new byte[got]; Array.Copy(buf, b2, got); buf = b2; len = got; }
                    if (len == 0) break;

                    FeedBlock(pipe, buf, len, meta);
                    pos += len;
                    if (onProgress != null)
                        onProgress(Math.Min(1, (pos - meta.DataOffset) / (double)Math.Max(1, dataEnd - meta.DataOffset)));
                }
            }
            return pipe;
        }
    }
}
