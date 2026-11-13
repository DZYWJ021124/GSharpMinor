# -*- coding: utf-8 -*-
"""verify_end_dynamic.py - 动态窗口定位的结束时间验收（适配任意分辨率/DPI）。
用法: python verify_end_dynamic.py <曲目绝对路径> <目标比例 0-1> <等待秒>
手势: 在轨道上按下 → 拖到目标比例 → 松手 → 等播放自然结束 → 读 [seek]/[end]/[len] 日志。
轨道几何按 DIP 硬编码（ScrubTrack x 55-1243, y 中心 613），随窗口 rect 自动换算。
"""
import sys, time, subprocess, ctypes, re, os
from ctypes import wintypes

ctypes.windll.user32.SetProcessDPIAware()
u = ctypes.windll.user32

APP = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\pa1-native\dist\PHOSPHOR-PA1.exe"
LOG = os.path.join(os.environ.get("TEMP", "."), "pa1-native-crash.log")

# 窗口内 ScrubTrack 的 DIP 几何（200% DPI 实测 110-2486 / 1226 物理 → ÷2）
TRACK_X0, TRACK_X1 = 55, 1243
TRACK_Y = 613


class RECT(ctypes.Structure):
    _fields_ = [("l", wintypes.LONG), ("t", wintypes.LONG),
                ("r", wintypes.LONG), ("b", wintypes.LONG)]


def find_pa1():
    res = []
    def cb(hwnd, _):
        buf = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(hwnd, buf, 256)
        if u.IsWindowVisible(hwnd) and ("PHOSPHOR" in buf.value.upper() or "PA-1" in buf.value.upper()):
            r = RECT(); u.GetWindowRect(hwnd, ctypes.byref(r))
            res.append((hwnd, r))
        return True
    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    u.EnumWindows(WNDENUMPROC(cb), 0)
    return res[0] if res else (None, None)


def foreground(hwnd):
    u.keybd_event(0x12, 0, 0, 0); u.keybd_event(0x12, 0, 2, 0)  # ALT trick
    u.SetForegroundWindow(hwnd); u.ShowWindow(hwnd, 9)
    time.sleep(0.8)


def click_drag(x_from, x_to, y):
    u.SetCursorPos(int(x_from), int(y)); time.sleep(0.15)
    u.mouse_event(0x0002, 0, 0, 0, 0)            # LEFTDOWN
    time.sleep(0.1)
    steps = 12
    for i in range(1, steps + 1):
        xx = x_from + (x_to - x_from) * i / steps
        u.SetCursorPos(int(xx), int(y)); time.sleep(0.03)
    time.sleep(0.15)
    u.mouse_event(0x0004, 0, 0, 0, 0)            # LEFTUP


def main():
    track, ratio, wait = sys.argv[1], float(sys.argv[2]), int(sys.argv[3])
    subprocess.Popen([APP, "--page=analyzer", "--theme=light", "--vol=0", "--no-save",
                      "--add=" + track, "--play"])
    time.sleep(7)
    hwnd, r = find_pa1()
    if not hwnd:
        print("FATAL: PA-1 window not found"); return 1
    print("window:", (r.l, r.t, r.r, r.b))
    foreground(hwnd)

    # 安全检查：确认前台确实是 PA-1，避免点击落进全屏游戏
    fg = u.GetForegroundWindow()
    if fg != hwnd:
        buf = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(fg, buf, 256)
        print("FATAL: foreground is %r, not PA-1 - abort to avoid clicking into it" % buf.value[:30])
        subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
        return 1
    # DIP -> 物理: 用窗口实际宽 / 1299 DIP 求缩放
    scale = (r.r - r.l) / 1299.0
    wx, wy = r.l, r.t
    ty = wy + int(TRACK_Y * scale)
    # 按下点：轨道 88% 处，拖到目标比例
    x_press = wx + int((TRACK_X0 + 0.88 * (TRACK_X1 - TRACK_X0)) * scale)
    x_target = wx + int((TRACK_X0 + ratio * (TRACK_X1 - TRACK_X0)) * scale)
    print("seek gesture: press x=%d -> drag x=%d y=%d (scale=%.2f)" % (x_press, x_target, ty, scale))
    click_drag(x_press, x_target, ty)

    t0 = time.time()
    time.sleep(wait)
    print("waited %ds" % wait)

    # 读日志
    txt = open(LOG, encoding="utf-8", errors="ignore").read() if os.path.exists(LOG) else ""
    tail = txt[-6000:]
    for ln in tail.splitlines():
        if re.search(r"\[seek\]|\[mfs-seek\]|\[end\]|\[len\]|mfs-cap", ln):
            print("LOG:", ln.strip())
    m_end = re.findall(r"\[end\] pos=([\d.]+) len=([\d.]+)", txt)
    if m_end:
        pos, ln_ = m_end[-1]
        err = abs(float(pos) - float(ln_))
        print("RESULT: end pos=%s len=%s err=%.2fs %s" %
              (pos, ln_, err, "PASS" if err < 0.5 else "FAIL"))
    else:
        print("RESULT: NO [end] within %ds (may need longer wait)" % wait)
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"],
                   capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
