"""Composes the Store screenshots (1920x1080) from clean captures of the app.

Inputs (2x captures, client area only, no window frame):
  shots/compact.png   the window without graphs
  shots/full.png      the window with graphs and stats on
  shots/full80.png    the same window at 80%, for the hover shot
  shots/card80.png    a real hover card, cropped from a live capture of that
  shots/party.png     the compact window with the little guy mid-dance on the bar
Run:  python make-screenshots.py   (from this folder)
"""
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(HERE, "shots")
W, H = 1920, 1080
INK, MUTED, BLUE = (14, 26, 43), (91, 107, 128), (0, 103, 192)
FONTS = r"C:\Windows\Fonts"
display = lambda size: ImageFont.truetype(os.path.join(FONTS, "seguisb.ttf"), size)   # Segoe UI Semibold
text = lambda size: ImageFont.truetype(os.path.join(FONTS, "segoeui.ttf"), size)


def background():
    """Off-white to pale blue, top to bottom, with a soft blue bloom where the window sits."""
    top, bottom = (247, 249, 252), (226, 234, 245)
    grad = Image.new("RGB", (1, H))
    for y in range(H):
        t = y / (H - 1)
        grad.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    bg = grad.resize((W, H)).convert("RGBA")
    bloom = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(bloom).ellipse((980, 40, 1900, 1180), fill=BLUE + (40,))
    bloom = bloom.filter(ImageFilter.GaussianBlur(160))
    return Image.alpha_composite(bg, bloom)


def rounded(im, radius):
    mask = Image.new("L", (im.width * 3, im.height * 3), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width * 3 - 1, im.height * 3 - 1), radius=radius * 3, fill=255)
    out = Image.new("RGBA", im.size, (0, 0, 0, 0))
    out.paste(im.convert("RGBA"), (0, 0), mask.resize(im.size, Image.LANCZOS))
    return out


def place(canvas, im, x, y, radius=18, shadow=True):
    """A window or card on the canvas, with rounded corners and a soft drop shadow beneath it."""
    im = rounded(im, radius)
    if shadow:
        pad = 120
        sh = Image.new("RGBA", (im.width + pad * 2, im.height + pad * 2), (0, 0, 0, 0))
        ImageDraw.Draw(sh).rounded_rectangle((pad, pad + 28, pad + im.width, pad + im.height + 28), radius=radius, fill=(10, 30, 70, 110))
        sh = sh.filter(ImageFilter.GaussianBlur(38))
        canvas.alpha_composite(sh, (x - pad, y - pad))
    canvas.alpha_composite(im, (x, y))


def wrap(draw, s, font, max_width):
    lines, line = [], ""
    for word in s.split():
        trial = (line + " " + word).strip()
        if draw.textlength(trial, font=font) <= max_width:
            line = trial
        else:
            lines.append(line); line = word
    lines.append(line)
    return lines


def headline(canvas, title, sub, x=150, y=330, width=760):
    d = ImageDraw.Draw(canvas)
    f1, f2 = display(78), text(30)
    for i, ln in enumerate(title.split("\n")):
        d.text((x, y + i * 92), ln, font=f1, fill=INK)
    yy = y + 92 * len(title.split("\n")) + 28
    for ln in wrap(d, sub, f2, width):
        d.text((x, yy), ln, font=f2, fill=MUTED); yy += 44


def fit(im, scale):
    return im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)


def save(canvas, name):
    canvas.convert("RGB").save(os.path.join(HERE, name), "PNG", optimize=True)
    print("saved", name)


compact = Image.open(os.path.join(SHOTS, "compact.png")).convert("RGBA")
full = Image.open(os.path.join(SHOTS, "full.png")).convert("RGBA")
full80 = Image.open(os.path.join(SHOTS, "full80.png")).convert("RGBA")
card = Image.open(os.path.join(SHOTS, "card80.png")).convert("RGBA")

# 1. the point of the app: the compact window, large, with the three numbers explained
c = background()
win = fit(compact, 0.98)
place(c, win, 1120, (H - win.height) // 2)
headline(c, "Is your charger\nkeeping up?",
         "IN is what the charger gives. OUT is what the laptop uses. NET is what's left for the battery. "
         "If it goes negative, the app says so, with how long you've got.")
save(c, "screenshot-1.png")

# 2. the history: the full window, big, bleeding off the bottom edge
c = background()
win = fit(full, 0.78)
place(c, win, 1120, 110)
headline(c, "Thirty minutes\nof history.",
         "Charge, charger watts, laptop watts and voltage, sampled every five seconds and kept across restarts. "
         "Hover any point for the value at that moment.")
save(c, "screenshot-2.png")

# 3. hover: the window with a real card open beside OUT
c = background()
scale = 0.78
win = fit(full80, scale)
cd = fit(card, scale)
wx, wy = 900, 110
place(c, win, wx, wy)
place(c, cd, wx + win.width + round(20 * scale), wy + round(142 * scale), radius=14)
headline(c, "Hover anything.\nIt explains itself.",
         "Two plain sentences for every number, with what the last half minute looked like.", width=700)
save(c, "screenshot-3.png")

# 4. the little guy, who does not exist
c = background()
party = Image.open(os.path.join(SHOTS, "party.png")).convert("RGBA")
win = fit(party, 0.98)
place(c, win, 1120, (H - win.height) // 2)
headline(c, "What's with the\nlittle dancing guy?",
         "These wild rumors are speculative and baseless. Next question.")
save(c, "screenshot-4.png")
