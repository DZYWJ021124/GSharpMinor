# -*- coding: utf-8 -*-
"""时长量化扫描：三个曲库文件夹内每个音频文件
   - meta_format: ffprobe format=duration（容器估算，≈PA-1 显示的 TotalTime 来源之一）
   - meta_stream: ffprobe stream=duration（FLAC STREAMINFO / 编码器元数据时长）
   - true_decode: ffmpeg 解码到空输出的实际时长（真实结束时间）
   输出 mismatch 清单（|meta-true| > 1.0s）。不碰 E:\\Music\\ASMR。"""
import subprocess, os, json, sys

FFPROBE = r"C:\Users\tzxy1\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffprobe.exe"
FFMPEG = r"C:\Users\tzxy1\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin\ffmpeg.exe"
DIRS = [r"E:\Music\流媒体", r"E:\Music\古典", r"E:\Music\CD抓轨"]
EXTS = {".flac", ".wav", ".mp3", ".m4a", ".mp4", ".ogg", ".opus", ".wma", ".aac", ".aiff", ".aif", ".dsf", ".dff"}
OUT = r"C:\Users\tzxy1\AppData\Local\Temp\pa1-lenscan.json"

def probe_meta(path):
    try:
        r = subprocess.run([FFPROBE, "-v", "error", "-show_entries",
                            "format=duration:stream=duration,codec_name",
                            "-of", "json", path], capture_output=True, text=True, timeout=60)
        j = json.loads(r.stdout)
        fmt = float(j.get("format", {}).get("duration") or 0)
        st = j.get("streams") or [{}]
        sd = float(st[0].get("duration") or 0)
        codec = st[0].get("codec_name", "?")
        return fmt, sd, codec
    except Exception as e:
        return 0, 0, "ERR:" + str(e)[:40]

def probe_true(path):
    try:
        r = subprocess.run([FFMPEG, "-v", "error", "-nostats", "-i", path, "-map", "0:a:0",
                            "-f", "null", "-", "-progress", "pipe:1"],
                           capture_output=True, text=True, timeout=600)
        t = 0.0
        for ln in (r.stdout or "").splitlines():
            if ln.startswith("out_time_us=") or ln.startswith("out_time_ms="):
                try:
                    t = float(ln.split("=", 1)[1]) / 1e6
                except ValueError:
                    pass
            elif ln.startswith("out_time="):
                s = ln.split("=", 1)[1].strip()
                try:
                    parts = s.split(":")
                    t = float(parts[0])*3600 + float(parts[1])*60 + float(parts[2])
                except Exception:
                    pass
        return t
    except Exception:
        return -1.0

def main():
    rows = []
    for d in DIRS:
        for root, _, files in os.walk(d):
            for fn in sorted(files):
                ext = os.path.splitext(fn)[1].lower()
                if ext not in EXTS:
                    continue
                p = os.path.join(root, fn)
                fmt, sd, codec = probe_meta(p)
                td = probe_true(p)
                diff = max(abs(fmt - td), abs(sd - td)) if td >= 0 else 999
                rows.append(dict(path=p, codec=codec, fmt=fmt, stream=sd, true=td, diff=diff))
                flag = "MISMATCH" if diff > 1.0 else "ok"
                print("%-5s %-44s fmt=%7.2f stream=%7.2f true=%7.2f  %s"
                      % (codec, fn[:44], fmt, sd, td, flag), flush=True)
    bad = [r for r in rows if r["diff"] > 1.0]
    print("\nTOTAL=%d MISMATCH=%d" % (len(rows), len(bad)))
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(rows, f, ensure_ascii=False, indent=1)
    print("saved:", OUT)

if __name__ == "__main__":
    main()
