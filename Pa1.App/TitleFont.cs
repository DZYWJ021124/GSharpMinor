using System;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;

namespace Pa1.App
{
    /// <summary>曲名字体（Pa1 Serif SC）的运行时引导。
    ///
    /// 字体 22MB 未压缩（基本区 + 扩展A + 假名 + 全部韩文音节），WPF 的 Resource 不做压缩，
    /// 直接内嵌会让 exe 涨到 32MB。所以和 SkiaSharp 原生库同一套做法：构建期 Deflate
    /// （_PackTitleFont），运行时解压到 %LOCALAPPDATA%\GSharpMinor 再用 file:// URI 挂载，
    /// 缓存戳为「资源长度|程序集版本|解压后长度」。
    ///
    /// 为什么不能用 pack:// Resource：WPF 只能从流以外的位置加载字体，
    /// 而 Resource 是嵌在程序集 .g.resources 里的字节流，没有可用的文件路径。</summary>
    internal static class TitleFont
    {
        const string ResName = "Pa1.App.fonts.Pa1SerifSC.ttf.deflate";
        const string FamilyName = "Pa1 Serif SC";   /* 与 scripts/make_font.py 里的家族名一致 */

        static bool _tried;
        static FontFamily _family;

        /// <summary>已就绪的字体族；解压或加载失败返回 null（调用方保持默认字体即可）。</summary>
        public static FontFamily Family
        {
            get
            {
                if (_tried) return _family;
                _tried = true;
                try { _family = Load(); }
                catch { _family = null; }
                return _family;
            }
        }

        /// <summary>解压后的 TTF 文件路径（SkiaTitle 直接按文件加载 typeface）。失败返回 null。</summary>
        public static string FilePath
        {
            get { try { return Extract(); } catch { return null; } }
        }

        static FontFamily Load()
        {
            string path = Extract();
            if (path == null) return null;

            /* 目录形式的基址 + "./#家族名"：WPF 会按家族名在该目录解析字体文件。
               用 TryGetGlyphTypeface 探一次，确认家族真的加载成功——
               否则 new FontFamily 不会抛异常，只会在渲染时静默回退。 */
            var dirUri = new Uri(Path.GetDirectoryName(path) + Path.DirectorySeparatorChar.ToString());
            var ff = new FontFamily(dirUri, "./#" + FamilyName);
            foreach (var tf in ff.GetTypefaces())
            {
                GlyphTypeface gt;
                if (tf.TryGetGlyphTypeface(out gt)) return ff;
            }
            return null;
        }

        static string Extract()
        {
            var asm = typeof(TitleFont).Assembly;
            using (var s = asm.GetManifestResourceStream(ResName))
            {
                if (s == null) return null;          /* 老构建或资源被裁掉 → 不阻断启动 */

                string dir = DataDirs.Local;
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, "Pa1SerifSC.ttf");
                string stamp = dst + ".stamp";
                string want = s.Length + "|" + asm.GetName().Version;

                long have = File.Exists(dst) ? new FileInfo(dst).Length : -1;
                if (have > 0 && ReadStamp(stamp) == want + "|" + have) return dst;

                string tmp = dst + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                using (var fs = File.Create(tmp))
                using (var zip = new DeflateStream(s, CompressionMode.Decompress))
                {
                    zip.CopyTo(fs);
                }
                long len = new FileInfo(tmp).Length;

                try
                {
                    /* 运行中的实例会持有同名字体文件（WPF 内存映射），删不掉 */
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                }
                catch (IOException)
                {
                    return Unique(dir, tmp);
                }
                catch (UnauthorizedAccessException)
                {
                    return Unique(dir, tmp);
                }

                try { File.WriteAllText(stamp, want + "|" + len); } catch { }
                return dst;
            }
        }

        static string Unique(string dir, string tmp)
        {
            string p = Path.Combine(dir, "Pa1SerifSC-" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".ttf");
            File.Move(tmp, p);
            return p;
        }

        static string ReadStamp(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }
    }
}
