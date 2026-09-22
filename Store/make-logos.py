"""Store logo art, in the same family as the screenshots: the tile on a light graded background with a real shadow.

  boxart-1080.png       1:1, the tile alone
  poster-720x1080.png   2:3, the tile with the name and one line
  tile-300.png          the bare tile on transparent, for the Store's app icon slot
Run:  python make-logos.py   (from this folder)
"""
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import os

HERE = os.path.dirname(os.path.abspath(__file__))
TILE = os.path.join(HERE, "..", "Assets", "Square150x150Logo.scale-400.png")   # 600 px, rounded corners baked in
INK, MUTED, BLUE = (14, 26, 43), (91, 107, 128), (0, 103, 192)
FONTS = r"C:\Windows\Fonts"


def background(w, h):
    top, bottom = (247, 249, 252), (226, 234, 245)
    grad = Image.new("RGB", (1, h))
    for y in range(h):
        t = y / (h - 1)
        grad.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    bg = grad.resize((w, h)).convert("RGBA")
    bloom = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(bloom).ellipse((w * 0.1, h * 0.05, w * 0.9, h * 0.75), fill=BLUE + (46,))
    return Image.alpha_composite(bg, bloom.filter(ImageFilter.GaussianBlur(w * 0.12)))


def tile_with_shadow(canvas, size, x, y):
    tile = Image.open(TILE).convert("RGBA").resize((size, size), Image.LANCZOS)
    pad = size // 3
    sh = Image.new("RGBA", (size + pad * 2, size + pad * 2), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle((pad, pad + size * 0.06, pad + size, pad + size + size * 0.06), radius=size * 0.22, fill=(10, 30, 70, 120))
    canvas.alpha_composite(sh.filter(ImageFilter.GaussianBlur(size * 0.09)), (x - pad, y - pad))
    canvas.alpha_composite(tile, (x, y))


def save(canvas, name):
    canvas.convert("RGB").save(os.path.join(HERE, name), "PNG", optimize=True)
    print("saved", name)


# 1:1 box art: the tile, large and centred
c = background(1080, 1080)
tile_with_shadow(c, 640, 220, 200)
save(c, "boxart-1080.png")

# 2:3 poster: the tile up top, the name and one line beneath in ink
c = background(720, 1080)
tile_with_shadow(c, 400, 160, 238)   # the tile plus the two lines span about 605 px; this centres the group
d = ImageDraw.Draw(c)
f1 = ImageFont.truetype(os.path.join(FONTS, "seguisb.ttf"), 62)
f2 = ImageFont.truetype(os.path.join(FONTS, "segoeui.ttf"), 26)
for s, f, y, col in (("Watt's Left", f1, 718, INK), ("Is your charger keeping up?", f2, 808, MUTED)):
    d.text(((720 - d.textlength(s, font=f)) / 2, y), s, font=f, fill=col)
save(c, "poster-720x1080.png")

# the bare tile at 300 for the app icon slot
Image.open(TILE).convert("RGBA").resize((300, 300), Image.LANCZOS).save(os.path.join(HERE, "tile-300.png"), "PNG", optimize=True)
print("saved tile-300.png")
