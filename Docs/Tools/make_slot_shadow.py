"""Inner-shadow textures of the HUD's recessed pockets (style A, Recessed; owner's picks 2026-11-17, DEC-113).
A pocket's padding box (a rounded rect) gets the shadow of a CSS `inset 0 3px 7px rgba(0,0,0,.70)`: the area outside
the box shifted 3 px down, blurred, kept inside the box. Drawn at 2x and cut to a 9-slice texture (-unity-slice-scale
0.5); its centre is clear, so the pocket's background colour shows through. Needs Pillow and numpy.
  slot  : the block slots, padding box 72 x 72 UI px (76 px slot, 2 px border), radius 8; 128 x 128, 32 px slices.
  track : the scrollbar track, padding box 16 x 64 UI px (20 px track, 2 px border), radius 8; 32 x 128,
          slices 15 left and right, 32 top and bottom.
Usage: python Docs/Tools/make_slot_shadow.py <out png> [slot|track]"""
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = sys.argv[1]
KIND = sys.argv[2] if len(sys.argv) > 2 else 'slot'
S = 2                    # pixels per UI px
OFFSET_Y = 3 * S
BLUR = 3.5 * S           # CSS blur 7 px = Gaussian sigma 3.5 px
ALPHA = 0.70             # the mockup had .55, which hardly showed on the dark in-game fill
MARGIN = 48              # room for the blur around the box
SS = 4                   # supersampling of the rounded edges
# box width, box height, radius (UI px); slice x, slice y, texture width, texture height (texture px)
KINDS = {'slot': (72, 72, 8, 32, 32, 128, 128), 'track': (16, 64, 8, 15, 32, 32, 128)}
BOX_W, BOX_H, RADIUS, SLICE_X, SLICE_Y, SIZE_X, SIZE_Y = KINDS[KIND]
BOX_W, BOX_H, RADIUS = BOX_W * S, BOX_H * S, RADIUS * S


def rounded_mask(width, height, left, top, radius):
    """Anti-aliased coverage (0..1) of a rounded rect on a width x height canvas."""
    im = Image.new('L', (width * SS, height * SS), 0)
    ImageDraw.Draw(im).rounded_rectangle(
        [left * SS, top * SS, (left + BOX_W) * SS - 1, (top + BOX_H) * SS - 1], radius * SS, fill=255)
    return np.asarray(im.resize((width, height), Image.LANCZOS), dtype=np.float64) / 255.0


def cut(length, box, size, slice_):
    """Source rows (or columns) of the 9-slice: the ends as drawn, the middle from the box centre (flat there)."""
    mid = size - 2 * slice_
    c0 = (box - mid) // 2
    return list(range(slice_)) + list(range(c0, c0 + mid)) + list(range(box - slice_, box))


nw, nh = BOX_W + 2 * MARGIN, BOX_H + 2 * MARGIN
box = rounded_mask(nw, nh, MARGIN, MARGIN, RADIUS)
shifted = rounded_mask(nw, nh, MARGIN, MARGIN + OFFSET_Y, RADIUS)
outside = Image.fromarray(np.uint8(np.round((1.0 - shifted) * 255)), 'L')
blurred = np.asarray(outside.filter(ImageFilter.GaussianBlur(BLUR)), dtype=np.float64) / 255.0
alpha = np.clip(ALPHA * blurred * box, 0, 1)[MARGIN:MARGIN + BOX_H, MARGIN:MARGIN + BOX_W]

tex = alpha[np.ix_(cut(BOX_H, BOX_H, SIZE_Y, SLICE_Y), cut(BOX_W, BOX_W, SIZE_X, SLICE_X))]
rgba = np.zeros((SIZE_Y, SIZE_X, 4), np.uint8)
rgba[..., 3] = np.uint8(np.round(tex * 255))
Image.fromarray(rgba, 'RGBA').save(OUT)
print(KIND, 'shadow ->', OUT, 'max alpha', rgba[..., 3].max(), 'centre alpha', rgba[SIZE_Y // 2, SIZE_X // 2, 3])
