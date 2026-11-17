using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Pa1.App
{
    /// <summary>SkiaSharp 原生库引导。
    /// 目录部署：直接加载 exe 旁 x64\ / x86\ 子目录（NuGet 标准布局）。
    /// 单文件部署：从内嵌资源抽出到 %LOCALAPPDATA%\GSharpMinor\ 再 LoadLibrary；
    /// 已加载的同名模块会让后续 P/Invoke("libSkiaSharp") 直接命中，无需 SkiaSharp 探测。</summary>
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

                /* 2) 单文件部署：内嵌资源抽出 */
                string resName = is64
                    ? "Pa1.App.native.libSkiaSharp-x64.dll"
                    : "Pa1.App.native.libSkiaSharp-x86.dll";
                var asm = typeof(NativeSkia).Assembly;
                using (var s = asm.GetManifestResourceStream(resName))
                {
                    if (s == null) return;
                    string dir = DataDirs.Local;
                    Directory.CreateDirectory(dir);
                    string dst = Path.Combine(dir, "libSkiaSharp.dll");
                    try
                    {
                        if (!File.Exists(dst) || new FileInfo(dst).Length != s.Length)
                        {
                            using (var fs = File.Create(dst)) s.CopyTo(fs);
                        }
                    }
                    catch (IOException)
                    {
                        /* 另一实例占用 → 独立文件名 */
                        dst = Path.Combine(dir, "libSkiaSharp-" +
                            Guid.NewGuid().ToString("N").Substring(0, 8) + ".dll");
                        using (var fs = File.Create(dst)) s.CopyTo(fs);
                    }
                    LoadLibrary(dst);
                }
            }
            catch { /* 加载失败由 SkiaSharp 自身异常路径接管 */ }
        }
    }
}
