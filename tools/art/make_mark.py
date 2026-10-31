# Hyperdrive mark: a heavy italic H whose crossbar runs out through the left stem as two dashes (a speed trail), set
# with the name in DIN lettering (Bahnschrift). Flat shapes only, two inks, drawn as polygons at 4096 px.
# usage: python make_mark.py <out dir>   -> mark/lockup PNGs (black on white, white on black, transparent) and a sheet
import os, sys
from PIL import Image, ImageDraw, ImageFont

INK, PAPER = (17, 18, 22), (243, 241, 236)
BAHN = "C:/Windows/Fonts/bahnschrift.ttf"
SLANT = 0.19          # italic: x moves right by SLANT * height above the baseline
R = 4096              # render size of the mark


# Mark geometry on a 1000 grid, y down, baseline at 900, cap height 800 (upright; slanted when drawn).
STEM, BAR = 168, 132
LEFT, COUNTER = 380, 176
RIGHT = LEFT + STEM + COUNTER
BAR_Y = 900 - 800 * 0.56          # crossbar a little above the middle, as in DIN
GAP = 46
DASHES = [214, 96]                # trail lengths, right to left


def shapes():
    """Upright rectangles (x0, y0, x1, y1) of the mark."""
    r = [(LEFT, 100, LEFT + STEM, 900), (RIGHT, 100, RIGHT + STEM, 900), (LEFT + STEM, BAR_Y - BAR / 2, RIGHT, BAR_Y + BAR / 2)]
    x = LEFT - GAP
    for length in DASHES:
        r.append((x - length, BAR_Y - BAR / 2, x, BAR_Y + BAR / 2))
        x -= length + GAP
    return r


def slant(x, y):
    return x + SLANT * (900 - y), y


def mark(ink, size=1024, pad=0.08):
    rects = shapes()
    pts = [slant(x, y) for (x0, y0, x1, y1) in rects for (x, y) in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
    minx, maxx = min(p[0] for p in pts), max(p[0] for p in pts)
    w, h = maxx - minx, 800
    k = R * (1 - 2 * pad) / max(w, h)
    ox, oy = (R - w * k) / 2 - minx * k, (R - h * k) / 2 - 100 * k
    img = Image.new("RGBA", (R, R), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for (x0, y0, x1, y1) in rects:
        quad = [slant(x, y) for (x, y) in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
        d.polygon([(ox + x * k, oy + y * k) for x, y in quad], fill=ink)
    return img.resize((size, size), Image.LANCZOS)


def wordmark(ink, cap):
    """HYPERDRIVE, Bahnschrift Bold SemiCondensed, tracked, slanted like the mark; cap = cap height in px."""
    f = ImageFont.truetype(BAHN, int(cap * 4 / 0.70))
    f.set_variation_by_name("Bold SemiCondensed")
    text, track = "HYPERDRIVE", 0.075
    size = f.size
    widths = [f.getlength(c) for c in text]
    w = int(sum(widths) + track * size * len(text) + size)
    img = Image.new("L", (w, int(size * 1.4)), 0)
    d = ImageDraw.Draw(img)
    x, base = size * 0.3, size * 1.1
    for c, cw in zip(text, widths):
        d.text((x, base), c, font=f, fill=255, anchor="ls")
        x += cw + track * size
    # slant around the baseline: x_src = x_dst - SLANT * (base - y_dst)
    img = img.transform(img.size, Image.AFFINE, (1, SLANT, -SLANT * base, 0, 1, 0), resample=Image.BICUBIC)
    img = img.crop(img.getbbox())
    img = img.resize((img.width // 4, img.height // 4), Image.LANCZOS)
    out = Image.new("RGBA", img.size, ink + (0,))
    out.putalpha(img)
    return out


def lockup(ink, bg, height=400):
    m = mark(ink, height, pad=0.0).crop()
    m = m.crop(m.getbbox())
    word = wordmark(ink, int(m.height * 0.46))
    gap, margin = int(m.height * 0.30), int(m.height * 0.55)
    c = Image.new("RGBA", (m.width + gap + word.width + 2 * margin, m.height + 2 * margin), bg + (255,) if bg else (0, 0, 0, 0))
    c.alpha_composite(m, (margin, margin))
    # the name sits on the mark's baseline
    c.alpha_composite(word, (margin + m.width + gap, margin + m.height - word.height))
    return c


def tile(ink, bg, size, radius=0.18, pad=0.17):
    c = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
    ImageDraw.Draw(c).rounded_rectangle([0, 0, size * 4 - 1, size * 4 - 1], radius=int(size * 4 * radius), fill=bg + (255,))
    c = c.resize((size, size), Image.LANCZOS)
    c.alpha_composite(mark(ink, size, pad=pad))
    return c


def main(OUT):
    os.makedirs(OUT, exist_ok=True)
    mark(INK).save(os.path.join(OUT, "mark_black.png"))
    mark(PAPER).save(os.path.join(OUT, "mark_white.png"))
    tile(PAPER, INK, 512).save(os.path.join(OUT, "icon_tile.png"))
    lockup(INK, PAPER).save(os.path.join(OUT, "lockup_light.png"))
    lockup(PAPER, INK).save(os.path.join(OUT, "lockup_dark.png"))
    lockup(INK, None).save(os.path.join(OUT, "lockup_transparent.png"))

    # Sheet: lockups on both grounds, the mark large, then the sizes the game and SpaceDock show.
    lab = ImageFont.truetype(BAHN, 22)
    W = 1800
    light, dark = lockup(INK, PAPER, 300), lockup(PAPER, INK, 300)
    sheet = Image.new("RGBA", (W, 2400), (226, 225, 220, 255))
    d = ImageDraw.Draw(sheet)
    sheet.alpha_composite(light, ((W - light.width) // 2, 40))
    sheet.alpha_composite(dark, ((W - dark.width) // 2, 80 + light.height))
    y = 120 + light.height * 2
    big = Image.new("RGBA", (560, 560), PAPER + (255,))
    big.alpha_composite(mark(INK, 560))
    sheet.alpha_composite(big, (60, y))
    sheet.alpha_composite(tile(PAPER, INK, 300), (680, y))
    x = 1040
    for s in (128, 64, 38, 24):
        for i, (ink, bg) in enumerate(((INK, PAPER), (PAPER, INK))):
            b = Image.new("RGBA", (s, s), bg + (255,))
            b.alpha_composite(mark(ink, s, pad=0.06))
            sheet.alpha_composite(b, (x, y + i * (s + 20)))
        d.text((x, y + 2 * s + 50), str(s) + " px", font=lab, fill=INK + (255,))
        x += s + 60
    sheet.alpha_composite(tile(PAPER, INK, 64), (1040, y + 400))
    sheet.alpha_composite(tile(PAPER, INK, 38), (1124, y + 413))
    d.text((1040, y + 480), "barre d'outils", font=lab, fill=INK + (255,))
    sheet = sheet.crop((0, 0, W, y + 600))
    sheet.convert("RGB").save(os.path.join(OUT, "planche_logo.png"))
    print("ok", sheet.size)


if __name__ == "__main__":
    main(sys.argv[1])
