"""生成可嵌入的静态子集字体 Pa1 Serif SC。

为什么要这一步，而不是直接把字体塞进包体：
  · 系统的 NotoSerifSC-VF.ttf 是可变字体，默认实例 wght=200（ExtraLight），
    而 WPF(net48) 不支持选择变量轴——按名字引用只会拿到最细那一档，15px 下笔画会断；
  · 全量 25MB（SC）/ 14MB（KR），界面用不到那么多字形；
  · 与系统已装字体同名会让 WPF 命中系统那份，故改名 Pa1 Serif SC
    （OFL 对派生作品的要求，同时保留原版权与授权声明）。

内容（--charset 决定汉字档位，日文假名与韩文固定包含）：
  拉丁 + 标点 + 几何符号 + 假名 + 汉字 + 韩文音节
  汉字档位：level1（3,755）| gb2312（6,763）| uro（基本区 20,992 + 扩展A 6,592）
  韩文来自 Noto Serif KR（SC 版完全不含谚文），合并时只取谚文，避免与汉字码位冲突。

源文件：
  SC  C:/Windows/Fonts/NotoSerifSC-VF.ttf                       （SIL OFL 1.1, Adobe）
  KR  Noto Serif KR 600：@expo-google-fonts/noto-serif-kr 的
      600SemiBold/NotoSerifKR_600SemiBold.ttf                    （SIL OFL 1.1, Adobe）
      可用 --kr 指定本地路径；文件不入库，需要时重新下载。

用法：python scripts/make_font.py [--charset=uro|gb2312|level1] [--weight=600] [--kr=路径]
"""
import argparse
import os
import sys
import zlib

from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools import subset
from fontTools.merge import Merger

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SC_SRC = r"C:/Windows/Fonts/NotoSerifSC-VF.ttf"
KR_SRC = os.path.join(ROOT, ".m", "NotoSerifKR-600.ttf")   # 下载物，不入库
OUT_DIR = os.path.join(ROOT, "Pa1.App", "Assets", "Fonts")
OUT = os.path.join(OUT_DIR, "Pa1SerifSC.ttf")
FAMILY = "Pa1 Serif SC"
WEIGHT = 600


def gb2312_chars(level1_only=False):
    """按 GB2312 区位遍历出全部可解码字符。

    一级汉字 3755（0xB0A1–0xD7F9）覆盖现代汉语约 99.7%，二级 3008 为次常用。
    """
    rows = range(16, 56) if level1_only else range(16, 88)
    out = set()
    for row in rows:
        for cell in range(1, 95):
            try:
                out.add(bytes([0xA0 + row, 0xA0 + cell]).decode("gb2312"))
            except UnicodeDecodeError:
                pass
    return out


def latin_and_symbols():
    chars = set()
    for cp in list(range(0x20, 0x7F)) + list(range(0xA0, 0x250)) + list(range(0x1E00, 0x1F00)):
        chars.add(chr(cp))
    for a, b in ((0x2000, 0x2070), (0x20A0, 0x20C0), (0x2190, 0x2200), (0x25A0, 0x2600)):
        chars.update(chr(cp) for cp in range(a, b))
    chars.update(chr(cp) for cp in range(0x3000, 0x3100))      # CJK 标点 + 平假名 + 片假名
    chars.update(chr(cp) for cp in range(0xFF00, 0xFF66))      # 全角形式
    return chars


def han_chars(tier):
    if tier == "level1":
        return gb2312_chars(True)
    if tier == "gb2312":
        return gb2312_chars(False)
    if tier == "uro":
        return ({chr(c) for c in range(0x4E00, 0xA000)} |        # CJK 基本区
                {chr(c) for c in range(0x3400, 0x4DC0)})         # CJK 扩展 A
    raise SystemExit("未知档位: " + tier)


def hangul_chars():
    return ({chr(c) for c in range(0xAC00, 0xD7A4)} |            # 谚文音节
            {chr(c) for c in range(0x1100, 0x1200)} |            # 谚文字母
            {chr(c) for c in range(0x3130, 0x3190)})             # 兼容字母


def subset_font(font, chars):
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
    return font


def rename(font):
    name = font["name"]
    ps = "Pa1SerifSC"
    for nid, val in ((1, FAMILY), (2, "Regular"), (3, f"{FAMILY};{ps}"),
                     (4, FAMILY), (6, ps), (16, FAMILY), (17, "Regular")):
        name.setName(val, nid, 3, 1, 0x409)
        name.setName(val, nid, 1, 0, 0)
    # 单字重家族：清掉粗体/斜体位，否则 WPF 会去找并不存在的其他字面
    font["OS/2"].usWeightClass = WEIGHT
    font["OS/2"].fsSelection = (font["OS/2"].fsSelection & ~0x21) | 0x40
    font["head"].macStyle = 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--charset", default="uro", choices=["uro", "gb2312", "level1"])
    ap.add_argument("--weight", type=int, default=600)
    ap.add_argument("--kr", default=KR_SRC)
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    global WEIGHT
    WEIGHT = args.weight
    if not os.path.exists(SC_SRC):
        sys.exit("找不到 SC 源字体：" + SC_SRC)

    sc_chars = latin_and_symbols() | han_chars(args.charset)
    print(f"[1/5] 载入 SC 可变字体，实例化 wght={WEIGHT}")
    sc = TTFont(SC_SRC)
    sc = instantiateVariableFont(sc, {"wght": WEIGHT}, inplace=True, updateFontNames=False)

    print(f"[2/5] 子集化 SC：{len(sc_chars)} 个字符（汉字档位 {args.charset}）")
    subset_font(sc, sc_chars)

    fonts = [sc]
    if os.path.exists(args.kr):
        kr_chars = hangul_chars()
        print(f"[3/5] 子集化 KR：{len(kr_chars)} 个谚文字符")
        kr = TTFont(args.kr)
        subset_font(kr, kr_chars)
        fonts.append(kr)
    else:
        print("[3/5] 跳过韩文（未找到 KR 源：%s）" % args.kr)

    if len(fonts) > 1:
        print("[4/5] 合并 SC + KR（同 UPM 1000，谚文与汉字无码位重叠）")
        font = Merger().merge(fonts)
    else:
        font = fonts[0]

    rename(font)
    print("[5/5] 改名为", FAMILY)

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    font.flavor = None
    font.save(args.out)

    raw = os.path.getsize(args.out)
    comp = len(zlib.compress(open(args.out, "rb").read(), 9))
    print(f"      字形 {len(font.getGlyphOrder()):,}")
    print(f"      输出 {args.out}")
    print(f"      体积 {raw:,} B（{raw/1048576:.2f} MiB），deflate 后约 {comp:,} B（{comp/1048576:.2f} MiB）")


if __name__ == "__main__":
    main()
