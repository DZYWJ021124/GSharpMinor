# -*- coding: utf-8 -*-
"""
进度条 Seek 验收（analyzer 页 ScrubTrack）：
  down 在轨道安全 x（避开 bilibili 浮窗物理 x 1085-1998）→ 拖向开头 → 窗口外松手。
修复前：MouseUp 丢失、无 [seek] 日志、进度头冻结；
修复后（鼠标捕获）：[seek] 0.00 落日志、进度头回到起点。
用法：python verify_seek.py
"""
import ctypes, subprocess, time, os, wave, math, struct
from PIL import ImageGrab

EXE = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\pa1-native\dist\PHOSPHOR-PA1.exe"
TMP = r"C:\Users\tzxy1\AppData\Local\Temp"
LOG = os.path.join(TMP, "pa1-native-crash.log")
WAV = os.path.join(TMP, "pa1-seektest.wav")

ctypes.windll.user32.SetProcessDPIAware()
user32 = ctypes.windll.user32

def make_wav(path, sec=60):
    if os.path.exists(path):
        return
    w = wave.open(path, "wb")
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(44100)
    n = sec * 44100
    fr = bytearray()
    for i in range(n):
        v = int(6000 * math.sin(2 * math.pi * 440 * i / 44100.0))
        fr += struct.pack("<hh", v, v)
    w.writeframes(bytes(fr)); w.close()
    print("wav:", path)

def find_window():
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
    r = w.RECT()
    user32.GetWindowRect(res[0], ctypes.byref(r))
    return res[0], (r.left, r.top, r.right, r.bottom)

def front(hwnd):
    user32.keybd_event(0x12, 0, 0, 0)
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(0x12, 0, 2, 0)
    time.sleep(0.4)

def is_amber(p):
    """浅色主题 WAmber=(180,83,9) 深橙（含 AA 混合）"""
    r, g, b = p
    return r > 140 and b < 95 and (r - b) > 90 and (r - g) > 45 and g < 160

def amber_head(im):
    """找 ScrubHead 竖线：轨道带 y 1185-1265 内同 x 最长连续琥珀行程（>=24px）。
    只扫轨道带：避开 Pause 按钮橙框（y 1277+）等干扰。"""
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
    make_wav(WAV)
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
    time.sleep(0.5)
    user32.SetCursorPos(80, 80)   # 启动前光标停窗外（防悬停粘滞）
    try: os.remove(LOG)
    except OSError: pass
    subprocess.Popen([EXE, "--page=analyzer", "--vol=0", "--no-save", "--theme=light",
                      "--add=" + WAV, "--play"])
    time.sleep(8)
    hwnd, rect = find_window()
    l, t, r, b = rect
    print("rect:", rect)
    front(hwnd)
    im0 = ImageGrab.grab(bbox=(l, t, r, b))
    head = amber_head(im0)
    print("head before:", head)
    if head is None:
        print("FAIL: ScrubHead 未找到")
        subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
        return
    hx, hy, _ = head
    # ── 手势 A：按下（轨道安全 x 2200，浮窗外）→ 分步拖到窗口外（向开头）→ 外面松手 ──
    start_x, start_y = l + 2200, t + hy
    user32.SetCursorPos(start_x, start_y); time.sleep(0.25)
    user32.mouse_event(0x0002, 0, 0, 0, 0); time.sleep(0.08)
    for i in range(1, 11):
        user32.SetCursorPos(int(start_x + (l - 350 - start_x) * i / 10.0), start_y)
        time.sleep(0.05)
    user32.mouse_event(0x0004, 0, 0, 0, 0)   # 松手在窗口外
    time.sleep(1.2)
    print("gesture A (drag-to-start, release outside) done")
    seek_lines = []
    try:
        for ln in open(LOG, encoding="utf-8", errors="ignore"):
            if "[seek" in ln or "[load]" in ln or "[plan]" in ln:
                seek_lines.append(ln.rstrip())
    except IOError:
        pass
    print("log tail:")
    for ln in seek_lines[-6:]:
        print("  ", ln[:150])
    ok_seek = any("[seek]" in ln for ln in seek_lines)
    seek_val = None
    for ln in seek_lines:
        if "[seek]" in ln:
            try: seek_val = float(ln.split("[seek]")[1].strip())
            except ValueError: pass
    front(hwnd)
    im1 = ImageGrab.grab(bbox=(l, t, r, b))
    head2 = amber_head(im1)
    print("head after A:", head2)
    ok_head = head2 is not None and head2[0] < 250
    ok_val = seek_val is not None and seek_val < 1.0
    # ── 手势 B：轨道内按下 → 拖到 x=1400 松手（浮窗外）→ seek 值应≈32.6s ──
    user32.SetCursorPos(l + 2200, t + hy); time.sleep(0.25)
    user32.mouse_event(0x0002, 0, 0, 0, 0); time.sleep(0.08)
    for i in range(1, 9):
        user32.SetCursorPos(int(l + 2200 + (1400 - 2200) * i / 8.0), t + hy)
        time.sleep(0.05)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(1.0)
    seek2 = None
    try:
        for ln in open(LOG, encoding="utf-8", errors="ignore"):
            if "[seek]" in ln:
                try: seek2 = float(ln.split("[seek]")[1].strip())
                except ValueError: pass
    except IOError:
        pass
    front(hwnd)
    im2 = ImageGrab.grab(bbox=(l, t, r, b))
    head3 = amber_head(im2)
    print("gesture B: seek_val=%s head=%s (期望 ~32.6 / x~1400)" % (seek2, head3))
    ok_b = seek2 is not None and 25.0 < seek2 < 40.0 and head3 is not None and 1150 < head3[0] < 1650
    print("RESULT: A: seek_logged=%s seek_val=%s head_near_start=%s | B: ok=%s"
          % (ok_seek, seek_val, ok_head, ok_b))
    if ok_seek and ok_head and ok_val and ok_b:
        print("PASS")
    else:
        print("FAIL")
        im2.crop((0, 1150, 2598, 1300)).save(os.path.join(TMP, "v-seek-fail.png"))
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)

if __name__ == "__main__":
    main()
