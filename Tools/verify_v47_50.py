# -*- coding: utf-8 -*-
"""v47-50 验收驱动：
  advance             —— 播完自动切曲（日志 [load] 计数，3 首 2s 曲应推进 ≥2 次）
  shoot <sort>        —— 截曲目表标题列（排序顺序 + CJK 字形），sort=import|title-az|title-za|album-az|album-za
  persist-save        —— 不带 --no-save 导入 3 首（曲库记忆写入）
  persist-check       —— 纯启动（不 --add），截曲目表验证恢复
共用：DPI-aware 物理坐标、只 bbox 截窗、只存标题列切图（bilibili 浮窗外）。
"""
import ctypes, subprocess, time, sys, os
from PIL import ImageGrab

ctypes.windll.user32.SetProcessDPIAware()

EXE = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\pa1-native\dist\PHOSPHOR-PA1.exe"
V = os.path.join(os.environ.get("TEMP", r"C:\Users\tzxy1\AppData\Local\Temp"), "pa-verify")
TMP = r"C:\Users\tzxy1\AppData\Local\Temp"
LOG = os.path.join(TMP, "pa1-native-crash.log")

ADDS = [os.path.join(V, "zeta.wav"), os.path.join(V, "alpha.wav"),
        os.path.join(V, "mid.wav"), os.path.join(V, "忘却の町 エルシア.wav")]

def kill_all():
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
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
    user32.keybd_event(0x12, 0, 0, 0)
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(0x12, 0, 2, 0)
    time.sleep(0.4)

def launch(args):
    kill_all()
    ctypes.windll.user32.SetCursorPos(80, 80)
    subprocess.Popen([EXE] + args)
    time.sleep(7.0)
    hwnd = find_hwnd()
    if not hwnd:
        print("ERROR: window not found"); sys.exit(1)
    l, t, r, b = get_rect(hwnd)
    to_front(hwnd)
    return hwnd, l, t, r, b

def shoot_title_strip(l, t, r, b, tag):
    """标题列切图 x rel 20-450（浮窗 464-1377 外）；右上角切图含 SortCombo/PlStats（2050-2598）"""
    im = ImageGrab.grab(bbox=(l, t, r, b))
    crop = im.crop((20, 480, 450, 920))
    p = os.path.join(TMP, "v-title-%s.png" % tag)
    crop.save(p)
    print("shot:", p, crop.size)
    crop2 = im.crop((1650, 380, 2598, 540))
    p2 = os.path.join(TMP, "v-combo-%s.png" % tag)
    crop2.save(p2)
    print("shot:", p2, crop2.size)

def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "advance"
    if mode == "advance":
        try: os.remove(LOG)
        except OSError: pass
        args = ["--page=library", "--vol=0", "--no-save", "--play",
                "--add=" + ADDS[0], "--add=" + ADDS[1], "--add=" + ADDS[2]]
        print("launch:", " ".join(args))
        subprocess.Popen([EXE] + args)
        time.sleep(12.0)          # 2s×3 曲 + 加载余量
        kill_all()
        n = 0
        try:
            for ln in open(LOG, encoding="utf-8", errors="ignore"):
                if "[load] begin" in ln:
                    n += 1
                    print("  ", ln.strip())
        except IOError:
            print("ERROR: no crash log")
        print("== advance: %d loads (need >=2) ==" % n)
        sys.exit(0 if n >= 2 else 2)

    if mode.startswith("shoot"):
        sort = sys.argv[2] if len(sys.argv) > 2 else "import"
        args = ["--page=library", "--vol=0", "--no-save", "--theme=light", "--sort=" + sort]
        for f in ADDS: args.append("--add=" + f)
        hwnd, l, t, r, b = launch(args)
        shoot_title_strip(l, t, r, b, mode + "-" + sort)
        kill_all()
        return

    if mode == "persist-save":
        args = ["--page=library", "--vol=0", "--theme=light"]
        for f in ADDS[:3]: args.append("--add=" + f)
        hwnd, l, t, r, b = launch(args)
        time.sleep(1.5)            # 等 AddFilesAsync 落盘
        kill_all()                 # /F 强杀也安全：导入完成时已 SaveTracks
        print("saved tracks to settings")
        return

    if mode == "persist-check":
        args = ["--page=library", "--vol=0", "--theme=light"]
        hwnd, l, t, r, b = launch(args)
        shoot_title_strip(l, t, r, b, "persist")
        kill_all()
        return

if __name__ == "__main__":
    main()
