"""从系统的 Noto Serif SC 可变字体生成可嵌入的静态子集字体。

为什么要这一步，而不是直接把 VF 塞进包体：
  · VF 的默认实例是 wght=200（ExtraLight），WPF(net48) 不支持选择变量轴，
    按名字引用只会拿到最细那一档，15px 下笔画会断；
  · 全量 VF 25MB，而界面只需要「拉丁 + 常用汉字」，子集后体积降到 1/10 以下。

生成物：
  Pa1.App/Assets/Fonts/Pa1SerifSC.ttf   家族名改为 Pa1 Serif SC，
  避免与系统已安装的 Noto Serif SC 撞名（WPF 会优先用系统那份的默认实例）。

授权：SIL OFL 1.1（Copyright © 2017-2023 Adobe）。改名属于 OFL 对派生作品的要求，
      同时保留原 name table 里的版权与授权声明。

用法：python scripts/make_font.py [--charset=gb2312|level1] [--weight=600]
"""
import argparse
import codecs
import io
import os
import sys
import struct

from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools import subset

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = r"C:/Windows/Fonts/NotoSerifSC-VF.ttf"
OUT_DIR = os.path.join(ROOT, "Pa1.App", "Assets", "Fonts")
OUT = os.path.join(OUT_DIR, "Pa1SerifSC.ttf")
FAMILY = "Pa1 Serif SC"
WEIGHT = 500


def gb2312_chars(level1_only=False):
    """按 GB2312 区位遍历出全部可解码字符。

    一级汉字 3755（0xB0A1–0xD7F9）覆盖现代汉语约 99.7%，二级 3008 为次常用。
    """
    rows = range(16, 56) if level1_only else range(16, 88)
    out = set()
    for row in rows:
        for cell in range(1, 95):
            b = bytes([0xA0 + row, 0xA0 + cell])
            try:
                out.add(b.decode("gb2312"))
            except UnicodeDecodeError:
                pass
    return out


def build_charset(level1_only=False):
    chars = set()
    # 基本拉丁 + Latin-1 + Latin Extended-A/B（西欧与部分东欧字母）
    for cp in list(range(0x20, 0x7F)) + list(range(0xA0, 0x250)) + list(range(0x1E00, 0x1F00)):
        chars.add(chr(cp))
    # 通用标点、货币、箭头、几何形状（▶ ◀ ■ □ 等界面符号，字体没有会被静默跳过）
    for a, b in ((0x2000, 0x2070), (0x20A0, 0x20C0), (0x2190, 0x2200), (0x25A0, 0x2600)):
        chars.update(chr(cp) for cp in range(a, b))
    # CJK 标点与全角形式
    chars.update(chr(cp) for cp in range(0x3000, 0x3040))
    chars.update(chr(cp) for cp in range(0xFF00, 0xFF66))
    # 汉字
    chars |= gb2312_chars(level1_only)
    return {c for c in chars if c != "\uFFFD"}


def rename(font):
    name = font["name"]
    ps = "Pa1SerifSC"
    for nid, val in ((1, FAMILY), (2, "Regular"), (3, f"{FAMILY};{ps}"),
                     (4, FAMILY), (6, ps), (16, FAMILY), (17, "Regular")):
        name.setName(val, nid, 3, 1, 0x409)     # Windows / Unicode BMP / en-US
        name.setName(val, nid, 1, 0, 0)         # Mac / Roman
    # 单字重家族：清除「粗体/斜体」位，否则 WPF 会去找并不存在的其他字面
    font["OS/2"].usWeightClass = WEIGHT
    font["OS/2"].fsSelection = (font["OS/2"].fsSelection & ~0x21) | 0x40   # REGULAR
    font["head"].macStyle = 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--charset", default="gb2312", choices=["gb2312", "level1"])
    ap.add_argument("--weight", type=int, default=600, help="固定后的字重（500 Medium / 600 SemiBold）")
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    global WEIGHT
    WEIGHT = args.weight

    if not os.path.exists(SRC):
        sys.exit("找不到源字体：%s" % SRC)

    print("[1/4] 载入可变字体", os.path.basename(SRC))
    font = TTFont(SRC)
    print("      轴:", [(a.axisTag, a.minValue, a.defaultValue, a.maxValue) for a in font["fvar"].axes])

    print(f"[2/4] 实例化 wght={WEIGHT}")
    font = instantiateVariableFont(font, {"wght": WEIGHT}, inplace=True, updateFontNames=False)
    print("      剩余表:", "fvar" in font, "gvar" in font)

    print("[3/4] 改名为", FAMILY)
    rename(font)

    chars = build_charset(args.charset == "level1")
    print(f"[4/4] 子集化：{len(chars)} 个字符（{args.charset}）")

    opts = subset.Options()
    opts.layout_features = ["*"]
    opts.name_IDs = ["*"]
    opts.name_languages = ["*"]
    opts.notdef_outline = True
    opts.recalc_bounds = True
    opts.recalc_timestamp = True
    opts.drop_tables += ["BASE", "STAT", "vhea", "vmtx", "meta"]
    sub = subset.Subsetter(options=opts)
    sub.populate(text="".join(sorted(chars)))
    sub.subset(font)

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    font.flavor = None
    font.save(args.out)

    raw = os.path.getsize(args.out)
    comp = len(zlib_compress(open(args.out, "rb").read()))
    kept = len([g for g in font.getGlyphOrder()])
    print(f"      字形数 {kept}")
    print(f"      输出 {args.out}")
    print(f"      体积 {raw:,} B（{raw/1048576:.2f} MiB），deflate 后约 {comp:,} B（{comp/1048576:.2f} MiB）")


def zlib_compress(data):
    import zlib
    return zlib.compress(data, 9)


if __name__ == "__main__":
    main()
