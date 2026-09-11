"""AnMusic 图标生成脚本。

源图：仓库根目录的 AnMusic.png（1254×1254 品牌图：深蓝紫渐变底 + 白色音符/耳机 + 青粉渐变字）。
本脚本把它加工成应用真正使用的两个文件（都在本目录）：

  app.ico       多尺寸图标（256/128/64/48/32/24/16），供 exe、窗口、任务栏、安装包使用
  app-icon.png  256×256 预览图（README / 商店图示用）

用法（在本目录直接跑，也可从仓库根跑）：
    python make-icon.py                 # 默认取 ../../../AnMusic.png
    python make-icon.py path/to/x.png   # 指定源图

处理细节：
* 源图自带透明留白（约 3%），先裁到不透明外接框再补 1% 透明边，
  这样任务栏/标题栏里的小尺寸图标不会显得又小又空；
* 各尺寸统一用 LANCZOS 降采样，避免小尺寸出现锯齿；
* 非正方形源图会补成正方形（居中），保证图标不被拉伸变形。
"""
import sys
from pathlib import Path

from PIL import Image

SIZES = [256, 128, 64, 48, 32, 24, 16]

# 源图相对本脚本的默认位置：src/AnMusic/Assets/ → 仓库根
DEFAULT_SOURCE = Path(__file__).resolve().parents[3] / "AnMusic.png"

# 裁掉透明边后保留的透明留白（占边长比例）
PADDING_RATIO = 0.01


def load_square(source: Path) -> Image.Image:
    """读入源图并整理成"正方形、内容饱满"的 RGBA 画布。"""
    im = Image.open(source).convert("RGBA")

    # 1) 裁掉整片透明留白：阈值取 8 以忽略抗锯齿边缘的零星像素
    solid = im.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    if solid:
        im = im.crop(solid)

    # 2) 补成正方形（居中），避免后续缩放时被拉伸
    side = max(im.size)
    if im.size != (side, side):
        square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
        square.paste(im, ((side - im.width) // 2, (side - im.height) // 2))
        im = square

    # 3) 四边留一点点透明边距，圆角不会顶到画布边上
    pad = round(side * PADDING_RATIO)
    canvas = Image.new("RGBA", (side + pad * 2, side + pad * 2), (0, 0, 0, 0))
    canvas.paste(im, (pad, pad))
    return canvas


def main() -> None:
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_SOURCE
    if not source.is_file():
        raise SystemExit(f"找不到源图：{source}\n用法：python make-icon.py [源图路径]")

    out_dir = Path(__file__).resolve().parent
    base = load_square(source)
    images = [base.resize((s, s), Image.LANCZOS) for s in SIZES]

    # 预览图（不透明背景上更好看清圆角与留白）
    preview = Image.new("RGBA", images[0].size, (0, 0, 0, 0))
    preview.alpha_composite(images[0])
    preview.save(out_dir / "app-icon.png")

    # 多尺寸 ico：PIL 会为每个尺寸各存一张（256 用 PNG 压缩，小尺寸用 BMP）
    images[0].save(
        out_dir / "app.ico",
        format="ICO",
        sizes=[(s, s) for s in SIZES],
    )

    print(f"源图：{source}  ({Image.open(source).size[0]}×{Image.open(source).size[1]})")
    print("已生成 app-icon.png 与 app.ico：", ", ".join(f"{s}x{s}" for s in SIZES))


if __name__ == "__main__":
    main()
