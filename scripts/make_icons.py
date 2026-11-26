"""Regenerate app icons from the supplied transparent RGBA source PNGs.

Straight-alpha LANCZOS resize pulls透明区域 RGB into边缘; if透明区 RGB is black this
produces the dark fringe seen on the taskbar. Resize in premultiplied-alpha space
and unpremultiply afterwards so边缘颜色始终来自图标内容本身.
"""
import os
import io
from PIL import Image, ImageDraw, ImageEnhance

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(BASE, "Pa1.App", "Assets")
SRC = os.path.join(ASSETS, "Source")


def premultiply(im):
    im = im.convert("RGBA")
    px = im.load()
    w, h = im.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a != 255:
                px[x, y] = (r * a // 255, g * a // 255, b * a // 255, a)
    return im


def unpremultiply(im):
    im = im.copy()
    px = im.load()
    w, h = im.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a < 12:
                # Nearly invisible alpha; drop it entirely. Otherwise the few
                # RGB=0 fringe pixels get interpolated into a visible dark rim
                # when Windows downscales the icon to taskbar size.
                px[x, y] = (0, 0, 0, 0)
            elif a < 255:
                px[x, y] = (min(255, r * 255 // a), min(255, g * 255 // a),
                            min(255, b * 255 // a), a)
    return im


def resize_safe(im, size):
    target = (size, size) if isinstance(size, int) else size
    return unpremultiply(premultiply(im).resize(target, Image.LANCZOS))


def fit_ink(im, canvas=256, fill=0.96):
    """Crop transparent margins and scale the squircle to `fill` of the canvas.
    The supplied sources leave ~20% empty margin, which makes the taskbar icon
    look small; expand the whole badge (not just the glyph) without stretching.
    """
    im = im.convert("RGBA")
    bbox = im.getchannel("A").getbbox()
    if bbox:
        im = im.crop(bbox)
    scale = canvas * fill / max(im.size)
    size = (max(1, round(im.width * scale)), max(1, round(im.height * scale)))
    fitted = resize_safe(im, size)
    out = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    out.paste(fitted, ((canvas - size[0]) // 2, (canvas - size[1]) // 2), fitted)
    return out


dark_raw = Image.open(os.path.join(SRC, "icon-dark-src.png"))
light_raw = Image.open(os.path.join(SRC, "icon-light-src.png"))

dark256 = fit_ink(dark_raw)
light256 = fit_ink(light_raw)
dark256.save(os.path.join(ASSETS, "icon-dark.png"))
light256.save(os.path.join(ASSETS, "icon-light.png"))

# High-res variants for Window.Icon: more source pixels when Windows downsamples
# to taskbar size. Still pre-cropped (no 20% empty margin) and fringe-cleaned.
# 保持 1024：窗口图标由 WPF 自己缩放，DWM/任务栏/Alt-Tab 都从这一张取像素，
# 源图越大缩放越干净（512 版在 Alt-Tab 大图标上能看出软边）。
dark_hi = fit_ink(dark_raw, canvas=1024)
light_hi = fit_ink(light_raw, canvas=1024)
dark_hi.save(os.path.join(ASSETS, "icon-dark-hi.png"))
light_hi.save(os.path.join(ASSETS, "icon-light-hi.png"))


def small_variant(src, size, box=(0.16, 0.20, 0.80, 0.88), boost=1.14):
    """16/24px 专用：裁到谱号主体 + 轻微提亮，再按原角率倒圆角。

    整图直接缩到 16px 时，谱号只占 ~10px 高，细笔画平均下来只剩一团灰雾；
    旁边的轨道环、# 号与「minor」字样更是噪点。小尺寸图标本来的做法就是重画一版
    「放大主体、丢掉细节」——这里用裁切代替重画，主体放大约 1.6 倍。
    用 6 倍超采样再降采样，圆角与笔画边缘才不会毛。"""
    w, h = src.size
    S = size * 6
    x0, y0, x1, y1 = [int(v * (w if i % 2 == 0 else h)) for i, v in enumerate(box)]
    c = src.crop((x0, y0, x1, y1)).resize((S, S), Image.LANCZOS)
    c = ImageEnhance.Brightness(c).enhance(boost)
    c = ImageEnhance.Contrast(c).enhance(1.08)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.225), fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(c, (0, 0), mask)
    return out.resize((size, size), Image.LANCZOS)


def write_ico(path, src, sizes):
    """多尺寸 ICO（PNG 帧），每档都直接从源图渲染。

    不能用 PIL 的 ICO save：它只写「不大于传入图像」的档位，以前传入的是 16px 帧，
    结果整个 app.ico 里只剩一张 16×16——资源管理器/任务栏/快捷方式都在把 16px 放大，
    糊成一片。这里逐档 resize_safe（预乘 alpha，避免透明区黑边）自己拼 ICO。
    ≤24px 走 small_variant（裁主体、提亮），其余档保留完整构图。
    """
    import struct
    frames = []
    for s in sizes:
        frames.append((s, small_variant(src, s) if s <= 24 else fit_ink(src, canvas=s)))
    blobs = []
    for s, im in frames:
        buf = io.BytesIO()
        im.save(buf, format="PNG", optimize=True)
        blobs.append((s, buf.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries, data = b"", b""
    for s, blob in blobs:
        edge = 0 if s >= 256 else s          # ICONDIRENTRY 用 0 表示 256
        entries += struct.pack("<BBBBHHII", edge, edge, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob
    with open(path, "wb") as f:
        f.write(header + entries + data)


# EXE 静态图标：深色（黑金谱号），逐尺寸渲染，16/24/32/48/64/128/256 各一张
write_ico(os.path.join(ASSETS, "app.ico"), dark_raw, [16, 24, 32, 48, 64, 128, 256])

# 窗口图标（任务栏 / Alt-Tab）：WPF 的 Window.Icon 会把源图原尺寸做成一个 HICON 交给系统，
# 1024px 源在任务栏 48px 处是一步 21:1 缩放 → 发糊。改为运行时按 DPI 从多尺寸 ICO 挑帧、
# 用 WM_SETICON 直接发尺寸正确的 HICON（见 WindowIcon.cs）。两套主题各一份。
for name, raw in (("icon-dark", dark_raw), ("icon-light", light_raw)):
    write_ico(os.path.join(ASSETS, name + ".ico"), raw, [16, 24, 32, 48, 64, 128, 256])

print("generated icon-dark/light (256 png) + icon-*-hi (1024 png) + app/theme ico (7 frames)")
