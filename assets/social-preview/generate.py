"""Generate the GitHub social preview card (1280x640).

Regenerate with `python generate.py` (needs Pillow and Segoe UI, so Windows).
Upload the result in Settings -> General -> Social preview. The 40pt safe
area from repository-open-graph-template.png is respected: keep any new
content inside x 80..1200, y 80..560.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent

W, H = 1280, 640
NAVY = (27, 58, 107)
BLUE = (41, 128, 185)
GRAY = (90, 110, 135)
BG_TOP = (255, 255, 255)
BG_BOT = (228, 240, 250)

img = Image.new("RGB", (W, H), BG_TOP)
draw = ImageDraw.Draw(img)

# vertical gradient
for y in range(H):
    t = y / H
    c = tuple(int(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOT))
    draw.line([(0, y), (W, y)], fill=c)

# decorative arcs echoing the logo ring, kept outside the safe area
deco = Image.new("RGBA", (W, H), (0, 0, 0, 0))
ddraw = ImageDraw.Draw(deco)
ddraw.arc([W - 260, -220, W + 260, 300], start=90, end=250, fill=BLUE + (46,), width=42)
ddraw.arc([-240, H - 280, 240, H + 200], start=270, end=70, fill=NAVY + (36,), width=42)
img = Image.alpha_composite(img.convert("RGBA"), deco).convert("RGB")
draw = ImageDraw.Draw(img)

# logo-full: wordmark only (400x73 source, 2x upscale)
logo = Image.open(REPO / "docs/assets/logo-full.png").convert("RGBA")
lw = 800
lh = round(logo.height * lw / logo.width)
logo = logo.resize((lw, lh), Image.LANCZOS)
img.paste(logo, ((W - lw) // 2, 175), logo)


def font(name, size):
    return ImageFont.truetype(f"C:/Windows/Fonts/{name}", size)


def center(text, y, fnt, color):
    tw = draw.textlength(text, font=fnt)
    draw.text(((W - tw) / 2, y), text, font=fnt, fill=color)


center("Persistent, resilient background task execution for .NET",
       420, font("seguisb.ttf", 42), NAVY)

features = ("Fire-and-forget  \u00b7  Delayed  \u00b7  Recurring  \u00b7  Retries  "
            "\u00b7  Rate limiting  \u00b7  Monitoring dashboard")
size = 28
while size > 20 and draw.textlength(features, font=font("segoeui.ttf", size)) > 1060:
    size -= 1
center(features, 498, font("segoeui.ttf", size), GRAY)

out = HERE / "evertask-social-preview.png"
img.save(out)
print("saved", out, img.size)
