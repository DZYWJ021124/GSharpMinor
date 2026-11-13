# -*- coding: utf-8 -*-
"""生成验收用短 WAV：LIST INFO 标签（INAM 标题 / IART 艺术家 / IPRD 专辑）
+ 一个 CJK 文件名曲目（标题走文件名兜底，验证字形渲染）。
RIFF 顺序 fmt → LIST → data（TagReader 只扫文件头 256KB，LIST 必须在 data 前）。
输出: %TEMP%\\pa-verify\\
"""
import os, struct, math

OUT = os.path.join(os.environ.get("TEMP", r"C:\Users\tzxy1\AppData\Local\Temp"), "pa-verify")
os.makedirs(OUT, exist_ok=True)

def make_wav(path, tags, freq=440, sec=2.0, rate=44100):
    # fmt chunk (PCM 16bit stereo)
    fmt = b"fmt " + struct.pack("<IHHIIHH", 16, 1, 2, rate, rate * 4, 4, 16)
    # LIST INFO
    info = b"INFO"
    for sid, txt in tags:
        raw = txt.encode("utf-8") + b"\x00"
        if len(raw) % 2: raw += b"\x00"
        info += sid.encode("ascii") + struct.pack("<I", len(txt.encode("utf-8")) + 1) + raw
    lst = b"LIST" + struct.pack("<I", len(info)) + info
    # data
    fr = bytearray()
    for i in range(int(rate * sec)):
        v = int(9000 * math.sin(2 * math.pi * freq * i / rate))
        fr += struct.pack("<hh", v, v)
    data = b"data" + struct.pack("<I", len(fr)) + bytes(fr)
    body = b"WAVE" + fmt + lst + data
    open(path, "wb").write(b"RIFF" + struct.pack("<I", len(body)) + body)

make_wav(os.path.join(OUT, "zeta.wav"),
         [("INAM", "Echo Song"), ("IART", "PA1LAB"), ("IPRD", "Delta Album")])
make_wav(os.path.join(OUT, "alpha.wav"),
         [("INAM", "Alpha Song"), ("IART", "PA1LAB"), ("IPRD", "Charlie Album")])
make_wav(os.path.join(OUT, "mid.wav"),
         [("INAM", "Midnight Song"), ("IART", "PA1LAB"), ("IPRD", "Bravo Album")])
make_wav(os.path.join(OUT, "忘却の町 エルシア.wav"),
         [("IART", "PA1LAB"), ("IPRD", "Able Album")])   # 标题走文件名兜底（Unicode）

for f in sorted(os.listdir(OUT)):
    print(f, os.path.getsize(os.path.join(OUT, f)))
