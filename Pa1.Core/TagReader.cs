using System;
using System.IO;
using System.Text;

namespace Pa1.Core
{
    /// <summary>曲目元数据：ID3v2（TIT2/TPE1/TALB/TRCK）+ ID3v1 + 文件名兜底。</summary>
    public sealed class TrackMeta
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public int TrackNo;
        public TrackSpec Spec;
    }

    public static class TagReader
    {
        /// <summary>从文件读取规格 + 标签（只读前 256KB + 尾部 128B，不做全文件扫描）。</summary>
        public static TrackMeta Read(string path)
        {
            var outMeta = new TrackMeta();
            var fi = new FileInfo(path);
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    int n = (int)Math.Min(262144, fs.Length);
                    var head = new byte[n];
                    int got = 0;
                    while (got < n)
                    {
                        int r = fs.Read(head, got, n - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    outMeta.Spec = FormatProbe.Sniff(head, Path.GetFileName(path));
                    ParseId3v2(head, outMeta);
                    ParseVorbisComment(head, outMeta);   /* FLAC/OGG：TITLE/ARTIST/ALBUM */
                    ParseRiffInfo(head, outMeta);        /* WAV：LIST INFO INAM/IART/IPRD */

                    /* DSF 的 ID3v2 标签通常在文件尾（metadataOffset 指向） */
                    if (outMeta.Spec != null && outMeta.Spec.Fmt == "DSF" && outMeta.Spec.Id3Offset > 0 && outMeta.Spec.Id3Offset < fs.Length)
                    {
                        fs.Position = outMeta.Spec.Id3Offset;
                        int m = (int)Math.Min(262144, fs.Length - outMeta.Spec.Id3Offset);
                        var mid = new byte[m];
                        got = 0;
                        while (got < m)
                        {
                            int r = fs.Read(mid, got, m - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        ParseId3v2(mid, outMeta);
                    }

                    /* ID3v1 兜底（文件尾 128 字节） */
                    if (string.IsNullOrEmpty(outMeta.Title) && fs.Length > 128)
                    {
                        fs.Position = fs.Length - 128;
                        var tail = new byte[128];
                        fs.Read(tail, 0, 128);
                        if (tail[0] == 0x54 && tail[1] == 0x41 && tail[2] == 0x47)   /* 'TAG' */
                        {
                            if (string.IsNullOrEmpty(outMeta.Title)) outMeta.Title = S1(tail, 3, 33);
                            if (string.IsNullOrEmpty(outMeta.Artist)) outMeta.Artist = S1(tail, 33, 63);
                            if (string.IsNullOrEmpty(outMeta.Album)) outMeta.Album = S1(tail, 63, 93);
                        }
                    }
                }
            }
            catch { /* 读取失败不阻塞导入 */ }

            /* 文件名兜底："01 - Title" / "01. Title" / "Artist - Title" */
            if (string.IsNullOrEmpty(outMeta.Title))
            {
                string baseName = Path.GetFileNameWithoutExtension(path);
                var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^(\d{1,3})\s*[-.–_]\s*(.+)$");
                if (m.Success)
                {
                    if (outMeta.TrackNo == 0)
                    {
                        int t2;
                        if (int.TryParse(m.Groups[1].Value, out t2)) outMeta.TrackNo = t2;
                    }
                    baseName = m.Groups[2].Value;
                }
                var dash = System.Text.RegularExpressions.Regex.Split(baseName, @"\s+[-–—]\s+");
                if (dash.Length >= 2 && string.IsNullOrEmpty(outMeta.Artist))
                {
                    outMeta.Artist = dash[0];
                    baseName = string.Join(" - ", dash, 1, dash.Length - 1);
                }
                outMeta.Title = baseName.Trim();
            }

            /* BOM/空白清理（ID3v2 UTF-16 解码常带 U+FEFF 前缀） */
            outMeta.Title = (outMeta.Title ?? "").Trim('\uFEFF', '\u200B', ' ', '\t');
            outMeta.Artist = (outMeta.Artist ?? "").Trim('\uFEFF', '\u200B', ' ', '\t');
            outMeta.Album = (outMeta.Album ?? "").Trim('\uFEFF', '\u200B', ' ', '\t');

            return outMeta;
        }

        static string S1(byte[] b, int a, int z)
        {
            var sb = new StringBuilder();
            for (int i = a; i < z && i < b.Length; i++)
            {
                if (b[i] == 0) break;
                sb.Append((char)b[i]);
            }
            return sb.ToString().Trim();
        }

        static string Sn(byte[] b, int off, int len)
        {
            int z = Math.Min(off + len, b.Length);
            return S1(b, off, z);
        }

        static uint U32le(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        /// <summary>FLAC/OGG Vorbis 注释：TITLE/ARTIST/ALBUM/TRACKNUMBER（已有一值不覆盖）。</summary>
        public static void ParseVorbisComment(byte[] head, TrackMeta m)
        {
            try
            {
                int o = -1;
                if (head.Length > 8 && head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') o = 4;
                else if (head.Length > 30 && head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S') return; /* OGG 页结构复杂，跳过 */
                if (o < 0) return;

                while (o + 4 <= head.Length)
                {
                    int type = head[o] & 0x7f;
                    int len = (head[o + 1] << 16) | (head[o + 2] << 8) | head[o + 3];
                    int body = o + 4;
                    if (len < 0 || body + len > head.Length) return;
                    if (type == 4)   /* VORBIS_COMMENT */
                    {
                        int p = body;
                        if (p + 4 > body + len) return;
                        int vlen = (int)U32le(head, p); p += 4 + vlen;
                        if (p + 4 > body + len) return;
                        int cnt = (int)U32le(head, p); p += 4;
                        for (int i = 0; i < cnt && p + 4 <= body + len; i++)
                        {
                            int cl = (int)U32le(head, p); p += 4;
                            if (cl < 0 || p + cl > body + len) break;
                            string s = Encoding.UTF8.GetString(head, p, cl); p += cl;
                            int eq = s.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = s.Substring(0, eq).Trim().ToUpperInvariant();
                            string val = s.Substring(eq + 1).Trim();
                            if (val.Length == 0) continue;
                            if (key == "TITLE" && string.IsNullOrEmpty(m.Title)) m.Title = val;
                            else if (key == "ARTIST" && string.IsNullOrEmpty(m.Artist)) m.Artist = val;
                            else if (key == "ALBUM" && string.IsNullOrEmpty(m.Album)) m.Album = val;
                            else if (key == "TRACKNUMBER" && m.TrackNo == 0)
                            {
                                int t;
                                if (int.TryParse(val.Split('/')[0], out t)) m.TrackNo = t;
                            }
                        }
                        return;
                    }
                    if ((head[o] & 0x80) != 0) return;   /* last block */
                    o = body + len;
                }
            }
            catch { }
        }

        /// <summary>WAV LIST INFO：INAM 标题 / IART 艺术家 / IPRD 专辑。</summary>
        public static void ParseRiffInfo(byte[] head, TrackMeta m)
        {
            try
            {
                if (head.Length < 12) return;
                if (!(head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')) return;
                if (!(head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E')) return;
                int o = 12;
                while (o + 8 <= head.Length)
                {
                    string id = Sn(head, o, 4);
                    uint sz = U32le(head, o + 4);
                    int body = o + 8;
                    if (sz > head.Length || body + sz > head.Length) return;
                    if (id == "LIST" && sz >= 4 && Sn(head, body, 4) == "INFO")
                    {
                        int p = body + 4;
                        while (p + 8 <= body + sz)
                        {
                            string sid = Sn(head, p, 4);
                            uint ssz = U32le(head, p + 4);
                            int sbody = p + 8;
                            if (sbody + ssz > body + sz) break;
                            string txt = ssz > 0 ? Sn(head, sbody, (int)ssz) : "";
                            if (txt.Length > 0)
                            {
                                if (sid == "INAM" && string.IsNullOrEmpty(m.Title)) m.Title = txt;
                                else if (sid == "IART" && string.IsNullOrEmpty(m.Artist)) m.Artist = txt;
                                else if (sid == "IPRD" && string.IsNullOrEmpty(m.Album)) m.Album = txt;
                            }
                            p = sbody + (int)ssz + (int)(ssz & 1);
                        }
                        return;
                    }
                    o = body + (int)sz + (int)(sz & 1);
                }
            }
            catch { }
        }

        static string DecodeText(byte[] b)
        {
            if (b == null || b.Length < 2) return "";
            byte enc = b[0];
            int len = b.Length - 1;
            try
            {
                string s;
                if (enc == 0) s = Encoding.GetEncoding("iso-8859-1").GetString(b, 1, len);
                else if (enc == 3) s = Encoding.UTF8.GetString(b, 1, len);
                else if (enc == 1) s = Encoding.Unicode.GetString(b, 1, len);
                else s = Encoding.BigEndianUnicode.GetString(b, 1, len);
                return s.TrimEnd('\0').Trim();
            }
            catch { return ""; }
        }

        public static void ParseId3v2(byte[] head, TrackMeta outMeta)
        {
            if (head == null || head.Length < 10) return;
            if (!(head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33)) return;
            byte ver = head[3];
            int size = ((head[6] & 0x7f) << 21) | ((head[7] & 0x7f) << 14) | ((head[8] & 0x7f) << 7) | (head[9] & 0x7f);
            int off = 10;
            int end = Math.Min(10 + size, head.Length);
            while (off + 10 <= end)
            {
                string id = new string(new[] { (char)head[off], (char)head[off + 1], (char)head[off + 2], (char)head[off + 3] });
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Z0-9]{4}$")) break;
                int fs;
                if (ver >= 4)
                    fs = ((head[off + 4] & 0x7f) << 21) | ((head[off + 5] & 0x7f) << 14) | ((head[off + 6] & 0x7f) << 7) | (head[off + 7] & 0x7f);
                else
                    fs = (head[off + 4] << 24) | (head[off + 5] << 16) | (head[off + 6] << 8) | head[off + 7];
                if (fs <= 0 || off + 10 + fs > head.Length) break;
                var body = new byte[fs];
                Array.Copy(head, off + 10, body, 0, fs);
                if (id == "TIT2") outMeta.Title = DecodeText(body);
                else if (id == "TPE1") outMeta.Artist = DecodeText(body);
                else if (id == "TALB") outMeta.Album = DecodeText(body);
                else if (id == "TRCK")
                {
                    int t;
                    if (int.TryParse(DecodeText(body), out t)) outMeta.TrackNo = t;
                }
                off += 10 + fs;
            }
        }
    }
}
