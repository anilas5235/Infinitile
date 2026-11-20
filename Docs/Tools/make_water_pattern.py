"""Generator of Assets/Engine/Resources/Artwork/KenneyDerived/water_ripples.png, the liquid pattern of Water (DEC-119,
owner's pick "A pixel ripples" 2026-11-20): a greyscale 16 x 16 tileable noise with horizontal streaks, six grey levels
between 0.62 and 1.0, scaled x8 (nearest) to the 128 x 128 Kenney tile size; alpha 0.8. The liquid shader tints it with
the biome water colour and scrolls two copies of it in world space. Needs Pillow and numpy.
Usage: python Docs/Tools/make_water_pattern.py <out png>"""
import sys
import numpy as np
from PIL import Image

OUT = sys.argv[1]
rng = np.random.default_rng(7)


def wrap_blur(a, kx, ky):
    out = np.zeros_like(a)
    for dx in range(-kx, kx + 1):
        for dy in range(-ky, ky + 1):
            out += np.roll(np.roll(a, dx, 1), dy, 0)
    return out / ((2 * kx + 1) * (2 * ky + 1))


def norm(a, lo, hi):
    a = (a - a.min()) / (a.max() - a.min())
    return lo + a * (hi - lo)


a = rng.random((16, 16))
a = wrap_blur(a, 2, 0) * 0.75 + wrap_blur(a, 0, 1) * 0.25  # streaks along x, a little along y; wraps, so it tiles
a = np.round(norm(a, 0, 1) * 5) / 5                         # six grey levels
g = np.kron(norm(a, 0.62, 1.0), np.ones((8, 8)))            # 16 px per block, nearest x8
rgba = (np.stack([g, g, g, np.full_like(g, 0.8)], -1) * 255).round().astype(np.uint8)
Image.fromarray(rgba, 'RGBA').save(OUT)
print(OUT, rgba.shape)
