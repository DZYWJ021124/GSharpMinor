using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Pa1.App
{
    /// <summary>SkiaSharp 原生库引导。
    /// 目录部署：直接加载 exe 旁 x64\ / x86\ 子目录（NuGet 标准布局）。
    /// 单文件部署：从内嵌的 Deflate 资源解压到 %LOCALAPPDATA%\GSharpMinor\ 再 LoadLibrary；
    /// 已加载的同名模块会让后续 P/Invoke("libSkiaSharp") 直接命中，无需 SkiaSharp 探测。
    /// 内嵌的是压缩流（构建期 _PackNativeSkia 生成），且只含目标平台那一份（约 3.7MB，原先两份 17.4MB）。
    /// 首启多花一次解压（几十毫秒），之后按缓存戳直接命中。</summary>
    internal static class NativeSkia
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string path);

        static bool _done;

        public static void EnsureLoaded()
        {
            if (_done) return;
            _done = true;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                bool is64 = Environment.Is64BitProcess;

                /* 1) 目录部署：exe 旁 x64\ / x86\ */
                string side = Path.Combine(baseDir, is64 ? "x64" : "x86", "libSkiaSharp.dll");
                if (File.Exists(side)) { LoadLibrary(side); return; }
                string flat = Path.Combine(baseDir, "libSkiaSharp.dll");
                if (File.Exists(flat)) { LoadLibrary(flat); return; }

                /* 2) 单文件部署：解压内嵌 Deflate 资源 */
                string resName = is64
                    ? "Pa1.App.native.libSkiaSharp-x64.dll.deflate"
                    : "Pa1.App.native.libSkiaSharp-x86.dll.deflate";
                var asm = typeof(NativeSkia).Assembly;
                using (var s = asm.GetManifestResourceStream(resName))
                {
                    /* 只嵌目标平台那一份：取不到说明构建平台与本进程位数不符（如 x64 构建装进 32 位进程），
                       交给 SkiaSharp 自己的异常路径报错，不再猜测加载另一份。 */
                    if (s == null) return;
                    string dir = DataDirs.Local;
                    Directory.CreateDirectory(dir);
                    string dst = Path.Combine(dir, "libSkiaSharp.dll");

                    /* 缓存戳：资源长度|程序集版本|解压后长度。三者全等才复用已解压文件——
                       资源换了（换 SkiaSharp 版本）或上次写到一半（长度不符）都会重解压。 */
                    string stamp = dst + ".stamp";
                    string want = s.Length + "|" + asm.GetName().Version;
                    long have = File.Exists(dst) ? new FileInfo(dst).Length : -1;
                    if (have > 0 && ReadStamp(stamp) == want + "|" + have)
                    {
                        LoadLibrary(dst);
                        return;
                    }

                    string tmp = dst + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                    using (var fs = File.Create(tmp))
                    using (var zip = new DeflateStream(s, CompressionMode.Decompress))
                    {
                        zip.CopyTo(fs);
                    }
                    long len = new FileInfo(tmp).Length;
                    bool canonical = true;
                    try
                    {
                        /* 旧实例可能还映射着同名 DLL（文件锁）→ File.Delete 失败即改走独立文件名，
                           不覆盖、不中断：多实例各用各的解压副本。 */
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(tmp, dst);
                    }
                    catch (IOException) { canonical = false; }
                    catch (UnauthorizedAccessException) { canonical = false; }

                    if (!canonical)
                    {
                        dst = Path.Combine(dir, "libSkiaSharp-" +
                            Guid.NewGuid().ToString("N").Substring(0, 8) + ".dll");
                        File.Move(tmp, dst);
                    }
                    else
                    {
                        /* 戳写不进去不算失败：DLL 已就位，最坏情况只是下次再解压一遍 */
                        try { File.WriteAllText(stamp, want + "|" + len); } catch { }
                    }
                    LoadLibrary(dst);
                }
            }
            catch { /* 加载失败由 SkiaSharp 自身异常路径接管 */ }
        }

        static string ReadStamp(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }
    }
}
