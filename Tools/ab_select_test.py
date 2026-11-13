# -*- coding: utf-8 -*-
"""A/B 验收：曲目行选中高亮（任务 #44）+ 文字垂直居中（#45）+ 主题跟随
v2 修正：
  1) 点击点 x≈1421 落在 bilibili 浮窗(1085-1998)上 -> 改点 x≈2200（浮窗右侧文字间隙）
  2) 采样带 x 660-700 在行内文字区/面板缘 -> 改 x 2120-2280 净背景区
  3) 不再落盘全屏截图（含 bilibili 浮窗内容会被内容过滤拦截）——只截窗口 bbox、
     只存两条采样带小图（均在浮窗 x 范围之外）
  4) 文字色改按「与背景距离」提取——浅色主题文字比背景深，旧的 sum>260 亮像素法失效
  5) 新增 light-click 浅色 A/B（S0 无选中 -> 点行 -> S1）验证 DynamicResource 随主题
用法：
  python ab_select_test.py dark-click | light-click | light-play
"""
import ctypes, subprocess, time, sys, os
from collections import Counter
from PIL import ImageGrab

ctypes.windll.user32.SetProcessDPIAware()

EXE = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\pa1-native\dist\PHOSPHOR-PA1.exe"
WAV = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\dev-tests-html\test-sine-1k-0dbfs.wav"
TMP = r"C:\Users\tzxy1\AppData\Local\Temp"

# 主题真源 Theme.cs 期望色（±4 容差覆盖 WPF 舍入）
EXPECT = {
    "dark":  {"s0bg": (18, 20, 13),   "s0tx": (233, 231, 220),
              "s1bg": (31, 90, 49),   "s1tx": (61, 255, 133)},
    "light": {"s0bg": (233, 229, 214), "s0tx": (35, 38, 28),
              "s1bg": (167, 197, 168), "s1tx": (12, 122, 61)},
}
TOL = 4

def kill_all():
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"],
                   capture_output=True)
    time.sleep(0.5)

def find_hwnd():
    user32 = ctypes.windll.user32
    result = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        n = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, n, 256)
        if "PHOSPHOR" in n.value and user32.IsWindowVisible(hwnd):
            result.append(hwnd)
        return True
    user32.EnumWindows(cb, 0)
    return result[0] if result else None

def get_rect(hwnd):
    import ctypes.wintypes as w
    r = w.RECT()
    ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom

def to_front(hwnd):
    user32 = ctypes.windll.user32
    user32.keybd_event(0x12, 0, 0, 0)          # ALT down
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(0x12, 0, 2, 0)          # ALT up
    time.sleep(0.4)

def click(x, y):
    ctypes.windll.user32.SetCursorPos(int(x), int(y))
    time.sleep(0.15)
    ctypes.windll.user32.mouse_event(0x0002, 0, 0, 0, 0)  # left down
    time.sleep(0.05)
    ctypes.windll.user32.mouse_event(0x0004, 0, 0, 0, 0)  # left up
    time.sleep(0.5)

def park_mouse(l=None, t=None):
    """清悬停态：若光标静止出现在窗内，WPF 命中测试置 IsMouseOver=true 但窗口未注册
    鼠标跟踪，直接移走收不到 WM_MOUSELEAVE 会粘住。必须先移入窗口产生 WM_MOUSEMOVE
    （激活跟踪）再移出（产生 WM_MOUSELEAVE）。启动前调用（l=None）只挪到窗外。"""
    import ctypes.wintypes as w
    if l is not None:
        ctypes.windll.user32.SetCursorPos(int(l + 2300), int(t + 100))  # 窗内空白区
        time.sleep(0.12)
    ctypes.windll.user32.SetCursorPos(80, 80)                          # 窗外（rect 起点 621,197）
    time.sleep(0.12)
    p = w.POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y

def sample(im, x0, x1, y0, y1):
    """区域内背景众数色（相对 bbox 图坐标）"""
    px = im.load()
    c = Counter()
    for x in range(x0, x1, 2):
        for y in range(y0, y1, 2):
            c[px[x, y][:3]] += 1
    return c.most_common(3)

def text_colors(im, x0, x1, y0, y1, bg):
    """与背景欧氏距离 >60 的像素众数（文字色）——兼容文字比背景深的浅色主题"""
    px = im.load()
    c = Counter()
    for x in range(x0, x1):
        for y in range(y0, y1):
            p = px[x, y][:3]
            d2 = (p[0]-bg[0])**2 + (p[1]-bg[1])**2 + (p[2]-bg[2])**2
            if d2 > 3600:
                c[p] += 1
    return c.most_common(3)

def near(got, want, tol=TOL):
    if not got:
        return False
    g = got[0][0]
    return all(abs(g[i] - want[i]) <= tol for i in range(3))

def verdict(label, got, want):
    ok = near(got, want)
    g = got[0][0] if got else None
    cnt = got[0][1] if got else 0
    print("  %-22s got=%s (n=%d) want=%s  -> %s"
          % (label, g, cnt, want, "PASS" if ok else "FAIL"))
    return ok

def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "dark-click"
    theme = "light" if mode.startswith("light") else "dark"
    exp = EXPECT[theme]

    kill_all()
    park_mouse()                                          # 启动前光标必须在窗外（防创建即悬停）
    args = [EXE, "--page=library", "--vol=0", "--no-save", "--theme=" + theme,
            "--add=" + WAV]
    if mode.endswith("play"):
        args.append("--play")

    print("launch:", " ".join(args[1:]))
    subprocess.Popen(args)
    time.sleep(6.0)

    hwnd = find_hwnd()
    if not hwnd:
        print("ERROR: window not found"); return 1
    l, t, r, b = get_rect(hwnd)
    print("rect:", l, t, r, b, " size:", r - l, b - t)
    to_front(hwnd)

    # 相对窗口坐标（bbox 截图后原点为窗口左上角）
    # 行带：第一行数据物理 y 708-770（表头 646-708）=> 相对 t+511..573；采样 t+528..568
    # 净背景 x：bilibili 浮窗物理 1085-1998 之外 —— titleband 681-1021（左）、band 2141-2241（右）
    row_click = (l + 1580, t + 545)                       # 物理 ~2201,742 浮窗右缘外
    band_rel = (1520, 1620, 528, 568)                     # x0,x1,y0,y1 行净背景带
    title_rel = (60, 400, 528, 568)                       # 标题文字带

    def snap(tag):
        cur = park_mouse(l, t)                            # 移入再移出：清粘住的悬停态
        print("  [dbg] %s cursor=(%d,%d) parked" % (tag, cur[0], cur[1]))
        im = ImageGrab.grab(bbox=(l, t, r, b))            # 只截窗口，不落全屏
        rowbg = sample(im, *band_rel)
        bg = rowbg[0][0]
        tx = text_colors(im, *title_rel, bg)
        # 只存两条采样带小图（浮窗 x 范围之外，纯 UI）
        im.crop((band_rel[0], band_rel[2], band_rel[1], band_rel[3])).save(
            os.path.join(TMP, "ab-band-%s-%s.png" % (mode, tag)))
        im.crop((title_rel[0], title_rel[2], title_rel[1], title_rel[3])).save(
            os.path.join(TMP, "ab-title-%s-%s.png" % (mode, tag)))
        return rowbg, tx

    ok = True
    if mode.endswith("click"):
        rowbg0, tx0 = snap("s0")
        print("S0 (no sel) rowbg:", rowbg0[:2])
        print("S0 text:", tx0[:2])
        ok &= verdict("S0 rowbg", rowbg0, exp["s0bg"])
        ok &= verdict("S0 text", tx0, exp["s0tx"])
        click(*row_click)
        rowbg1, tx1 = snap("s1")
        print("S1 (rowclick) rowbg:", rowbg1[:2])
        print("S1 text:", tx1[:2])
        ok &= verdict("S1 rowbg", rowbg1, exp["s1bg"])
        ok &= verdict("S1 text", tx1, exp["s1tx"])
    else:
        rowbg1, tx1 = snap("s1")
        print("S1 (%s) rowbg:" % mode, rowbg1[:2])
        print("S1 text:", tx1[:2])
        ok &= verdict("S1 rowbg", rowbg1, exp["s1bg"])
        ok &= verdict("S1 text", tx1, exp["s1tx"])

    kill_all()
    print("== %s: %s ==" % (mode, "PASS" if ok else "FAIL"))
    return 0 if ok else 2

if __name__ == "__main__":
    sys.exit(main())
