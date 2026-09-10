"""AnMusic 图标生成脚本。

设计：圆角方块 + 品牌渐变底色（科技蓝 → 夜紫）+ 白色双八分音符，
音符由「A」的斜杠笔势构成：两根符干 + 顶部连线，整体既像音符又暗含 AnMusic 的 A。

用法：python make-icon.py          # 生成 app-icon.png(256) 与多尺寸 app.ico
"""
from PIL import Image, ImageDraw

SIZES = [256, 128, 64, 48, 32, 24, 16]

# 品牌渐变（与默认强调色一致）：左上 #2B7DE9 → 右下 #6C4BE0
C_TOP = (43, 125, 233)
C_BOTTOM = (108, 75, 224)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_mask(size, radius_ratio=0.22):
    """圆角方块遮罩（4x 超采样抗锯齿）。"""
    ss = 4
    big = Image.new("L", (size * ss, size * ss), 0)
    ImageDraw.Draw(big).rounded_rectangle(
        [0, 0, size * ss - 1, size * ss - 1],
        radius=int(size * ss * radius_ratio),
        fill=255,
    )
    return big.resize((size, size), Image.LANCZOS)


def diagonal_gradient(size):
    """对角线性渐变。"""
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = lerp(C_TOP, C_BOTTOM, t)
    return img


def draw_notes(size, scale=1.0):
    """白色（微透明）双八分音符：两根符干 + 符头 + 顶部符杠，符杠略带 A 的斜势。

    坐标为 0~1 归一化，乘 size 后绘制，保证任意尺寸比例一致。
    """
    ss = 4
    layer = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    def s(v):
        # 以中心为原点、按 scale 缩放后再做整体微调（让图形在方块里更饱满居中）
        return ((0.5 + (v[0] - 0.5) * scale + 0.008) * size * ss,
                (0.5 + (v[1] - 0.5) * scale - 0.012) * size * ss)

    def rect(x0, y0, x1, y1, r):
        p0, p1 = s((x0, y0)), s((x1, y1))
        d.rounded_rectangle([p0, p1], radius=r * size * ss, fill=(255, 255, 255, 255))

    def ellipse(cx, cy, rx, ry):
        p0, p1 = s((cx - rx, cy - ry)), s((cx + rx, cy + ry))
        d.ellipse([p0, p1], fill=(255, 255, 255, 255))

    stem_w = 0.056
    # 左符干与符头（符头略扁，更易在小尺寸辨认）
    rect(0.392, 0.238, 0.392 + stem_w, 0.706, stem_w / 2)
    ellipse(0.350, 0.706, 0.080, 0.066)
    # 右符干与符头（略低，形成八分音符的错落）
    rect(0.612, 0.238, 0.612 + stem_w, 0.626, stem_w / 2)
    ellipse(0.570, 0.626, 0.080, 0.066)
    # 顶部符杠：上沿水平、下沿右高左低，形成 A 的斜势
    d.polygon(
        [s((0.386, 0.238)), s((0.666, 0.238)), s((0.666, 0.352)), s((0.386, 0.400))],
        fill=(255, 255, 255, 255),
    )

    return layer.resize((size, size), Image.LANCZOS)


def render(size):
    base = diagonal_gradient(size).convert("RGBA")
    base.putalpha(rounded_mask(size))
    notes = draw_notes(size, scale=1.16)
    base.alpha_composite(notes)
    return base


def main():
    images = [render(s) for s in SIZES]
    images[0].save("app-icon.png")  # 256 预览图（README / 商店图示用）
    images[0].save(
        "app.ico",
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=images[1:],
    )
    print("已生成 app-icon.png 与 app.ico：", ", ".join(f"{s}x{s}" for s in SIZES))


if __name__ == "__main__":
    main()
