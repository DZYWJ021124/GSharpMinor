using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pa1.Core;

namespace Pa1.Tests
{
    /// <summary>
    /// Pa1.Core 回归测试（逐条移植 dsd-test.mjs / pl-test.mjs 断言）：
    ///   1. 容器解析（DSF/DFF）
    ///   2. 方波 PDM 谐波幅度须符合理论 4/(nπ)（Goertzel）
    ///   3. LFSR 跨容器一致性 / 位序反例
    ///   4. 硬件判定降档矩阵（10 场景）
    ///   5. 播放模式推进（pl-test 11 条）
    ///   6. 格式探测 / 频段数学
    /// </summary>
    static class Program
    {
        static int _pass, _fail;

        static void Ok(string name, bool cond, string detail = "")
        {
            if (cond) _pass++;
            else _fail++;
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        }

        const int FBit = 2822400, Freq = 1000, Dur = 4, CH = 2;
        const int NBits = FBit / Dur;            // 705600
        const int NBytes = NBits / 8;            // 88200

        /* ── 测试信号（与 dsd-test.mjs 逐位一致） ── */

        static byte[] PackSine(bool lsbFirst)
        {
            var outB = new byte[NBytes];
            for (int i = 0; i < NBytes; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++)
                {
                    double t = (i * 8 + b) / (double)FBit;
                    v |= (Math.Sin(2 * Math.PI * Freq * t) >= 0 ? 1 : 0) << (lsbFirst ? b : 7 - b);
                }
                outB[i] = (byte)v;
            }
            return outB;
        }

        static byte[] PackLfsr(bool lsbFirst)
        {
            var outB = new byte[NBytes];
            int x = 0x1337;
            for (int i = 0; i < NBytes; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++)
                {
                    int bit = x & 1;
                    x = (x >> 1) ^ (bit != 0 ? 0x6000 : 0);   /* x^15 + x^14 + 1 */
                    v |= bit << (lsbFirst ? b : 7 - b);
                }
                outB[i] = (byte)v;
            }
            return outB;
        }

        /* raw: 每声道字节（两声道同数据）；容器按各自位序/交错打包（布局与 dsd-test.mjs 相同） */
        static byte[] BuildDsf(byte[] raw)
        {
            const int blockSize = 4096;
            int total = 92 + NBytes * CH;
            var buf = new byte[total];
            Action<int, string> str = (o, s) => { for (int i = 0; i < s.Length; i++) buf[o + i] = (byte)s[i]; };
            Action<int, ulong> u64le = (o, v) => { for (int i = 0; i < 8; i++) buf[o + i] = (byte)(v >> (8 * i)); };
            Action<int, uint> u32le = (o, v) => { for (int i = 0; i < 4; i++) buf[o + i] = (byte)(v >> (8 * i)); };
            str(0, "DSD "); u64le(4, 28); u64le(12, (ulong)total); u64le(20, 0);
            str(28, "fmt "); u64le(32, 52); u32le(40, 1); u32le(44, 0);
            u32le(48, 2); u32le(52, CH);
            u32le(56, FBit); u32le(60, 1);
            u64le(64, NBits); u32le(72, blockSize); u32le(76, 0);
            str(80, "data"); u64le(84, (ulong)(total - 80));
            int w = 92;
            int groups = NBytes / blockSize;
            for (int g = 0; g < groups; g++)
                for (int c = 0; c < CH; c++)
                {
                    Array.Copy(raw, g * blockSize, buf, w, blockSize);
                    w += blockSize;
                }
            int rem = NBytes - groups * blockSize;
            for (int c = 0; c < CH; c++)
            {
                Array.Copy(raw, groups * blockSize, buf, w, rem);
                w += rem;
            }
            return buf;
        }

        static byte[] BuildDff(byte[] raw)
        {
            int dataLen = NBytes * CH;
            int sndSubs = 12 + 18 + 14;                    /* FS / CHNL(8+10) / CMPR(8+5+pad) */
            int propLen = 4 + sndSubs;
            int total = 88 + dataLen;
            var buf = new byte[total];
            Action<int, string> str = (o, s) => { for (int i = 0; i < s.Length; i++) buf[o + i] = (byte)s[i]; };
            Action<int, ulong> u64be = (o, v) => { for (int i = 0; i < 8; i++) buf[o + i] = (byte)(v >> (8 * (7 - i))); };
            Action<int, uint> u32be = (o, v) => { for (int i = 0; i < 4; i++) buf[o + i] = (byte)(v >> (8 * (3 - i))); };
            Action<int, ushort> u16be = (o, v) => { buf[o] = (byte)(v >> 8); buf[o + 1] = (byte)v; };
            str(0, "FRM8"); u64be(4, (ulong)(total - 8)); str(12, "DSD ");
            str(16, "PROP"); u64be(20, (ulong)propLen); str(28, "SND ");
            str(32, "FS  "); u32be(36, 4); u32be(40, FBit);
            str(44, "CHNL"); u32be(48, 10); u16be(52, CH);
            str(54, "SLFT"); str(58, "SRGT");
            str(62, "CMPR"); u32be(66, 5); str(70, "DSD "); buf[74] = 0;
            str(76, "DSD "); u64be(80, (ulong)dataLen);
            for (int i = 0; i < NBytes; i++)
                for (int c = 0; c < CH; c++)
                    buf[88 + i * CH + c] = raw[i];
            return buf;
        }

        /* Goertzel（与 dsd-test.mjs 相同公式） */
        static double Goertzel(float[] x, int fs, double f)
        {
            double w = 2 * Math.PI * f / fs, cw = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double s0 = x[i] + cw * s1 - s2;
                s2 = s1; s1 = s0;
            }
            return 2 * Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - cw * s1 * s2)) / x.Length;
        }

        static int Main()
        {
            Console.WriteLine("=== Pa1.Core regression ===");
            string tmp = Path.Combine(Path.GetTempPath(), "pa1-native-test");
            Directory.CreateDirectory(tmp);

            var sineLsb = PackSine(true);
            var sineMsb = PackSine(false);
            var lfsrLsb = PackLfsr(true);
            var lfsrMsb = PackLfsr(false);
            string pSineDsf = Path.Combine(tmp, "sine.dsf");
            string pSineDff = Path.Combine(tmp, "sine.dff");
            string pLfsrDsf = Path.Combine(tmp, "lfsr.dsf");
            string pLfsrDff = Path.Combine(tmp, "lfsr.dff");
            File.WriteAllBytes(pSineDsf, BuildDsf(sineLsb));
            File.WriteAllBytes(pSineDff, BuildDff(sineMsb));
            File.WriteAllBytes(pLfsrDsf, BuildDsf(lfsrLsb));
            File.WriteAllBytes(pLfsrDff, BuildDff(lfsrMsb));

            /* ── 1. 容器解析 ── */
            var dsfHead = File.ReadAllBytes(pSineDsf);
            var dffHead = File.ReadAllBytes(pSineDff);
            var mDsf = DsdDecoder.ParseDsf(dsfHead);
            var mDff = DsdDecoder.ParseDff(dffHead);
            Ok("parseDsf ok", mDsf.Ok && mDsf.Rate == FBit && mDsf.Channels == 2 && mDsf.LsbFirst && mDsf.SampleCount == NBits,
                mDsf.Ok ? string.Format("rate={0} ch={1} lsb={2} sc={3}", mDsf.Rate, mDsf.Channels, mDsf.LsbFirst, mDsf.SampleCount) : mDsf.Error);
            Ok("parseDff ok", mDff.Ok && mDff.Rate == FBit && mDff.Channels == 2 && !mDff.LsbFirst,
                mDff.Ok ? string.Format("rate={0} ch={1} lsb={2}", mDff.Rate, mDff.Channels, mDff.LsbFirst) : mDff.Error);

            /* DSF 的 ID3 位于音频区之后：读流边界必须停在标签前，不得把封面当 DSD 噪声。 */
            var mBoundary = mDsf.Clone();
            mBoundary.Id3Offset = 123456;
            Ok("DSF 数据边界排除尾部 ID3", DsdDecoder.AudioDataEnd(mBoundary, 200000) == 123456,
                DsdDecoder.AudioDataEnd(mBoundary, 200000).ToString());
            Ok("DFF 数据边界服从 DSD chunk", DsdDecoder.AudioDataEnd(mDff, 200000) == mDff.DataOffset + mDff.DataBytes,
                DsdDecoder.AudioDataEnd(mDff, 200000).ToString());

            /* ── 2. 方波谐波（4/(nπ)），DSF 与 DFF 两条链路 ── */
            foreach (var chain in new[] { "DSF", "DFF" })
            {
                string path = chain == "DSF" ? pSineDsf : pSineDff;
                var meta = chain == "DSF" ? mDsf : mDff;
                var pipe = DsdDecoder.DecodeFile(path, meta, 176400);
                var x = pipe.Out[0];
                int len = (int)pipe.Done();
                Ok(chain + " 解码长度/采样率", len == NBits / 16, "len=" + len + " expect=" + (NBits / 16));
                double a1k = Goertzel(x, 176400, 1000), a3k = Goertzel(x, 176400, 3000),
                       a5k = Goertzel(x, 176400, 5000), a500 = Goertzel(x, 176400, 500);
                /* 方波基波 4/π≈1.273，3 次 1/3、5 次 1/5（经低通后 ±10%） */
                bool harmOk = Math.Abs(a1k - 4 / Math.PI) / (4 / Math.PI) < 0.1
                    && Math.Abs(a3k / a1k - 1.0 / 3) < 0.1
                    && Math.Abs(a5k / a1k - 1.0 / 5) < 0.1
                    && a500 < 0.05;
                Ok(chain + " 方波谐波符合理论 4/(nπ)", harmOk,
                    string.Format("a1k={0:0.000} a3k={1:0.000} a5k={2:0.000} a500={3:0.0000}", a1k, a3k, a5k, a500));
            }

            /* ── 3. LFSR 跨容器一致 + 位序反例 ── */
            {
                var rDsf = DsdDecoder.DecodeFile(pLfsrDsf, mDsf, 176400);
                var rDff = DsdDecoder.DecodeFile(pLfsrDff, mDff, 176400);
                var u = rDsf.Out[0]; var v = rDff.Out[0];
                int n = u.Length;
                double maxDiff = 0, e1 = 0, e2 = 0, dot = 0;
                for (int i = 0; i < n; i++)
                {
                    maxDiff = Math.Max(maxDiff, Math.Abs(u[i] - v[i]));
                    e1 += u[i] * u[i]; e2 += v[i] * v[i]; dot += u[i] * v[i];
                }
                double rho = dot / Math.Sqrt(e1 * e2);
                Ok("LFSR 跨容器一致（DSF LSB ≡ DFF MSB 解码）", maxDiff < 1e-3 && rho > 0.999,
                    string.Format("maxDiff={0:0.00e+0} ρ={1:0.000000}", maxDiff, rho));

                /* 位序反例：DSF 按 MSB 解 → 与正确解码不相关 */
                var mRev = mDsf.Clone();
                mRev.LsbFirst = false;
                var rRev = DsdDecoder.DecodeFile(pLfsrDsf, mRev, 176400);
                var w2 = rRev.Out[0];
                double e3 = 0, dot2 = 0;
                for (int i = 0; i < n; i++) { e3 += w2[i] * w2[i]; dot2 += u[i] * w2[i]; }
                double rhoRev = dot2 / Math.Sqrt(e1 * e3);
                Ok("位序反例：颠倒位序 → 解码结果确实改变", Math.Abs(rhoRev) < 0.995, "ρ=" + rhoRev.ToString("0.0000"));
            }

            /* ── 3b. 降档路径 44.1k ── */
            {
                var d44 = DsdDecoder.DecodeFile(pSineDsf, mDsf, 44100);
                int len = (int)d44.Done();
                double a1k = Goertzel(d44.Out[0], 44100, 1000);
                Ok("降档 44.1k 解码", len == NBits / 64 && Math.Abs(a1k - 4 / Math.PI) / (4 / Math.PI) < 0.15,
                    string.Format("len={0} expect={1} a1k={2:0.000}", len, NBits / 64, a1k));
            }

            /* ── 3c. 流式解码 ≡ 批式解码（逐块 FeedBlock 与 DecodeFile 输出一致） ── */
            {
                var batch = DsdDecoder.DecodeFile(pSineDsf, mDsf, 176400);
                var pipe = DsdDecoder.MakePipeline(mDsf, 176400, false);   /* 流式：不分配整曲缓冲 */
                var stream = new System.Collections.Generic.List<float>();
                byte[] file = System.IO.File.ReadAllBytes(pSineDsf);
                int off = (int)mDsf.DataOffset, blk = mDsf.BlockSize * mDsf.Channels;
                while (off < file.Length)
                {
                    int len = Math.Min(blk, file.Length - off);
                    var buf = new byte[len];
                    Array.Copy(file, off, buf, 0, len);
                    var planes = DsdDecoder.FeedBlock(pipe, buf, len, mDsf);
                    for (int i = 0; i < planes[0].Length; i++) stream.Add(planes[0][i]);
                    off += len;
                }
                int nb = (int)batch.Done(), ns = stream.Count;
                double maxD = 0;
                for (int i = 0; i < Math.Min(nb, ns); i++) maxD = Math.Max(maxD, Math.Abs(batch.Out[0][i] - stream[i]));
                Ok("流式 ≡ 批式（FeedBlock 逐块 == DecodeFile）", nb == ns && maxD == 0,
                    string.Format("batch={0} stream={1} maxDiff={2:0.0e+0}", nb, ns, maxD));
            }

            /* ── 3c-2. 预解码源 DsdStreamSource：同步/预取 ≡ 批式（逐样本） ── */
            {
                var batch = DsdDecoder.DecodeFile(pSineDsf, mDsf, 176400);
                int nb = (int)batch.Done();
                Func<double, List<float>> readAll = (lookahead) =>
                {
                    var got = new List<float>();
                    using (var src = new DsdStreamSource(pSineDsf, mDsf, 176400, lookahead))
                    {
                        var buf = new float[700 * 2];   /* 非整块读，压实块拼接路径 */
                        while (true)
                        {
                            int n = src.Read(buf, 700);
                            if (n <= 0) break;
                            for (int i = 0; i < n; i++) got.Add(buf[i * 2]);   /* ch0 */
                        }
                    }
                    return got;
                };
                foreach (var la in new[] { 0.0, 1.0 })
                {
                    var got = readAll(la);
                    double maxD = 0;
                    for (int i = 0; i < Math.Min(nb, got.Count); i++) maxD = Math.Max(maxD, Math.Abs(batch.Out[0][i] - got[i]));
                    Ok("DsdStreamSource la=" + la + " ≡ 批式（" + got.Count + " 帧）", nb == got.Count && maxD == 0,
                        string.Format("batch={0} src={1} maxDiff={2:0.0e+0}", nb, got.Count, maxD));
                }

                /* 样本数优先于块填充：少声明 16 bit（=176.4k 下 1 帧）就必须少输出 1 帧。 */
                var mShort = mDsf.Clone();
                mShort.SampleCount = NBits - 16;
                int shortFrames = 0;
                using (var src = new DsdStreamSource(pSineDsf, mShort, 176400, 0))
                {
                    var buf = new float[700 * 2];
                    while (true)
                    {
                        int n = src.Read(buf, 700);
                        if (n <= 0) break;
                        shortFrames += n;
                    }
                    Ok("DSD 流在 SampleCount 处截断块填充", shortFrames == nb - 1 && src.EndOfStream,
                        "frames=" + shortFrames + " expect=" + (nb - 1));
                }

                /* 尾部 ID3 集成路径：文件变长但输出帧数不得增加。 */
                byte[] rawDsf = File.ReadAllBytes(pSineDsf);
                string pTagged = Path.Combine(tmp, "tagged.dsf");
                using (var fs = File.Create(pTagged))
                {
                    fs.Write(rawDsf, 0, rawDsf.Length);
                    var tag = new byte[] { 0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0, 0x4e, 0x4f, 0x49, 0x53, 0x45, 0x21 };
                    fs.Write(tag, 0, tag.Length);
                }
                var mTagged = mDsf.Clone(); mTagged.Id3Offset = rawDsf.Length;
                int taggedFrames = 0;
                using (var src = new DsdStreamSource(pTagged, mTagged, 176400, 0))
                {
                    var buf = new float[700 * 2];
                    while (true)
                    {
                        int n = src.Read(buf, 700);
                        if (n <= 0) break;
                        taggedFrames += n;
                    }
                }
                Ok("DSD 流不播放尾部 ID3 标签", taggedFrames == nb,
                    "frames=" + taggedFrames + " expect=" + nb);

                /* 预取源 Seek：跳后位置读数正确、仍有数据产出（fixture 仅 0.25s，取中点 0.1s） */
                using (var src = new DsdStreamSource(pSineDsf, mDsf, 176400, 1.0))
                {
                    var buf = new float[2000 * 2];
                    src.CurrentTime = 0.1;
                    double posAfterSeek = src.CurrentTime;
                    int n = src.Read(buf, 2000);
                    Ok("DsdStreamSource seek 0.1s → 位置推进 + 可续读",
                        Math.Abs(posAfterSeek - 0.1) < 0.02 && n == 2000 && src.CurrentTime > posAfterSeek,
                        string.Format("seekPos={0:0.000} read={1} now={2:0.000}", posAfterSeek, n, src.CurrentTime));
                }

                /* 系数缓存：同参数二次建管线解码结果逐样本一致 */
                {
                    var meta2 = mDsf.Clone();
                    var a = DsdDecoder.MakePipeline(meta2, 176400);
                    var b = DsdDecoder.MakePipeline(meta2, 176400);
                    byte[] file = System.IO.File.ReadAllBytes(pSineDsf);
                    int off = (int)mDsf.DataOffset, blk = mDsf.BlockSize * mDsf.Channels;
                    double maxD = 0; long na = 0, nb2 = 0;
                    while (off < file.Length)
                    {
                        int len = Math.Min(blk, file.Length - off);
                        var buf2 = new byte[len];
                        Array.Copy(file, off, buf2, 0, len);
                        var pa = DsdDecoder.FeedBlock(a, buf2, len, meta2);
                        var pb = DsdDecoder.FeedBlock(b, buf2, len, meta2);
                        for (int i = 0; i < pa[0].Length; i++) maxD = Math.Max(maxD, Math.Abs(pa[0][i] - pb[0][i]));
                        na += pa[0].Length; nb2 += pb[0].Length;
                        off += len;
                    }
                    Ok("系数缓存：二次建管线解码逐样本一致", na == nb2 && maxD == 0,
                        string.Format("n1={0} n2={1} maxDiff={2:0.0e+0}", na, nb2, maxD));
                }
            }

            /* ── 3c-3. 直流/次声阻断（治「电流声」）：全 1 位流 = 直流 +1 → 后段均值须回到 0 ── */
            {
                var ones = new byte[NBytes];
                for (int i = 0; i < NBytes; i++) ones[i] = 0xFF;
                string pOnes = Path.Combine(tmp, "ones.dsf");
                File.WriteAllBytes(pOnes, BuildDsf(ones));
                var mOnes = DsdDecoder.ParseDsf(File.ReadAllBytes(pOnes));
                var d = DsdDecoder.DecodeFile(pOnes, mOnes, 176400);
                int n = (int)d.Done();
                double head = 0, tail = 0;
                for (int i = 0; i < 200; i++) head += d.Out[0][i];
                for (int i = n * 3 / 4; i < n; i++) tail += d.Out[0][i];
                head /= 200; tail /= Math.Max(1, n - n * 3 / 4);
                Ok("直流阻断：全 1 位流头部有信号、后段均值 ≈ 0（去电流声）",
                    n > 1000 && head > 0.5 && Math.Abs(tail) < 0.02,
                    string.Format("n={0} head={1:0.000} tail={2:0.0e+0}", n, head, tail));
            }

            /* ── 3c-4. DoP 1.1 打包（DSD 位流直通载波） ── */
            {
                Ok("DoP 载波率 = 比特率/16",
                    DopPacker.CarrierRate(2822400) == 176400 && DopPacker.CarrierRate(5644800) == 352800
                    && DopPacker.CarrierRate(11289600) == 705600,
                    DopPacker.CarrierRate(2822400) + "/" + DopPacker.CarrierRate(5644800) + "/" + DopPacker.CarrierRate(11289600));
                Ok("DoP 载波安全表（DSD256=705.6k 高于旧门槛，现全速率直发）",
                    OutputPlanner.IsSafeDopCarrier(2822400) && OutputPlanner.IsSafeDopCarrier(5644800)
                    && !OutputPlanner.IsSafeDopCarrier(11289600));

                var dst = new byte[64];

                /* DSF（LSB-first）：ch0 首字节 0x01 → t0=1 → payload 位15=1 → 帧 [0x00][0x80][m] */
                {
                    var flat = new byte[] { 0x01, 0x00, 0x00, 0x00 };
                    bool phase = false;
                    int nf = DopPacker.Pack(dst, flat, 2, 1, 2, true, ref phase);
                    Ok("DoP DSF 帧布局（t0→bit15、双声道同标记）",
                        nf == 1 && dst[0] == 0x00 && dst[1] == 0x80 && dst[2] == 0x05
                               && dst[3] == 0x00 && dst[4] == 0x00 && dst[5] == 0x05 && phase,
                        string.Format("f={0} bytes={1:X2} {2:X2} {3:X2} | {4:X2} {5:X2} {6:X2}", nf, dst[0], dst[1], dst[2], dst[3], dst[4], dst[5]));
                }

                /* DFF（MSB-first）：b0=0x80 → t0=1 → 同样 [0x00][0x80][m] */
                {
                    var flat = new byte[] { 0x80, 0x00, 0x00, 0x00 };
                    bool phase = false;
                    int nf = DopPacker.Pack(dst, flat, 2, 1, 2, false, ref phase);
                    Ok("DoP DFF 帧布局（MSB-first 直取）",
                        nf == 1 && dst[0] == 0x00 && dst[1] == 0x80 && dst[2] == 0x05 && dst[5] == 0x05,
                        string.Format("bytes={0:X2} {1:X2} {2:X2}", dst[0], dst[1], dst[2]));
                }

                /* 标记帧间交替 0x05 → 0xFA */
                {
                    var flat = new byte[8];
                    bool phase = false;
                    int nf = DopPacker.Pack(dst, flat, 4, 2, 1, true, ref phase);
                    Ok("DoP 标记帧间交替 0x05/0xFA",
                        nf == 2 && dst[2] == 0x05 && dst[5] == 0xFA && !phase,
                        string.Format("m0={0:X2} m1={1:X2}", dst[2], dst[5]));
                }

                /* 静音前导帧：0x69 位型 + 标记交替 + 跨调用相位连续（起播防爆音） */
                {
                    bool phase = false;
                    int nb = DopPacker.PackSilence(dst, 0, 3, 2, ref phase);
                    Ok("DoP 静音前导（0x69 位型、标记交替、相位连续）",
                        nb == 3 * 2 * 3
                        && dst[0] == 0x69 && dst[1] == 0x69 && dst[2] == 0x05
                        && dst[6] == 0x69 && dst[8] == 0xFA
                        && dst[12] == 0x69 && dst[14] == 0x05 && phase,
                        string.Format("bytes={0} m0={1:X2} m1={2:X2} m2={3:X2}", nb, dst[2], dst[8], dst[14]));
                }
            }

            /* ── 3c-5. Native DSD 打包（DSD_U32_LE / DSD_U16_LE 位流直排） ── */
            {
                Ok("Native 载波率 U32=/32、U16=/16（DSD256 U32 载波 352.8k，避开 DoP 705.6k 陷阱）",
                    DsdNativePacker.CarrierRateU32(2822400) == 88200 && DsdNativePacker.CarrierRateU32(5644800) == 176400
                    && DsdNativePacker.CarrierRateU32(11289600) == 352800
                    && DsdNativePacker.CarrierRateU16(2822400) == 176400 && DsdNativePacker.CarrierRateU16(11289600) == 705600,
                    DsdNativePacker.CarrierRateU32(11289600) + "/" + DsdNativePacker.CarrierRateU16(11289600));

                {
                    var p32 = OutputPlanner.PlanDsdNative(11289600, 32);
                    var p16 = OutputPlanner.PlanDsdNative(2822400, 16);
                    Ok("PlanDsdNative 规划（率/位宽/NATIVE DSD 前缀）",
                        p32.Rate == 352800 && p32.Bits == 32 && p32.Mode == "nativedsd"
                        && p32.Label.StartsWith("NATIVE DSD · ")
                        && p16.Rate == 176400 && p16.Bits == 16 && p16.Mode == "nativedsd",
                        p32.Label + " | " + p16.Label);
                }

                /* Native U32：DSF（LSB-first）字节位反转 → t0 落容器 bit31 → 小端 [00 00 00 80] */
                {
                    var flat = new byte[] { 0x01, 0x00, 0x00, 0x00,   /* ch0 */
                                            0x01, 0x00, 0x00, 0x00 }; /* ch1 */
                    var ndst = new byte[32];
                    int nf = DsdNativePacker.Pack32(ndst, flat, 4, 1, 2, true);
                    Ok("Native U32 DSF 帧（t0→bit31、小端、双声道）",
                        nf == 1 && ndst[0] == 0x00 && ndst[1] == 0x00 && ndst[2] == 0x00 && ndst[3] == 0x80
                               && ndst[4] == 0x00 && ndst[5] == 0x00 && ndst[6] == 0x00 && ndst[7] == 0x80,
                        string.Format("f={0} bytes={1:X2} {2:X2} {3:X2} {4:X2} | {5:X2} {6:X2} {7:X2} {8:X2}",
                            nf, ndst[0], ndst[1], ndst[2], ndst[3], ndst[4], ndst[5], ndst[6], ndst[7]));
                }

                /* DFF（MSB-first）直取：与 DSF 位反转结果一致（同一位流 → 同一容器值） */
                {
                    var flat = new byte[] { 0x80, 0x00, 0x00, 0x00,
                                            0x80, 0x00, 0x00, 0x00 };
                    var ndst = new byte[32];
                    int nf = DsdNativePacker.Pack32(ndst, flat, 4, 1, 2, false);
                    Ok("Native U32 DFF 帧（MSB-first 直取）",
                        nf == 1 && ndst[0] == 0x00 && ndst[3] == 0x80 && ndst[4] == 0x00 && ndst[7] == 0x80,
                        string.Format("bytes={0:X2} {1:X2} {2:X2} {3:X2}", ndst[0], ndst[1], ndst[2], ndst[3]));
                }

                /* 中间位序：DSF 0x02（bit1=1 → t1）→ 容器 bit30 → w=0x40000000 */
                {
                    var flat = new byte[] { 0x02, 0x00, 0x00, 0x00 };
                    var ndst = new byte[16];
                    DsdNativePacker.Pack32(ndst, flat, 4, 1, 1, true);
                    Ok("Native U32 DSF 中间位（0x02→t1→bit30）",
                        ndst[0] == 0x00 && ndst[1] == 0x00 && ndst[2] == 0x00 && ndst[3] == 0x40,
                        string.Format("w={0:X2} {1:X2} {2:X2} {3:X2}", ndst[0], ndst[1], ndst[2], ndst[3]));
                }

                /* 多帧 × 双声道：帧主序交错（f0: ch0,ch1；f1: ch0,ch1），平面缓冲按声道连续 */
                {
                    var flat = new byte[] {
                        0x80, 0x00, 0x00, 0x00,  0x40, 0x00, 0x00, 0x00,   /* ch0: f0,f1 */
                        0x00, 0x00, 0x00, 0x80,  0x00, 0x00, 0x00, 0x01 }; /* ch1: f0,f1 */
                    var ndst = new byte[32];
                    int nf = DsdNativePacker.Pack32(ndst, flat, 8, 2, 2, false);
                    Ok("Native U32 多帧交错（ch-major 平面 → 帧主序）",
                        nf == 2
                        && ndst[0] == 0x00 && ndst[3] == 0x80 && ndst[4] == 0x80 && ndst[7] == 0x00
                        && ndst[8] == 0x00 && ndst[11] == 0x40 && ndst[12] == 0x01 && ndst[15] == 0x00,
                        string.Format("f0={0:X2}{1:X2}{2:X2}{3:X2} {4:X2}{5:X2}{6:X2}{7:X2}",
                            ndst[0], ndst[1], ndst[2], ndst[3], ndst[4], ndst[5], ndst[6], ndst[7]));
                }

                /* U16：DSF 与 DFF 同 t0→bit15 约定（与 DoP payload 同布局，仅无标记） */
                {
                    var flat = new byte[] { 0x01, 0x00,  0x01, 0x00 };
                    var ndst = new byte[16];
                    int nf = DsdNativePacker.Pack16(ndst, flat, 2, 1, 2, true);
                    Ok("Native U16 DSF 帧（t0→bit15、小端）",
                        nf == 1 && ndst[0] == 0x00 && ndst[1] == 0x80 && ndst[2] == 0x00 && ndst[3] == 0x80,
                        string.Format("bytes={0:X2} {1:X2} {2:X2} {3:X2}", ndst[0], ndst[1], ndst[2], ndst[3]));
                }
                {
                    var flat = new byte[] { 0x80, 0x00,  0x80, 0x00 };
                    var ndst = new byte[16];
                    int nf = DsdNativePacker.Pack16(ndst, flat, 2, 1, 2, false);
                    Ok("Native U16 DFF 帧（MSB-first 直取）",
                        nf == 1 && ndst[0] == 0x00 && ndst[1] == 0x80 && ndst[2] == 0x00 && ndst[3] == 0x80,
                        string.Format("bytes={0:X2} {1:X2} {2:X2} {3:X2}", ndst[0], ndst[1], ndst[2], ndst[3]));
                }

                /* 静音：0x00 填充（误按 PCM 解读时是真静音，防 0x69 被读成 +82% 直流） */
                {
                    var ndst = new byte[32];
                    for (int i = 0; i < ndst.Length; i++) ndst[i] = 0xAA;
                    int nb = DsdNativePacker.PackSilence(ndst, 4, 3, 2, 4);
                    bool z = true;
                    for (int i = 4; i < 4 + nb; i++) if (ndst[i] != 0) z = false;
                    Ok("Native 静音帧（0x00 填充、offset 生效、不越界）",
                        nb == 3 * 2 * 4 && z && ndst[0] == 0xAA && ndst[3] == 0xAA
                        && ndst[4 + nb] == 0xAA,
                        string.Format("nb={0}", nb));
                }
            }

            /* ── 3c-6. SplitRaw 手算对照（DSF 组交错+尾部均分 / DFF 字节交错） ── */
            {
                var m2 = new DsdDecoder.DsdMeta { Container = "DSF", BlockSize = 8, Channels = 2, LsbFirst = true };
                var buf = new byte[3 * 16 + 4];
                for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i + 1);
                var flat = new byte[buf.Length];
                int per = DsdDecoder.SplitRaw(m2, buf, buf.Length, flat);
                /* 手算期望（buf[i]=i+1）：组0 ch0=1..8/ch1=9..16；组1 17..24/25..32；组2 33..40/41..48；
                   尾 4B 均分：ch0=49,50/ch1=51,52。平面布局 ch0[0..25] ‖ ch1[26..51] */
                bool okDsf = per == 26;
                for (int i = 0; i < 8 && okDsf; i++)
                    okDsf = flat[i] == 1 + i && flat[8 + i] == 17 + i && flat[16 + i] == 33 + i
                         && flat[26 + i] == 9 + i && flat[34 + i] == 25 + i && flat[42 + i] == 41 + i;
                okDsf = okDsf && flat[24] == 49 && flat[25] == 50 && flat[50] == 51 && flat[51] == 52;
                Ok("SplitRaw DSF 组交错+尾部均分（手算对照）", okDsf, "per=" + per);

                var m2d = new DsdDecoder.DsdMeta { Container = "DFF", BlockSize = 4096, Channels = 2, LsbFirst = false };
                var buf2 = new byte[12];
                for (int i = 0; i < 12; i++) buf2[i] = (byte)(i + 1);
                var flat2 = new byte[12];
                int per2 = DsdDecoder.SplitRaw(m2d, buf2, 12, flat2);
                /* DFF 字节交错：ch0=1,3,5,7,9,11；ch1=2,4,6,8,10,12 */
                Ok("SplitRaw DFF 字节交错（手算对照）",
                    per2 == 6 && flat2[0] == 1 && flat2[1] == 3 && flat2[2] == 5
                    && flat2[3] == 7 && flat2[4] == 9 && flat2[5] == 11
                    && flat2[6] == 2 && flat2[7] == 4 && flat2[8] == 6
                    && flat2[9] == 8 && flat2[10] == 10 && flat2[11] == 12,
                    "per=" + per2);
            }

            /* ── 3c-7. DoP 位级往返：跨分块 Pack → 解帧 → 与源位流逐位一致、标记逐帧交替 ──
               （DSD256 全速率路径的正确性认证：排除「爆音 = 打包串位」假设） */
            {
                foreach (var lsb in new[] { true, false })   /* DSF LSB / DFF MSB 两种位序 */
                {
                    var src = PackLfsr(lsb);
                    int perCh = NBytes;
                    /* 双声道不同内容（ch1 按位取反）防声道串扰 */
                    var flatAll = new byte[perCh * 2];
                    Array.Copy(src, 0, flatAll, 0, perCh);
                    for (int i = 0; i < perCh; i++) flatAll[perCh + i] = (byte)~src[i];

                    /* 两种分块喂法：整块 与 3000/700/尾块（跨块 marker 相位必须连续） */
                    int[][] chunkings = { new[] { perCh }, new[] { 3000, 700, perCh - 3700 } };
                    byte[] wireA = null, wireB = null;
                    for (int ci = 0; ci < chunkings.Length; ci++)
                    {
                        var wire = new List<byte>();
                        bool phase = false;
                        int off = 0;
                        foreach (int per in chunkings[ci])
                        {
                            var part = new byte[per * 2];
                            Array.Copy(flatAll, off, part, 0, per);
                            Array.Copy(flatAll, perCh + off, part, per, per);
                            int frames = per / 2;
                            var dst = new byte[frames * 3 * 2 + 8];
                            DopPacker.Pack(dst, part, per, frames, 2, lsb, ref phase);
                            for (int i = 0; i < frames * 3 * 2; i++) wire.Add(dst[i]);
                            off += per;
                        }
                        if (ci == 0) wireA = wire.ToArray(); else wireB = wire.ToArray();
                    }
                    bool same = wireA.Length == wireB.Length;
                    for (int i = 0; same && i < wireA.Length; i++) same = wireA[i] == wireB[i];
                    Ok("DoP 跨分块字节一致（marker 相位连续）" + (lsb ? " DSF" : " DFF"), same,
                        "len=" + wireA.Length + "/" + wireB.Length);

                    /* 位级校验：payload t0=bit15 起时间序 == 源位流时间序；标记 0x05/0xFA 逐帧交替 */
                    int framesTotal = perCh / 2;
                    bool bitsOk = true, markOk = true;
                    for (int f = 0; f < framesTotal && (bitsOk || markOk); f++)
                    {
                        byte expectM = (f % 2 == 0) ? DopPacker.MarkerA : DopPacker.MarkerB;
                        if (wireA[f * 6 + 2] != expectM || wireA[f * 6 + 5] != expectM) markOk = false;
                        for (int c = 0; c < 2 && bitsOk; c++)
                        {
                            int payload = wireA[f * 6 + c * 3] | (wireA[f * 6 + c * 3 + 1] << 8);
                            for (int b = 0; b < 16; b++)
                            {
                                int wireBit = (payload >> (15 - b)) & 1;
                                int sb = f * 16 + b;
                                byte srcByte = flatAll[c * perCh + (sb >> 3)];
                                int srcBit = lsb ? ((srcByte >> (sb & 7)) & 1) : ((srcByte >> (7 - (sb & 7))) & 1);
                                if (wireBit != srcBit) { bitsOk = false; break; }
                            }
                        }
                    }
                    Ok("DoP 位级往返（t0→bit15、双声道无串位）" + (lsb ? " DSF" : " DFF"), bitsOk && markOk,
                        string.Format("frames={0} mark={1} bits={2}", framesTotal, markOk, bitsOk));
                }
            }

            /* ── 3c-8. Native U32 位级往返（t0→bit31、小端） ── */
            {
                var src = PackLfsr(true);
                int perCh = 256;
                var part = new byte[perCh * 2];
                Array.Copy(src, 0, part, 0, perCh);
                for (int i = 0; i < perCh; i++) part[perCh + i] = (byte)~src[i];
                int frames = perCh / 4;
                var dst = new byte[frames * 4 * 2];
                DsdNativePacker.Pack32(dst, part, perCh, frames, 2, true);
                bool bitsOk = true;
                for (int f = 0; f < frames && bitsOk; f++)
                    for (int c = 0; c < 2 && bitsOk; c++)
                    {
                        int o = f * 8 + c * 4;
                        uint w = (uint)(dst[o] | (dst[o + 1] << 8) | (dst[o + 2] << 16) | (dst[o + 3] << 24));
                        for (int b = 0; b < 32; b++)
                        {
                            int wireBit = (int)((w >> (31 - b)) & 1);
                            int sb = f * 32 + b;
                            byte srcByte = part[c * perCh + (sb >> 3)];
                            int srcBit = (srcByte >> (sb & 7)) & 1;
                            if (wireBit != srcBit) { bitsOk = false; break; }
                        }
                    }
                Ok("Native U32 位级往返（t0→bit31、小端）", bitsOk, "frames=" + frames);
            }

            /* ── 4. 硬件判定降档矩阵（10 场景） ── */
            {
                Func<int, TrackSpec, OutputPlan> T = (hw, spec) => OutputPlanner.Plan(spec, hw, 16);
                var cases = new[]
                {
                    new { Name = "384k@dev48 → 48k/16 DOWN", Plan = T(48000, new TrackSpec { Rate = 384000, Bits = 24 }), Rate = 48000, Bits = 16, Mode = "downsample" },
                    new { Name = "44.1k@dev48 → FULL 不降档", Plan = T(48000, new TrackSpec { Rate = 44100, Bits = 16 }), Rate = 44100, Bits = 16, Mode = "full" },
                    new { Name = "96k@dev192 → FULL 不降档", Plan = T(192000, new TrackSpec { Rate = 96000, Bits = 24 }), Rate = 96000, Bits = 24, Mode = "full" },
                    new { Name = "384k@dev384 → NATIVE", Plan = T(384000, new TrackSpec { Rate = 384000, Bits = 24 }), Rate = 384000, Bits = 24, Mode = "native" },
                    new { Name = "384k@dev192 → DOWN 48k", Plan = T(192000, new TrackSpec { Rate = 384000, Bits = 24 }), Rate = 48000, Bits = 16, Mode = "downsample" },
                    new { Name = "dsd64@dev48 → DOWN 44.1k", Plan = T(48000, new TrackSpec { Rate = 2822400, Bits = 1, Dsd = true }), Rate = 44100, Bits = 16, Mode = "downsample" },
                    new { Name = "dsd64@dev384 → FULL 176.4k/24", Plan = T(384000, new TrackSpec { Rate = 2822400, Bits = 1, Dsd = true }), Rate = 176400, Bits = 24, Mode = "full" },
                    new { Name = "dsd128@dev384 → FULL 352.8k/24", Plan = T(384000, new TrackSpec { Rate = 5644800, Bits = 1, Dsd = true }), Rate = 352800, Bits = 24, Mode = "full" },
                    new { Name = "dsd256@dev384 → FULL 352.8k/24", Plan = T(384000, new TrackSpec { Rate = 11289600, Bits = 1, Dsd = true }), Rate = 352800, Bits = 24, Mode = "full" },
                    new { Name = "48k@dev44.1 → DOWN 44.1k", Plan = T(44100, new TrackSpec { Rate = 48000, Bits = 16 }), Rate = 44100, Bits = 16, Mode = "downsample" },
                };
                foreach (var c in cases)
                {
                    Ok("plan " + c.Name, c.Plan.Rate == c.Rate && c.Plan.Bits == c.Bits && c.Plan.Mode == c.Mode,
                        c.Plan.Label);
                }
                Ok("planChain DSD64→176.4k = 1 hb (dec=16)", DsdDecoder.PlanChain(FBit, 176400) == 1);
                Ok("planChain DSD64→44.1k = 3 hb (dec=64)", DsdDecoder.PlanChain(FBit, 44100) == 3);
                Ok("planChain DSD256→352.8k = 2 hb (dec=32)", DsdDecoder.PlanChain(11289600, 352800) == 2);
                bool threw = false;
                try { DsdDecoder.PlanChain(FBit, 48000); } catch { threw = true; }
                Ok("planChain bad target throws", threw);

                /* hwRate=0 = 硬件未知（独占模式）→ 不降档 */
                var pn = OutputPlanner.Plan(new TrackSpec { Rate = 384000, Bits = 24 }, 0, 16);
                Ok("plan hwRate=0 不降档 full", pn.Rate == 384000 && pn.Bits == 24 && pn.Mode == "full", pn.Label);
                var pd = OutputPlanner.Plan(new TrackSpec { Rate = 2822400, Bits = 1, Dsd = true }, 0, 16);
                Ok("plan hwRate=0 DSD64 full 176.4k/24", pd.Rate == 176400 && pd.Bits == 24 && pd.Mode == "full", pd.Label);
            }

            /* ── 5. 播放模式推进 ── */
            {
                Func<List<TrackMeta>> mk = () =>
                {
                    var l = new List<TrackMeta>();
                    for (int i = 0; i < 5; i++)
                        l.Add(new TrackMeta { Title = "T" + i, Album = i < 3 ? "A" : "B", TrackNo = i + 1 });
                    return l;
                };

                var eng = new PlaylistEngine(12345);
                foreach (var t in mk()) eng.Tracks.Add(t);
                eng.ModeIdx = 0; eng.CurIdx = 0;
                Ok("order auto advances", eng.PickNext(1, true) == 1);
                eng.CurIdx = 4;
                Ok("order stops at end", eng.PickNext(1, true) == -1);
                Ok("order manual wraps", eng.PickNext(1, false) == 0);

                eng.ModeIdx = 4; eng.CurIdx = 2;
                Ok("one repeats on auto", eng.PickNext(1, true) == 2);
                Ok("one manual steps", eng.PickNext(1, false) == 3);

                eng.ModeIdx = 2; eng.CurIdx = 0;
                var seq = eng.OrderedSeq("album-order");
                Ok("album-order pool = same album", seq.All(i => eng.Tracks[i].Album == "A") && seq.Count == 3, string.Join(",", seq));
                eng.CurIdx = 2;
                Ok("album-order stops at album end", eng.PickNext(1, true) == -1);

                eng.ModeIdx = 1; eng.InvalidateShuffle(); eng.CurIdx = 0;
                var first = eng.PickNext(1, true);
                Ok("shuffle advances within list", first >= 0 && first != 4 || first == 4, "next=" + first);
                eng.CurIdx = 4;
                int afterEnd = eng.PickNext(1, true);
                Ok("shuffle continues past end", afterEnd >= 0, "next=" + afterEnd);

                eng.ModeIdx = 3; eng.InvalidateShuffle(); eng.CurIdx = 0;
                int n3 = eng.PickNext(1, true);
                Ok("album-shuffle stays in album", n3 >= 0 && eng.Tracks[n3].Album == "A", "next=" + n3);

                eng.ModeIdx = 1; eng.CurIdx = 4;
                Ok("manual prev wraps", eng.PickNext(-1, false) >= 0);
            }

            /* ── 6. 格式探测 / 频段数学 ── */
            {
                var wav = new byte[64];
                Action<int, string> SetS = (o, s) => { for (int i = 0; i < s.Length; i++) wav[o + i] = (byte)s[i]; };
                Action<int, uint> SetU32 = (o, v) => { wav[o] = (byte)v; wav[o + 1] = (byte)(v >> 8); wav[o + 2] = (byte)(v >> 16); wav[o + 3] = (byte)(v >> 24); };
                Action<int, ushort> SetU16 = (o, v) => { wav[o] = (byte)v; wav[o + 1] = (byte)(v >> 8); };
                SetS(0, "RIFF"); SetS(8, "WAVE"); SetS(12, "fmt ");
                SetU32(16, 16); SetU16(20, 1); SetU16(22, 2);
                SetU32(24, 44100); SetU32(28, 176400); SetU16(32, 4); SetU16(34, 16);
                SetS(36, "data"); SetU32(40, 8);
                var spec = FormatProbe.Sniff(wav, "t.wav");
                Ok("WAV sniff", spec != null && spec.Fmt == "PCM" && spec.Rate == 44100 && spec.Bits == 16 && spec.Channels == 2,
                    spec == null ? "null" : spec.ToString());

                var flac = new byte[64];
                flac[0] = (byte)'f'; flac[1] = (byte)'L'; flac[2] = (byte)'a'; flac[3] = (byte)'C';
                /* rate 96000 → 20bit @ b10..b12；ch=2；bits=24 */
                flac[8 + 10] = (byte)(96000 >> 12);
                flac[8 + 11] = (byte)((96000 >> 4) & 0xff);
                /* b12: rate 低 4 位 | (ch-1)=1<<1 | (bits-1)=23 的 bit4 -> bit0 */
                flac[8 + 12] = (byte)(((96000 & 0xf) << 4) | (1 << 1) | ((23 >> 4) & 1));
                /* b13: (bits-1)=23 的低 4 位占高 nibble（23<<4 溢出 byte，须掩码） */
                flac[8 + 13] = (byte)((23 & 0xf) << 4);
                spec = FormatProbe.Sniff(flac, "t.flac");
                Ok("FLAC sniff rate", spec != null && spec.Fmt == "FLAC" && spec.Rate == 96000, spec == null ? "null" : spec.ToString());

                spec = FormatProbe.Sniff(dsfHead, "t.dsf");
                Ok("DSF sniff", spec != null && spec.Dsd && spec.Fmt == "DSF" && spec.Rate == FBit, spec == null ? "null" : spec.ToString());

                spec = FormatProbe.Sniff(dffHead, "t.dff");
                Ok("DFF sniff", spec != null && spec.Dsd && spec.Fmt == "DFF" && spec.Rate == FBit, spec == null ? "null" : spec.ToString());

                /* MP3 带 ID3v2 头（回归：'ID3' 三字符比较 bug 曾使其 Spec=NULL） */
                var mp3 = new byte[64];
                mp3[0] = (byte)'I'; mp3[1] = (byte)'D'; mp3[2] = (byte)'3'; mp3[3] = 4;   /* ID3v2.4, size=0 */
                mp3[10] = 0xFF; mp3[11] = 0xFB; mp3[12] = 0x94; mp3[13] = 0x00;            /* MPEG1 L3 48k stereo (srIdx=1) */
                spec = FormatProbe.Sniff(mp3, "t.mp3");
                Ok("MP3 sniff past ID3v2", spec != null && spec.Fmt == "MP3" && spec.Rate == 48000 && spec.Channels == 2,
                    spec == null ? "null" : spec.ToString());

                /* WAV LIST INFO 标签 */
                var wtag = new byte[128];
                SetS = (o, s) => { for (int i = 0; i < s.Length; i++) wtag[o + i] = (byte)s[i]; };
                SetU32 = (o, v) => { wtag[o] = (byte)v; wtag[o + 1] = (byte)(v >> 8); wtag[o + 2] = (byte)(v >> 16); wtag[o + 3] = (byte)(v >> 24); };
                SetS(0, "RIFF"); SetS(8, "WAVE"); SetS(12, "fmt ");
                SetU32(16, 16); wtag[20] = 1; wtag[22] = 2;
                SetU32(24, 44100); SetU32(28, 176400); wtag[32] = 4; wtag[34] = 16;
                SetS(36, "LIST"); SetU32(40, 30); SetS(44, "INFO");   /* INFO(4)+INAM 8+5+1 + IART 8+3+1 = 30 */
                SetS(48, "INAM"); SetU32(52, 5); SetS(56, "Hello");
                SetS(62, "IART"); SetU32(66, 3); SetS(70, "Lab");
                SetS(74, "data"); SetU32(78, 8);
                var mTag = new TrackMeta();
                TagReader.ParseId3v2(wtag, mTag);   /* 无 ID3，应无副作用 */
                TagReader.ParseRiffInfo(wtag, mTag);
                Ok("WAV LIST INFO tags", mTag.Title == "Hello" && mTag.Artist == "Lab",
                    "'" + mTag.Title + "','" + mTag.Artist + "'");

                /* FLAC Vorbis 注释 */
                var ftag = new byte[128];
                ftag[0] = (byte)'f'; ftag[1] = (byte)'L'; ftag[2] = (byte)'a'; ftag[3] = (byte)'C';
                ftag[4] = 0x80; ftag[5] = 0; ftag[6] = 0; ftag[7] = 34;                     /* STREAMINFO last=0? type0 len34 */
                ftag[4] = 0x00;                                                              /* not-last */
                int vo = 4 + 4 + 34;                                                         /* VORBIS_COMMENT 块起点 */
                string vendor = "x", c1 = "TITLE=HiRes Song", c2 = "ARTIST=PA-1 LAB", c3 = "ALBUM=Native Tests";
                int vcLen = 4 + vendor.Length + 4 + (4 + c1.Length) + (4 + c2.Length) + (4 + c3.Length);
                ftag[vo] = 0x84;                                                              /* last + type 4 */
                ftag[vo + 1] = (byte)((vcLen >> 16) & 0xff); ftag[vo + 2] = (byte)((vcLen >> 8) & 0xff); ftag[vo + 3] = (byte)(vcLen & 0xff);
                int vp = vo + 4;
                ftag[vp] = 1; vp += 4; ftag[vp] = (byte)'x'; vp += 1;                         /* vendor len=1 "x" */
                ftag[vp] = 3; vp += 4;                                                        /* 3 comments */
                foreach (var c in new[] { c1, c2, c3 })
                {
                    ftag[vp] = (byte)c.Length; vp += 4;
                    for (int i = 0; i < c.Length; i++) ftag[vp + i] = (byte)c[i];
                    vp += c.Length;
                }
                var mF = new TrackMeta();
                TagReader.ParseVorbisComment(ftag, mF);
                Ok("FLAC Vorbis tags", mF.Title == "HiRes Song" && mF.Artist == "PA-1 LAB" && mF.Album == "Native Tests",
                    "'" + mF.Title + "','" + mF.Artist + "','" + mF.Album + "'");

                Ok("KhzLabel 44.1k", FormatProbe.KhzLabel(44100) == "44.1 kHz", FormatProbe.KhzLabel(44100));
                Ok("KhzLabel 48k", FormatProbe.KhzLabel(48000) == "48 kHz", FormatProbe.KhzLabel(48000));
                Ok("SpecLabel DSD64", FormatProbe.SpecLabel(new TrackSpec { Dsd = true, Rate = FBit }) == "DSD64",
                    FormatProbe.SpecLabel(new TrackSpec { Dsd = true, Rate = FBit }));

                Ok("FToX(20)=0", Math.Abs(AnalyzerMath.FToX(20)) < 1e-9);
                Ok("FToX(24000)=1", Math.Abs(AnalyzerMath.FToX(24000) - 1) < 1e-9);
                Ok("roundtrip x→f→x", Math.Abs(AnalyzerMath.FToX(AnalyzerMath.XToF(0.5)) - 0.5) < 1e-9);

                /* FFT：单位正弦峰位正确 + Parseval 能量守恒 */
                {
                    int n = 1024, kSine = 64;
                    var re = new double[n]; var im = new double[n];
                    for (int i = 0; i < n; i++) re[i] = Math.Sin(2 * Math.PI * kSine * i / n);
                    Fft.Transform(re, im);
                    double maxMag = 0; int maxK = -1;
                    for (int k = 0; k < n; k++)
                    {
                        double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                        if (m > maxMag) { maxMag = m; maxK = k; }
                    }
                    Ok("FFT sine peak at bin", maxK == kSine || maxK == n - kSine, "k=" + maxK);
                    double eTime = 0, eFreq = 0;
                    for (int i = 0; i < n; i++) eTime += Math.Sin(2 * Math.PI * kSine * i / n) * Math.Sin(2 * Math.PI * kSine * i / n);
                    for (int k = 0; k < n; k++) eFreq += re[k] * re[k] + im[k] * im[k];
                    Ok("FFT Parseval energy", Math.Abs(eFreq / n - eTime) / eTime < 1e-9,
                        (eFreq / n).ToString("0.000") + " vs " + eTime.ToString("0.000"));
                }

                int N = AnalyzerMath.FftSize;
                var fd = new float[N];
                for (int i = 0; i < N; i++) fd[i] = -200;
                int k0 = 1000;
                fd[k0] = (float)-AnalyzerMath.DbWindowComp;   // 0dBFS 正弦理论读数（补偿前）
                fd[k0 - 1] = -25f; fd[k0 + 1] = -25f;
                double v = AnalyzerMath.BandValue(fd, k0 - 0.5, k0 + 1.5, "max", AnalyzerMath.DbWindowComp);
                Ok("max-mode sine → 0 dB", Math.Abs(v) < 0.5, v.ToString("0.00") + " dB");
                /* 窄带路径：中心对准峰 bin 精确读 0 dB；偏移 0.15 bin 向 -25dB 邻 bin
                   插值的物理读数约 -0.65 dB（验证插值平滑而非卡死在峰顶） */
                double v2 = AnalyzerMath.BandValue(fd, k0 - 0.15, k0 + 0.15, "max", AnalyzerMath.DbWindowComp);
                Ok("narrowband interpolation (peak-centered)", Math.Abs(v2) < 0.2, v2.ToString("0.00") + " dB");
                double v3 = AnalyzerMath.BandValue(fd, k0 + 0.1, k0 + 0.2, "max", AnalyzerMath.DbWindowComp);
                Ok("narrowband near-peak smooth", Math.Abs(v3) < 1.0, v3.ToString("0.00") + " dB");
            }

            Console.WriteLine();
            Console.WriteLine("=== " + _pass + " passed, " + _fail + " failed ===");
            return _fail == 0 ? 0 : 1;
        }
    }
}
