using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenShot
{
    class Program
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        static void Main(string[] args)
        {
            SetProcessDPIAware();   /* 物理像素抓全窗口，防 PrintWindow 只截左上角 */
            string outPath = args.Length > 0 ? args[0] : "shot.png";
            string procName = args.Length > 1 ? args[1] : "";
            string keys = args.Length > 2 ? args[2] : "";   /* 如 "2" 或 "T"，先聚焦再按键 */

            IntPtr target = IntPtr.Zero;
            if (!string.IsNullOrEmpty(procName))
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    Console.WriteLine("proc: " + p.ProcessName + " pid=" + p.Id + " hwnd=" + p.MainWindowHandle);
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        target = p.MainWindowHandle;
                        break;
                    }
                }
                /* 找不到 MainWindowHandle 时按标题枚举 */
                if (target == IntPtr.Zero)
                {
                    EnumWindows((h, l) =>
                    {
                        if (!IsWindowVisible(h)) return true;
                        var sb = new System.Text.StringBuilder(256);
                        GetWindowText(h, sb, 256);
                        string title = sb.ToString();
                        if (title.IndexOf("G# minor", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("PHOSPHOR", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf(procName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            target = h;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                }
            }

            if (target != IntPtr.Zero)
            {
                if (IsIconic(target)) ShowWindow(target, 9);
                SetForegroundWindow(target);
                Thread.Sleep(800);
                if (!string.IsNullOrEmpty(keys))
                {
                    /* 指令语法（空格分隔）："2" 按键  "click:x,y" 客户区左键单击  "dbl:x,y" 双击  "wait:ms" 等待 */
                    foreach (var tok in keys.Split(' '))
                    {
                        if (tok.StartsWith("wait:"))
                        {
                            Thread.Sleep(int.Parse(tok.Substring(5)));
                        }
                        else if (tok.StartsWith("click:") || tok.StartsWith("dbl:"))
                        {
                            bool dbl = tok.StartsWith("dbl:");
                            var xy = tok.Substring(tok.IndexOf(':') + 1).Split(',');
                            var pt = new POINT { X = int.Parse(xy[0]), Y = int.Parse(xy[1]) };
                            ClientToScreen(target, ref pt);          /* 客户区坐标 → 屏幕坐标 */
                            SetCursorPos(pt.X, pt.Y);
                            Thread.Sleep(120);
                            int clicks = dbl ? 2 : 1;
                            for (int c = 0; c < clicks; c++)
                            {
                                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                                Thread.Sleep(dbl ? 120 : 250);
                            }
                        }
                        else if (tok.StartsWith("vk:"))
                        {
                            /* 原始虚拟键码（如 vk:32 = SPACE），空格类按键无法用字符语法表达 */
                            short vk = short.Parse(tok.Substring(3));
                            keybd_event((byte)vk, 0, 0, IntPtr.Zero);
                            keybd_event((byte)vk, 0, 2, IntPtr.Zero);
                            Thread.Sleep(250);
                        }
                        else
                        {
                            foreach (char k in tok)
                            {
                                short vk = (short)Char.ToUpperInvariant(k);
                                keybd_event((byte)vk, 0, 0, IntPtr.Zero);
                                keybd_event((byte)vk, 0, 2, IntPtr.Zero);
                                Thread.Sleep(250);
                            }
                        }
                    }
                    Thread.Sleep(500);
                }
                RECT r;
                GetWindowRect(target, out r);
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w > 0 && h > 0)
                {
                    /* PrintWindow 直接渲染目标窗口（不被遮挡干扰）；
                       CopyFromScreen 会抓到 Z 序上层的其它窗口 */
                    using (var bmp = new Bitmap(w, h))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(target, hdc, 2 /* PW_RENDERFULLCONTENT */);
                        g.ReleaseHdc(hdc);
                        bmp.Save(outPath, ImageFormat.Png);
                    }
                    Console.WriteLine("saved window " + w + "x" + h + " -> " + outPath);
                    return;
                }
            }
            Console.WriteLine("window not found, fallback fullscreen");
            var b = SystemInformation.VirtualScreen;
            using (var bmp = new Bitmap(b.Width, b.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
                bmp.Save(outPath);
            }
            Console.WriteLine("saved " + outPath);
        }
    }
}
