"""Inner-shadow texture of the HUD block slots (style A, Recessed; owner's pick 2026-11-17, DEC-113).
The slot's padding box (a rounded rect, radius 8 UI px) gets the shadow of a CSS `inset 0 3px 7px rgba(0,0,0,.70)`:
the area outside the box shifted 3 px down, blurred, kept inside the box. Drawn at 2x and cut to a 9-slice texture,
128 x 128 with 32 px slices (16 UI px at -unity-slice-scale 0.5); its centre is clear, so the slot's background colour
shows through. Needs Pillow and numpy.
Usage: python Docs/Tools/make_slot_shadow.py <out png>"""
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = sys.argv[1]
S = 2                    # pixels per UI px
BOX = 72 * S             # padding box of a 76 px slot with a 2 px border
RADIUS = 8 * S
OFFSET_Y = 3 * S
BLUR = 3.5 * S           # CSS blur 7 px = Gaussian sigma 3.5 px
ALPHA = 0.70             # the mockup had .55, which hardly showed on the dark in-game fill
MARGIN = 48              # room for the blur around the box
SS = 4                   # supersampling of the rounded edges
SLICE = 32
SIZE = 128


def rounded_mask(width, height, left, top, radius):
    """Anti-aliased coverage (0..1) of a rounded rect on a width x height canvas."""
    im = Image.new('L', (width * SS, height * SS), 0)
    ImageDraw.Draw(im).rounded_rectangle(
        [left * SS, top * SS, (left + BOX) * SS - 1, (top + BOX) * SS - 1], radius * SS, fill=255)
    return np.asarray(im.resize((width, height), Image.LANCZOS), dtype=np.float64) / 255.0


n = BOX + 2 * MARGIN
box = rounded_mask(n, n, MARGIN, MARGIN, RADIUS)
shifted = rounded_mask(n, n, MARGIN, MARGIN + OFFSET_Y, RADIUS)
outside = Image.fromarray(np.uint8(np.round((1.0 - shifted) * 255)), 'L')
blurred = np.asarray(outside.filter(ImageFilter.GaussianBlur(BLUR)), dtype=np.float64) / 255.0
alpha = np.clip(ALPHA * blurred * box, 0, 1)[MARGIN:MARGIN + BOX, MARGIN:MARGIN + BOX]

# 9-slice: the corners and edges as drawn, the middle taken from the box centre (the shadow is flat there)
mid = SIZE - 2 * SLICE
c0 = (BOX - mid) // 2
idx = list(range(SLICE)) + list(range(c0, c0 + mid)) + list(range(BOX - SLICE, BOX))
tex = alpha[np.ix_(idx, idx)]
rgba = np.zeros((SIZE, SIZE, 4), np.uint8)
rgba[..., 3] = np.uint8(np.round(tex * 255))
Image.fromarray(rgba, 'RGBA').save(OUT)
print('slot shadow ->', OUT, 'max alpha', rgba[..., 3].max(), 'centre alpha', rgba[SIZE // 2, SIZE // 2, 3])
