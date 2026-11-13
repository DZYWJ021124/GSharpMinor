# -*- coding: utf-8 -*-
"""
真实结束时间验收：加载真实曲目 → 进度条 seek 到 ratio → 听到自然播完 →
读 [end] pos= / len= 日志与 [len] est=/true=，对照元数据时长。
用法：python verify_end.py "<文件路径>" <seek_ratio> [wait_sec]
"""
import ctypes, subprocess, time, os, sys
from PIL import ImageGrab

EXE = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\pa1-native\dist\PHOSPHOR-PA1.exe"
TMP = r"C:\Users\tzxy1\AppData\Local\Temp"
LOG = os.path.join(TMP, "pa1-native-crash.log")
ctypes.windll.user32.SetProcessDPIAware()
user32 = ctypes.windll.user32

def is_amber(p):
    r, g, b = p
    return r > 140 and b < 95 and (r - b) > 90 and (r - g) > 45 and g < 160

def amber_head(im):
    px = im.convert("RGB").load()
    w, h = im.size
    best = None
    for x in range(0, w):
        ys = [y for y in range(1185, min(1265, h)) if is_amber(px[x, y])]
        if len(ys) < 24:
            continue
        run_best = 1; run = 1
        for i in range(1, len(ys)):
            run = run + 1 if ys[i] - ys[i - 1] <= 2 else 1
            if run > run_best:
                run_best = run
        if run_best >= 24 and (best is None or run_best > best[2]):
            best = (x, sum(ys) // len(ys), run_best)
    return best

def main():
    path = sys.argv[1]
    ratio = float(sys.argv[2]) if len(sys.argv) > 2 else 0.9
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 25
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
    time.sleep(0.5)
    user32.SetCursorPos(80, 80)
    try: os.remove(LOG)
    except OSError: pass
    subprocess.Popen([EXE, "--page=analyzer", "--vol=0", "--no-save", "--theme=light",
                      "--add=" + path, "--play"])
    time.sleep(8)
    res = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, _):
        n = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(h, n, 256)
        if "PHOSPHOR" in n.value and user32.IsWindowVisible(h):
            res.append(h)
        return True
    user32.EnumWindows(cb, 0)
    import ctypes.wintypes as w
    r = w.RECT(); user32.GetWindowRect(res[0], ctypes.byref(r))
    l, t = r.left, r.top
    user32.keybd_event(0x12, 0, 0, 0); user32.SetForegroundWindow(res[0]); user32.keybd_event(0x12, 0, 2, 0)
    time.sleep(0.4)
    im0 = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom))
    head = amber_head(im0)
    print("head:", head)
    hy = head[1] if head else 1226
    # 轨道 x 110-2486（窗口相对物理）：按下 2200（浮窗外）→ 拖到目标 ratio → 松手（浮窗外）
    tx = int(110 + (2486 - 110) * ratio)
    if 1085 - l < tx < 1998 - l:  # 避开 bilibili 浮窗物理 x 带
        tx = 2050 - l
    user32.SetCursorPos(l + 2200, t + hy); time.sleep(0.25)
    user32.mouse_event(0x0002, 0, 0, 0, 0); time.sleep(0.08)
    for i in range(1, 9):
        user32.SetCursorPos(int(l + 2200 + (l + tx - (l + 2200)) * i / 8.0), t + hy)
        time.sleep(0.04)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    print("seeked to ratio", ratio, "-> x", tx)
    time.sleep(wait)
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
    print("---- log ----")
    for ln in open(LOG, encoding="utf-8", errors="ignore"):
        s = ln.rstrip()
        if any(k in s for k in ("[len]", "[end]", "[seek]", "[plan]")):
            print(s[:170])

if __name__ == "__main__":
    main()
