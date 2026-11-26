"""Regenerate app icons from the supplied transparent RGBA source PNGs.

Straight-alpha LANCZOS resize pulls透明区域 RGB into边缘; if透明区 RGB is black this
produces the dark fringe seen on the taskbar. Resize in premultiplied-alpha space
and unpremultiply afterwards so边缘颜色始终来自图标内容本身.
"""
import os
from PIL import Image

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
# 512 而非 1024：窗口图标实际最大用到 256px（200% DPI 的 Alt-Tab），
# 1024 版单张 970KB、解码后常驻 4MB 位图，对画质无可见收益而白占包体。
dark_hi = fit_ink(dark_raw, canvas=512)
light_hi = fit_ink(light_raw, canvas=512)
dark_hi.save(os.path.join(ASSETS, "icon-dark-hi.png"))
light_hi.save(os.path.join(ASSETS, "icon-light-hi.png"))

# ICO: multi-size from the premultiplied 256 base, each unpremultiplied separately
base_pm = premultiply(dark256)
sizes = [16, 24, 32, 48, 64, 128, 256]
ico_frames = [unpremultiply(base_pm.resize((s, s), Image.LANCZOS)) for s in sizes]
ico_frames[0].save(os.path.join(ASSETS, "app.ico"), format="ICO",
                   sizes=[(s, s) for s in sizes])

print("generated icon-dark/light (256) + icon-*-hi (512) + app.ico from RGBA sources")
