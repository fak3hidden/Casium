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
    paths["sans-400"] = ttf(os.path.join(FONTS, "plex-sans-400.woff2"), tmp)
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
    """One black screen: mark, name, sentence, three pills — same as the site."""
    W, H = 1200 * SS, 630 * SS
    img = Image.new("RGBA", (W, H), BG + (255,))
    img.alpha_composite(glow((W, H), (W // 2, int(H * 0.42)), int(W * 0.34), EMBER, 0.22))
    d = ImageDraw.Draw(img)

    cx = W // 2

    # mark
    ms = 52 * SS
    mark(d, (cx - ms // 2, int(H * 0.20) - ms // 2, cx + ms // 2, int(H * 0.20) + ms // 2),
         (6, 7, 8), INK, EMBER, int(3.4 * SS))

    # name
    f_name = load(fonts["archivo-600"], 78 * SS)
    name = "Casium"
    nw = d.textlength(name, font=f_name)
    d.text((cx - nw / 2, int(H * 0.30)), name, font=f_name, fill=INK)

    # tagline
    f_tag = load(fonts["sans-400"], 26 * SS)
    tag = "A powerful Lua executor aimed to give you the best scripting experience."
    tw = tracked_len(d, tag, f_tag, tracking=int(0.4 * SS))
    if tw > W - 180 * SS:
        f_tag = load(fonts["sans-400"], 23 * SS)
        tw = d.textlength(tag, font=f_tag)
        tracked_plain = True
    else:
        tracked_plain = False
    if tracked_plain:
        d.text((cx - tw / 2, int(H * 0.475)), tag, font=f_tag, fill=INK_3)
    else:
        tracked(d, (cx - tw / 2, int(H * 0.475)), tag, f_tag, INK_3, tracking=int(0.4 * SS))

    # pills
    pill_labels = [("Download", True), ("Discord", False), ("FAQ", False)]
    f_pill = load(fonts["sans-400"], 22 * SS)
    pad_x = 34 * SS
    height = 56 * SS
    gap = 14 * SS
    widths = [tracked_len(d, t, f_pill, tracking=int(1.2 * SS)) + pad_x * 2 for t, _ in pill_labels]
    total = sum(widths) + gap * (len(pill_labels) - 1)
    x = cx - total / 2
    y = int(H * 0.60)
    for (label, solid), w in zip(pill_labels, widths):
        d.rounded_rectangle(
            [x, y, x + w, y + height],
            radius=height // 2,
            fill=(242, 243, 245, 255) if solid else (6, 7, 8, 255),
            outline=None if solid else LINE_2,
            width=1 * SS,
        )
        tracked(d, (x + pad_x, y + height / 2 - 13 * SS), label, f_pill,
                (10, 11, 12) if solid else INK_2, tracking=int(1.2 * SS))
        x += w + gap

    # footer
    f_foot = load(fonts["mono-400"], 16 * SS)
    foot = "casium.top  ·  windows 10 / 11 x64"
    fw = tracked_len(d, foot, f_foot, tracking=int(2.4 * SS))
    tracked(d, (cx - fw / 2, int(H * 0.865)), foot, f_foot, INK_4, tracking=int(2.4 * SS))

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
