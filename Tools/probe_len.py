# -*- coding: utf-8 -*-
"""
总时长 vs 真实结束时间 诊断：
  A. 真实曲库文件：载入后看 [len] est=? true=? （后台解码到 EOF 的真值探测）
  B. 谎报头 WAV（头部 60s / 实际 10s）：验证真值探测 + [end] 定格
  C. DSF：播到接近结尾，看 [end] pos vs len（DSD 不走 [len] 探测）
用法：python probe_len.py
"""
import ctypes, subprocess, time, os, wave, math, struct, glob
from PIL import ImageGrab

EXE = r"C:\Users\tzxy1\WorkBuddy\2026-11-07-18-08-52\pa1-native\dist\PHOSPHOR-PA1.exe"
TMP = r"C:\Users\tzxy1\AppData\Local\Temp"
LOG = os.path.join(TMP, "pa1-native-crash.log")
LIB = r"E:\Music\古典"
LYING = os.path.join(TMP, "pa1-lying60.wav")

ctypes.windll.user32.SetProcessDPIAware()
user32 = ctypes.windll.user32

def clear_log():
    try: os.remove(LOG)
    except OSError: pass

def read_log():
    out = []
    try:
        for ln in open(LOG, encoding="utf-8", errors="ignore"):
            if "[len]" in ln or "[end]" in ln:
                out.append(ln.split("] ", 1)[-1].strip() if "] " in ln else ln.strip())
    except IOError:
        pass
    return out

def run_one(path, wait):
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
    time.sleep(0.4)
    user32.SetCursorPos(80, 80)
    clear_log()
    subprocess.Popen([EXE, "--page=analyzer", "--vol=0", "--no-save", "--theme=light",
                      "--add=" + path, "--play"])
    time.sleep(wait)
    lines = read_log()
    subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
    time.sleep(0.3)
    return lines

def make_lying_wav(path):
    """fmt/data 正常，但 data 块大小谎报 60s、物理只写 10s 采样（截断文件）"""
    if os.path.exists(path):
        return
    rate, ch, width = 44100, 2, 2
    real_sec, claim_sec = 10, 60
    n = rate * real_sec
    frames = bytearray()
    for i in range(n):
        v = int(6000 * math.sin(2 * math.pi * 440 * i / rate))
        frames += struct.pack("<hh", v, v)
    claim_bytes = rate * claim_sec * ch * width      # 谎报
    fmt = struct.pack("<HHIIHH", 1, ch, rate, rate * ch * width, ch * width, 16)
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 4 + (8 + 16) + (8 + claim_bytes)) + b"WAVE")
        f.write(b"fmt " + struct.pack("<I", 16) + fmt)
        f.write(b"data" + struct.pack("<I", claim_bytes))
        f.write(frames)                               # 物理只有 10s
    print("lying wav:", path, "claim=60s real=10s")

def main():
    make_lying_wav(LYING)
    files = []
    for ext in ("*.flac", "*.dsf", "*.dff", "*.mp3", "*.wav", "*.m4a"):
        files += glob.glob(os.path.join(LIB, ext))
    files.sort()
    print("=== A. 真实曲库 est vs true ===")
    for p in files:
        name = os.path.basename(p)
        lines = run_one(p, 7)
        tag = "DSF(no probe)" if name.lower().endswith((".dsf", ".dff")) else "?"
        print("%-52s %s" % (name[:50], " | ".join(lines) if lines else tag))
    print("=== B. 谎报头 WAV（claim 60s / real 10s）===")
    lines = run_one(LYING, 15)
    for ln in lines:
        print("  ", ln)
    print("=== C. DSF 播到尾 [end] pos vs len ===")
    for p in [x for x in files if x.lower().endswith((".dsf", ".dff"))][:2]:
        name = os.path.basename(p)
        subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
        time.sleep(0.4)
        user32.SetCursorPos(80, 80)
        clear_log()
        subprocess.Popen([EXE, "--page=analyzer", "--vol=0", "--no-save", "--theme=light",
                          "--add=" + p, "--play"])
        time.sleep(7)
        # 轨道几何：窗口相对 y≈1226、x 110→2486；点 95% 处（物理 x 远离浮窗）
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
        user32.keybd_event(0x12, 0, 0, 0); user32.SetForegroundWindow(res[0]); user32.keybd_event(0x12, 0, 2, 0)
        time.sleep(0.3)
        cx, cy = r.left + int(110 + 2376 * 0.95), r.top + 1226
        user32.SetCursorPos(cx, cy); time.sleep(0.2)
        user32.mouse_event(0x0002, 0, 0, 0, 0); time.sleep(0.05); user32.mouse_event(0x0004, 0, 0, 0, 0)
        time.sleep(12)   # 等自然播完（余下 5% + 排空）
        lines = read_log()
        subprocess.run(["taskkill", "/F", "/IM", "PHOSPHOR-PA1.exe"], capture_output=True)
        time.sleep(0.3)
        print("%-52s %s" % (name[:50], " | ".join(lines) if lines else "(no end log)"))

if __name__ == "__main__":
    main()
