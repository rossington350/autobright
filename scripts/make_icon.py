"""Generate AutoBright's icon set from one geometry definition (256-unit canvas).

Outputs icon.ico (multi-size, raster via PIL with supersampling), SVGs for the
MAUI appicon/foreground/splash/header logo, and a preview sheet.

Usage (from the repo root; needs Pillow):
    python scripts/make_icon.py . <preview-output-dir>
"""
import math
import sys
from pathlib import Path
from PIL import Image, ImageDraw

REPO = Path(sys.argv[1])
SCRATCH = Path(sys.argv[2])

# Neutral graphite so the icon sits well beside any Windows accent colour
# (the in-app UI follows the user's accent; the icon deliberately doesn't).
TOP = (0x5E, 0x63, 0x6B)      # gradient top-left
BOTTOM = (0x22, 0x25, 0x2A)   # gradient bottom-right
CORNER = 56
C = 128.0
DISC_R = 44.0
RING_W = 13.0
RAY_IN, RAY_OUT, RAY_W = 70.0, 98.0, 15.0
FADED = 0.5                   # alpha of rays on the "dim" (right) half

# Simplified glyph for tiny sizes: bigger disc, no rays.
SMALL_DISC_R, SMALL_RING_W = 62.0, 24.0


def rays():
    """Yield (x1, y1, x2, y2, alpha) for the 8 rays, starting at 12 o'clock."""
    for i in range(8):
        a = math.radians(i * 45 - 90)
        x1, y1 = C + RAY_IN * math.cos(a), C + RAY_IN * math.sin(a)
        x2, y2 = C + RAY_OUT * math.cos(a), C + RAY_OUT * math.sin(a)
        # i: 0 top, 1..3 right side, 4 bottom, 5..7 left side
        alpha = FADED if 1 <= i <= 3 else 1.0
        yield x1, y1, x2, y2, alpha


# ---------------------------------------------------------------- raster

def gradient(size):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(round(TOP[k] + (BOTTOM[k] - TOP[k]) * t) for k in range(3)) + (255,)
    return img


def render(size, small):
    ss = 4
    S = size * ss
    k = S / 256.0
    bg = gradient(S)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=CORNER * k, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(bg, (0, 0), mask)

    glyph = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    white = lambda a: (255, 255, 255, round(255 * a))

    disc_r = SMALL_DISC_R if small else DISC_R
    ring_w = SMALL_RING_W if small else RING_W
    r = disc_r * k
    box = [C * k - r, C * k - r, C * k + r, C * k + r]
    # Left half solid (PIL angles: clockwise from 3 o'clock; 90..270 = via left).
    d.pieslice(box, 90, 270, fill=white(1))
    d.ellipse(box, outline=white(1), width=round(ring_w * k))

    if not small:
        layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        for x1, y1, x2, y2, a in rays():
            ray = Image.new("RGBA", (S, S), (0, 0, 0, 0))
            rd = ImageDraw.Draw(ray)
            w = RAY_W * k
            rd.line([x1 * k, y1 * k, x2 * k, y2 * k], fill=white(1), width=round(w))
            for x, y in ((x1, y1), (x2, y2)):
                rd.ellipse([x * k - w / 2, y * k - w / 2, x * k + w / 2, y * k + w / 2], fill=white(1))
            if a < 1:
                ray.putalpha(ray.getchannel("A").point(lambda v: round(v * a)))
            layer = Image.alpha_composite(layer, ray)
        glyph = Image.alpha_composite(glyph, layer)

    out = Image.alpha_composite(out, glyph)
    return out.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = {s: render(s, small=s <= 24) for s in sizes}
ico = REPO / "src/AutoBright/icon.ico"
frames[256].save(ico, format="ICO", sizes=[(s, s) for s in sizes],
                 append_images=[frames[s] for s in sizes if s != 256])

# Preview sheet: every size on light and dark strips, plus 4x zoom of 16/24/32.
sheet = Image.new("RGBA", (900, 420), (255, 255, 255, 255))
sd = ImageDraw.Draw(sheet)
sd.rectangle([0, 210, 900, 420], fill=(32, 32, 32, 255))
x = 10
for s in sizes:
    sheet.alpha_composite(frames[s], (x, 10))
    sheet.alpha_composite(frames[s], (x, 220))
    x += s + 14
x = 520
for s in (16, 24, 32):
    z = frames[s].resize((s * 4, s * 4), Image.NEAREST)
    sheet.alpha_composite(z, (x, 290))
    x += s * 4 + 16
sheet.save(SCRATCH / "icon_preview.png")

# ---------------------------------------------------------------- SVG

def svg_rays():
    parts = []
    for x1, y1, x2, y2, a in rays():
        op = "" if a == 1 else f' stroke-opacity="{a}"'
        parts.append(f'<line x1="{x1:.2f}" y1="{y1:.2f}" x2="{x2:.2f}" y2="{y2:.2f}"{op} />')
    return "\n    ".join(parts)


glyph_svg = f'''<g fill="none" stroke="#FFFFFF" stroke-linecap="round" stroke-width="{RAY_W}">
    {svg_rays()}
  </g>
  <path d="M{C},{C - DISC_R} A{DISC_R},{DISC_R} 0 0,0 {C},{C + DISC_R} Z" fill="#FFFFFF" />
  <circle cx="{C}" cy="{C}" r="{DISC_R - RING_W / 2}" fill="none" stroke="#FFFFFF" stroke-width="{RING_W}" />'''

grad = f'''<defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#{TOP[0]:02X}{TOP[1]:02X}{TOP[2]:02X}" />
      <stop offset="1" stop-color="#{BOTTOM[0]:02X}{BOTTOM[1]:02X}{BOTTOM[2]:02X}" />
    </linearGradient>
  </defs>'''

header = '<?xml version="1.0" encoding="UTF-8"?>\n'
comment = "<!-- Generated by scripts/make_icon.py; edit the script, not this file. -->\n"

full = (header + comment + f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  {grad}
  <rect width="256" height="256" rx="{CORNER}" fill="url(#bg)" />
  {glyph_svg}
</svg>
''')
bg_only = (header + comment + f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  {grad}
  <rect width="256" height="256" fill="url(#bg)" />
</svg>
''')
# Foreground: glyph scaled into the platform-safe centre (~65%).
fg = (header + comment + f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <g transform="translate(128 128) scale(0.65) translate(-128 -128)">
  {glyph_svg}
  </g>
</svg>
''')
glyph_only = (header + comment + f'''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  {glyph_svg}
</svg>
''')

res = REPO / "src/AutoBright/Resources"
(res / "AppIcon/appicon.svg").write_text(bg_only, encoding="utf-8")
(res / "AppIcon/appiconfg.svg").write_text(fg, encoding="utf-8")
(res / "Splash/splash.svg").write_text(glyph_only, encoding="utf-8")
(res / "Images/logo.svg").write_text(full, encoding="utf-8")
print("ok", ico.stat().st_size, "bytes")
