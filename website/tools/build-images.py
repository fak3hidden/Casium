#!/usr/bin/env python3
"""
Casium — brand image builder
============================
Renders the Open Graph card and the PNG icons from the same design tokens as
the site, using the self-hosted fonts. Nothing here is hand-drawn in an image
editor, so the assets can always be regenerated:

    python3 tools/build-images.py           (needs: pillow, fonttools, brotli)

Outputs
    public/assets/img/og.png               1200x630 social card
    public/assets/img/apple-touch-icon.png  180x180
    public/assets/img/favicon-32.png         32x32  (legacy fallback)
"""

from __future__ import annotations

import io
import os
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFilter, ImageFont

try:
    from fontTools.ttLib import TTFont
    from fontTools.varLib import instancer
except ImportError:  # pragma: no cover
    sys.exit("fonttools + brotli are required:  pip install pillow fonttools brotli")

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = os.path.dirname(HERE)
FONTS = os.path.join(SITE, "public", "assets", "fonts")
OUT = os.path.join(SITE, "public", "assets", "img")

# ---------------------------------------------------------------- tokens
BG = (10, 11, 13)
BG_2 = (13, 15, 18)
SURFACE = (17, 19, 23)
LINE = (33, 37, 43)
LINE_2 = (44, 50, 59)
INK = (238, 240, 243)
INK_2 = (163, 169, 179)
INK_3 = (109, 115, 125)
INK_4 = (77, 83, 92)
EMBER = (255, 92, 43)
MINT = (70, 201, 140)
CODE_KEY = (255, 130, 86)
CODE_STR = (155, 214, 138)
CODE_COM = (91, 99, 110)
CODE_FX = (127, 178, 232)

SS = 3  # supersampling factor


# ---------------------------------------------------------------- fonts
def load(name: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(name, size)


def static_from_variable(woff2: str, weight: int, tmp: str) -> str:
    """Freeze one weight out of a variable woff2 so Pillow can render it."""
    font = TTFont(woff2)
    if "fvar" in font:
        font = instancer.instantiateVariableFont(font, {"wght": weight}, inplace=False)
    else:
        font.flavor = None
    out = os.path.join(tmp, f"{os.path.basename(woff2)}-{weight}.ttf")
    font.flavor = None
    font.save(out)
    return out


def prepare_fonts(tmp: str) -> dict[str, str]:
    paths = {}
    paths["archivo-700"] = static_from_variable(os.path.join(FONTS, "archivo-var.woff2"), 700, tmp)
    paths["archivo-600"] = static_from_variable(os.path.join(FONTS, "archivo-var.woff2"), 600, tmp)
    for weight in (400, 500):
        paths[f"mono-{weight}"] = ttf(os.path.join(FONTS, f"plex-mono-{weight}.woff2"), tmp)
    for weight in (500, 600):
        paths[f"cond-{weight}"] = ttf(os.path.join(FONTS, f"plex-cond-{weight}.woff2"), tmp)
    return paths


def ttf(woff2: str, tmp: str) -> str:
    font = TTFont(woff2)
    out = os.path.join(tmp, os.path.basename(woff2) + ".ttf")
    font.flavor = None
    font.save(out)
    return out


# ---------------------------------------------------------------- drawing
def tracked(draw: ImageDraw.ImageDraw, xy, text, font, fill, tracking=0):
    """Pillow has no letter-spacing, so place glyphs one at a time."""
    x, y = xy
    for char in text:
        draw.text((x, y), char, font=font, fill=fill)
        x += draw.textlength(char, font=font) + tracking
    return x


def tracked_len(draw: ImageDraw.ImageDraw, text, font, tracking=0):
    return sum(draw.textlength(c, font=font) + tracking for c in text) - (tracking if text else 0)


def mark(draw: ImageDraw.ImageDraw, box, ring, arc, dot, width):
    x0, y0, x1, y1 = box
    radius = (x1 - x0) * 0.235
    draw.rounded_rectangle(box, radius=radius, outline=ring, width=max(1, width // 3))
    inset = (x1 - x0) * 0.24
    draw.arc([x0 + inset, y0 + inset, x1 - inset, y1 - inset], 50, 310, fill=arc, width=width)
    r = (x1 - x0) * 0.075
    cx = x0 + (x1 - x0) * 0.753
    cy = (y0 + y1) / 2
    draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=dot)


def glow(size, centre, radius, colour, strength=1.0):
    """Soft radial glow, built by blurring a filled disc."""
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = centre
    d.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=colour + (int(150 * strength),))
    return layer.filter(ImageFilter.GaussianBlur(radius * 0.55))


def grid(size, step, colour, fade_from):
    layer = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    w, h = size
    for x in range(0, w, step):
        d.line([(x, 0), (x, h)], fill=colour, width=1)
    for y in range(0, h, step):
        d.line([(0, y), (w, y)], fill=colour, width=1)
    mask = Image.new("L", size, 0)
    md = ImageDraw.Draw(mask)
    for y in range(h):
        alpha = 255 if y < fade_from else max(0, int(255 * (1 - (y - fade_from) / (h - fade_from)) ** 1.4))
        md.line([(0, y), (w, y)], fill=alpha)
    layer.putalpha(mask)
    return layer


# ---------------------------------------------------------------- og card
def build_og(fonts: dict[str, str], path: str) -> None:
    W, H = 1200 * SS, 630 * SS
    img = Image.new("RGBA", (W, H), BG + (255,))

    img.alpha_composite(grid((W, H), 64 * SS, (255, 255, 255, 5), int(H * 0.68)))
    img.alpha_composite(glow((W, H), (int(W * 0.88), int(H * 0.0)), int(W * 0.40), EMBER, 0.26))

    # oversized watermark mark, bottom-right, barely there
    wm = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    wd = ImageDraw.Draw(wm)
    box = [int(W * 0.72), int(H * 0.42), int(W * 1.06), int(H * 1.10)]
    wd.arc(box, 50, 310, fill=(255, 92, 43, 26), width=int(26 * SS))
    img.alpha_composite(wm.filter(ImageFilter.GaussianBlur(1.2 * SS)))

    d = ImageDraw.Draw(img)

    # ---- wordmark
    mx, my = 96 * SS, 74 * SS
    mark(d, (mx, my, mx + 50 * SS, my + 50 * SS), LINE_2, INK, EMBER, int(5.2 * SS))
    f_word = load(fonts["archivo-700"], 38 * SS)
    word_end = tracked(d, (mx + 70 * SS, my + 7 * SS), "CASIUM", f_word, INK, tracking=int(7 * SS))
    d.line([(word_end + 18 * SS, my + 8 * SS), (word_end + 18 * SS, my + 42 * SS)], fill=LINE_2, width=1 * SS)
    tracked(
        d,
        (word_end + 38 * SS, my + 19 * SS),
        "WINDOWS EXECUTOR",
        load(fonts["mono-400"], 18 * SS),
        INK_4,
        tracking=int(3.4 * SS),
    )

    # ---- headline (measured so it can never overflow)
    max_w = W - 2 * 96 * SS
    size = 84 * SS
    f_big = load(fonts["archivo-700"], size)
    line1 = "Injection is the easy part."
    line2 = "Staying attached isn’t."
    while max(d.textlength(line1, font=f_big), d.textlength(line2, font=f_big)) > max_w and size > 40 * SS:
        size -= 2 * SS
        f_big = load(fonts["archivo-700"], size)

    hx, hy = 96 * SS, 188 * SS
    lh = int(size * 1.16)
    d.text((hx, hy), line1, font=f_big, fill=INK)
    d.text((hx, hy + lh), line2, font=f_big, fill=INK_2)
    d.rectangle([hx, hy + lh + int(size * 1.02), hx + d.textlength(line2, font=f_big), hy + lh + int(size * 1.02) + 7 * SS], fill=EMBER)

    # ---- terminal strip
    ty = hy + lh * 2 + int(size * 0.92)
    f_mono = load(fonts["mono-400"], 19 * SS)
    snippet = "casium --attach"
    result = "session ok · key expires 2027-09-05"
    pad = 16 * SS
    inner_w = int(d.textlength("$ ", font=f_mono) + d.textlength(snippet, font=f_mono)
                  + 34 * SS + d.textlength(result, font=f_mono) + pad * 2)
    strip_h = 52 * SS
    d.rounded_rectangle([hx, ty, hx + inner_w, ty + strip_h], radius=8 * SS, fill=BG_2 + (255,), outline=LINE_2, width=1 * SS)
    x = hx + pad
    d.text((x, ty + 14 * SS), "$ ", font=f_mono, fill=INK_4)
    x += d.textlength("$ ", font=f_mono)
    d.text((x, ty + 14 * SS), snippet, font=f_mono, fill=INK)
    x += d.textlength(snippet, font=f_mono) + 14 * SS
    tracked(d, (x, ty + 14 * SS), "->", load(fonts["mono-400"], 19 * SS), EMBER)
    x += d.textlength("->", font=f_mono) + 14 * SS
    d.ellipse([x, ty + 21 * SS, x + 9 * SS, ty + 30 * SS], fill=MINT)
    x += 18 * SS
    d.text((x, ty + 14 * SS), result, font=f_mono, fill=INK_2)

    # ---- footer strip
    fy = 560 * SS
    d.line([(96 * SS, fy - 30 * SS), (W - 96 * SS, fy - 30 * SS)], fill=LINE, width=1 * SS)
    d.rectangle([96 * SS, fy - 31 * SS, 96 * SS + 46 * SS, fy - 30 * SS], fill=EMBER)
    tracked(d, (96 * SS, fy), "casium.top", load(fonts["mono-500"], 22 * SS), INK, tracking=int(1.5 * SS))
    right = "BUBBLEAPI CORE · MONACO · KEY LICENSING"
    f_small = load(fonts["mono-400"], 15 * SS)
    rw = tracked_len(d, right, f_small, tracking=int(2.2 * SS))
    tracked(d, (W - 96 * SS - rw, fy + 4 * SS), right, f_small, INK_4, tracking=int(2.2 * SS))

    img.convert("RGB").save(path, "PNG", optimize=True)
    print(f"  wrote {os.path.relpath(path, SITE)}  ({os.path.getsize(path) // 1024} KB)")


# ---------------------------------------------------------------- icons
def build_icon(fonts: dict[str, str], path: str, size: int, rounded_bg: bool) -> None:
    S = size * SS
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    if rounded_bg:
        d.rounded_rectangle([0, 0, S, S], radius=int(S * 0.22), fill=(15, 17, 21))
    else:
        d.rectangle([0, 0, S, S], fill=(15, 17, 21))

    pad = S * 0.19
    inset = pad
    d.arc(
        [inset, inset, S - inset, S - inset],
        50,
        310,
        fill=INK,
        width=int(S * 0.115),
    )
    r = S * 0.085
    cx = S * 0.755
    cy = S / 2
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=EMBER)

    img.convert("RGBA").resize((size, size), Image.LANCZOS).save(path, "PNG", optimize=True)
    print(f"  wrote {os.path.relpath(path, SITE)}  ({os.path.getsize(path) // 1024} KB)")


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        print("Preparing fonts…")
        fonts = prepare_fonts(tmp)
        print("Rendering…")
        build_og(fonts, os.path.join(OUT, "og.png"))
        build_icon(fonts, os.path.join(OUT, "apple-touch-icon.png"), 180, rounded_bg=False)
        build_icon(fonts, os.path.join(OUT, "favicon-32.png"), 32, rounded_bg=True)


if __name__ == "__main__":
    main()
