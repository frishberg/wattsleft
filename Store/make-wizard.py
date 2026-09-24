"""Installer art, in the same family as the Store logos: the tile on a light graded background with a real shadow,
the name and one line beneath.

  wizard-large-*.png   the tall banner on the Welcome and Finished pages, 164x314 at 100 %
  wizard-small-*.png   the tile in the top-right corner of the other pages, 55x55 at 100 %
Each is drawn natively at 100, 125, 150, 175, 200 and 250 %, and Inno Setup picks the closest for the screen.
Run:  python make-wizard.py   (from this folder)
"""
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import os

HERE = os.path.dirname(os.path.abspath(__file__))
TILE = os.path.join(HERE, "..", "Assets", "Square150x150Logo.scale-400.png")   # 600 px, rounded corners baked in
INK, MUTED, BLUE = (14, 26, 43), (91, 107, 128), (0, 103, 192)
FONTS = r"C:\Windows\Fonts"
SCALES = (100, 125, 150, 175, 200, 250)


def background(w, h):
    top, bottom = (247, 249, 252), (226, 234, 245)
    grad = Image.new("RGB", (1, h))
    for y in range(h):
        t = y / (h - 1)
        grad.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    bg = grad.resize((w, h)).convert("RGBA")
    bloom = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(bloom).ellipse((-w * 0.2, h * 0.12, w * 1.2, h * 0.62), fill=BLUE + (46,))
    return Image.alpha_composite(bg, bloom.filter(ImageFilter.GaussianBlur(w * 0.16)))


def tile_with_shadow(canvas, size, x, y):
    tile = Image.open(TILE).convert("RGBA").resize((size, size), Image.LANCZOS)
    pad = size // 2
    sh = Image.new("RGBA", (size + pad * 2, size + pad * 2), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((pad, pad + size * 0.06, pad + size, pad + size + size * 0.06), radius=size * 0.22, fill=(10, 30, 70, 120))
    canvas.alpha_composite(sh.filter(ImageFilter.GaussianBlur(size * 0.09)), (x - pad, y - pad))
    canvas.alpha_composite(tile, (x, y))


for pct in SCALES:
    k = pct / 100
    w, h = round(164 * k), round(314 * k)
    c = background(w, h)
    size = round(w * 0.56)
    tile_with_shadow(c, size, (w - size) // 2, round(h * 0.27))
    d = ImageDraw.Draw(c)
    f1 = ImageFont.truetype(os.path.join(FONTS, "seguisb.ttf"), round(w * 0.135))
    f2 = ImageFont.truetype(os.path.join(FONTS, "segoeui.ttf"), round(w * 0.068))
    y = round(h * 0.27) + size + round(h * 0.085)
    for s, f, dy, col in (("Watt's Left", f1, 0, INK), ("Is your charger keeping up?", f2, round(w * 0.2), MUTED)):
        d.text(((w - d.textlength(s, font=f)) / 2, y + dy), s, font=f, fill=col)
    c.convert("RGB").save(os.path.join(HERE, f"wizard-large-{pct}.png"), "PNG", optimize=True)

    s = round(55 * k)
    Image.open(TILE).convert("RGBA").resize((s, s), Image.LANCZOS).save(os.path.join(HERE, f"wizard-small-{pct}.png"), "PNG", optimize=True)
    print("saved", pct, (w, h), (s, s))
