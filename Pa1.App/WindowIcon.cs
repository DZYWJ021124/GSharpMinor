using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Pa1.App
{
    /// <summary>窗口图标（任务栏 / Alt-Tab / 窗口菜单）。
    ///
    /// 为什么不用 Window.Icon：WPF 会把该属性里的位图**按原尺寸**做成一个 HICON 交给系统，
    /// 1024px 的源图在任务栏 48px 处就是一步 21:1 的缩放，边缘必然发糊——源图越大不代表越清晰，
    /// 关键在「交给系统的 HICON 尺寸是否合适」。这里改为从多尺寸 ICO（16…256 各一帧）里
    /// 按当前 DPI 取对应帧，用 WM_SETICON 发尺寸正确的 HICON，系统只需 1:1 或 2:1 缩放。
    ///
    /// 用 CreateIconFromResourceEx 而不是 System.Drawing.Icon：ICO 帧是 PNG 压缩的，
    /// System.Drawing 对 PNG 帧支持不全；这个 API 就是资源管理器加载 Vista 图标组的同一条路径。</summary>
    internal static class WindowIcon
    {
        [DllImport("user32", SetLastError = true)]
        static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, bool fIcon,
                                                      uint dwVer, int cx, int cy, uint flags);
        [DllImport("user32")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32")] static extern uint GetDpiForWindow(IntPtr hwnd);

        const int WM_SETICON = 0x0080;
        const int ICON_SMALL = 0, ICON_BIG = 1;
        const uint DefaultIconVer = 0x00030000;

        /* WM_SETICON 的 HICON 归应用所有：自己留着句柄，换图标时自己销毁。 */
        static IntPtr _small, _big;

        /// <summary>按主题换窗口图标。hwnd 未建立（构造期）时静默跳过，SourceInitialized 会再来一次。</summary>
        public static void Apply(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var frames = Load(dark ? "Assets/icon-dark.ico" : "Assets/icon-light.ico");
                if (frames.Count == 0) return;
                int dpi = 96;
                try
                {
                    uint d = GetDpiForWindow(hwnd);
                    if (d >= 96) dpi = (int)d;
                }
                catch { }

                /* ICON_SMALL：标题栏/小任务栏（SM_CXSMICON ≈ 16 逻辑像素）；
                   ICON_BIG：任务栏按钮与 Alt-Tab（Win11 大图标约 64 逻辑像素）。 */
                Set(hwnd, ICON_SMALL, ref _small, Create(frames, 16 * dpi / 96));
                Set(hwnd, ICON_BIG, ref _big, Create(frames, 64 * dpi / 96));
            }
            catch { /* 图标不在功能路径上：失败就退回系统默认 */ }
        }

        static void Set(IntPtr hwnd, int which, ref IntPtr slot, IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return;
            var old = slot;
            slot = hIcon;
            SendMessage(hwnd, WM_SETICON, (IntPtr)which, hIcon);
            if (old != IntPtr.Zero) { try { DestroyIcon(old); } catch { } }
        }

        /// <summary>取「不小于目标尺寸的最小帧」；都比目标小时用最大帧（宁缩不放）。</summary>
        static IntPtr Create(List<KeyValuePair<int, byte[]>> frames, int want)
        {
            KeyValuePair<int, byte[]> pick = default(KeyValuePair<int, byte[]>);
            bool found = false;
            foreach (var f in frames)
                if (f.Key >= want && (!found || f.Key < pick.Key)) { pick = f; found = true; }
            if (!found)
                foreach (var f in frames)
                    if (!found || f.Key > pick.Key) { pick = f; found = true; }
            if (!found) return IntPtr.Zero;
            return CreateIconFromResourceEx(pick.Value, (uint)pick.Value.Length, true, DefaultIconVer, 0, 0, 0);
        }

        /// <summary>读 ICO 目录（ICONDIR + ICONDIRENTRY×n），把每帧字节单独取出来。</summary>
        static List<KeyValuePair<int, byte[]>> Load(string relativeUri)
        {
            var list = new List<KeyValuePair<int, byte[]>>();
            var res = Application.GetResourceStream(new Uri(relativeUri, UriKind.Relative));
            if (res == null) return list;
            byte[] buf;
            using (var s = res.Stream)
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                buf = ms.ToArray();
            }
            if (buf.Length < 6) return list;
            int count = BitConverter.ToUInt16(buf, 4);
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                if (e + 16 > buf.Length) break;
                int w = buf[e];
                if (w == 0) w = 256;                    /* ICONDIRENTRY 用 0 表示 256 */
                int bytes = BitConverter.ToInt32(buf, e + 8);
                int off = BitConverter.ToInt32(buf, e + 12);
                if (bytes <= 0 || off <= 0 || off + bytes > buf.Length) continue;
                var data = new byte[bytes];
                Buffer.BlockCopy(buf, off, data, 0, bytes);
                list.Add(new KeyValuePair<int, byte[]>(w, data));
            }
            return list;
        }
    }
}
