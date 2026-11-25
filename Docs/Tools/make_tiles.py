"""Generator of the tiles in Assets/Engine/Resources/Artwork/KenneyDerived (batches of 2026-11-10): HSV recolours of
Kenney Voxel Pack tiles (CC0) plus moss composites, 128x128. Needs Pillow.
Usage: python Docs/Tools/make_tiles.py <project root> <out dir>
Writes every tile and a contact sheet (_sheet.png) to <out dir>; copy the tiles you want into KenneyDerived. The opaque
leaves_* tiles are generated but not in the project (the leaf voxels use the *_transparent ones)."""
import sys, os, colorsys, random, math
from PIL import Image, ImageDraw, ImageFilter, ImageChops

ROOT, OUT = sys.argv[1], sys.argv[2]
K = os.path.join(ROOT, 'Assets/Engine/Resources/Artwork/kenney_voxel-pack/PNG/Tiles')
os.makedirs(OUT, exist_ok=True)


def load(name):
    return Image.open(os.path.join(K, name + '.png')).convert('RGBA')


def recolour(im, mask, hue=None, sat=1.0, val=1.0, hue_shift=0.0, sat_abs=None):
    """Map every pixel whose HSV passes mask(h°, s, v): hue set (or shifted), saturation and value scaled."""
    out = im.copy(); px = out.load(); cache = {}
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a == 0: continue
            key = (r, g, b)
            if key not in cache:
                h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
                if mask(h * 360, s, v):
                    nh = (hue / 360 if hue is not None else h) + hue_shift / 360
                    nr, ng, nb = colorsys.hsv_to_rgb(nh % 1, min(1, s * sat) if sat_abs is None else sat_abs, min(1, v * val))
                    cache[key] = (round(nr * 255), round(ng * 255), round(nb * 255))
                else:
                    cache[key] = key
            px[x, y] = cache[key] + (a,)
    return out


ALL = lambda h, s, v: True
GREEN = lambda h, s, v: 90 <= h <= 175 and s > .25
WHITE = lambda h, s, v: s < .22 and v > .8

tiles = {}

# --- Grass: the grass tops, sides and green plants are grey tint tiles (DEC-122, Docs/Tools/make_tint_tiles.py); the
# biome colours them. Only the litter and other non-grass recolours stay here. ------------------------------------
# Needle litter (podzol) for the Taiga forest floor.
tiles['litter_top'] = recolour(load('grass_top'), GREEN, 26, .80, .58)
tiles['dirt_litter'] = recolour(load('dirt_grass'), GREEN, 26, .80, .58)

# --- Earth and stone ------------------------------------------------------------------------------------------------
tiles['mud'] = recolour(load('dirt'), ALL, 24, .75, .52)
tiles['sandstone'] = recolour(load('redstone'), ALL, 40, .50, 1.22)
tiles['sandstone_sand'] = None  # placeholder slot, filled below
CLAY = {'orange': (24, 1.05, 1.12), 'yellow': (44, .85, 1.18), 'white': (30, .16, 1.30), 'brown': (18, .85, .62)}
for n, (h, s, v) in CLAY.items():
    tiles[f'clay_{n}'] = recolour(load('redstone'), ALL, h, s, v)
del tiles['sandstone_sand']


SS = 4  # supersampling of the shape layers


def _wrap_dist(n, ax, ay, bx, by):
    dx = abs(ax - bx); dy = abs(ay - by)
    return math.hypot(min(dx, n - dx), min(dy, n - dy))


def _largest_gap(covered):
    """The longest run of False in a cyclic list."""
    if all(covered): return 0
    k = covered.index(False); best = run = 0
    for c in covered[k:] + covered[:k]:
        run = 0 if c else run + 1; best = max(best, run)
    return best


def spread(rnd, n, radii, tries=800):
    """Centres for shapes of the given radii on the wrapping tile, chosen so that the tiled pattern shows no repeat
    grid (owner 2026-11-25: the green details repeated along visible axes): of `tries` random layouts without big
    overlaps, the one whose longest empty lane across the tile - along x, along y and along both diagonals - is
    shortest."""
    best, best_score = None, 1e9
    for _ in range(tries):
        pts = []
        for r in radii:
            for _ in range(200):
                x, y = rnd.uniform(0, n), rnd.uniform(0, n)
                if all(_wrap_dist(n, x, y, px, py) >= (r + pr) * .8 for (px, py), pr in zip(pts, radii)):
                    pts.append((x, y)); break
            else:
                break
        if len(pts) < len(radii): continue
        score = 0
        for proj, ext in ((lambda x, y: x, 1.0), (lambda x, y: y, 1.0), (lambda x, y: x + y, 1.414), (lambda x, y: x - y, 1.414)):
            covered = [False] * n
            for (x, y), r in zip(pts, radii):
                c, e = proj(x, y), r * .7 * ext
                for u in range(int(c - e), int(c + e) + 1): covered[u % n] = True
            score = max(score, _largest_gap(covered))
        if score < best_score: best, best_score = pts, score
    return best


def _mask(n, polys, dx=0.0, dy=0.0):
    """Supersampled mask of the union of the polygons, shifted by (dx, dy) px and drawn at every wrap offset."""
    m = Image.new('L', (n * SS, n * SS), 0); d = ImageDraw.Draw(m)
    for pts in polys:
        for ox in (-n, 0, n):
            for oy in (-n, 0, n):
                d.polygon([((x + ox + dx) * SS, (y + oy + dy) * SS) for x, y in pts], fill=255)
    return m


def flat_shapes(base, shapes, light=None, shadow=None):
    """Kenney style: flat shapes drawn in order over the base, each a union of polygons in one colour. light = (f, h):
    a top band h px high in the shape colour times f (the Kenney brick and gravel bevel); shadow = (f, dx, dy): the
    ground under the shape, shifted down-right, times f (the Kenney gravel shadow). Seamless: every shape wraps."""
    n = base.width
    img = base.convert('RGBA').resize((n * SS, n * SS), Image.NEAREST)
    for polys, col in shapes:
        m = _mask(n, polys)
        if shadow:
            f, sx, sy = shadow
            dark = img.point(lambda v, f=f: int(v * f)); dark.putalpha(255)
            img = Image.composite(dark, img, ImageChops.subtract(_mask(n, polys, sx, sy), m))
        if light:
            f, h = light
            img.paste(tuple(min(255, int(c * f)) for c in col[:3]) + (255,), mask=m)
            m = ImageChops.darker(m, _mask(n, polys, 0, h))
        img.paste(col, mask=m)
    return img.resize((n, n), Image.BOX)



def _soft_mask(n, polys, dx=0.0, dy=0.0):
    """Antialiased 0..1 mask (numpy) of the wrapping polygons."""
    import numpy as np
    return np.asarray(_mask(n, polys, dx, dy).resize((n, n), Image.BOX)).astype(float) / 255


def shade(img, m, f):
    """Multiply the tile by f where the mask m is set, keeping its texture (the Kenney squares stay visible)."""
    import numpy as np
    a = np.asarray(img.convert('RGBA')).astype(float)
    a[..., :3] = np.clip(a[..., :3] * (1 + (f - 1) * m[..., None]), 0, 255)
    return Image.fromarray(a.astype(np.uint8), 'RGBA')


def speckle(base, seed, count, colours, rmin, rmax, sides=(4, 6), even=False):
    """Base tile with small flat polygons (pebbles, grains) in the given colours; seamless (patches wrap). With `even`
    the centres come from spread(), so the flecks do not line up in rows when the tile repeats."""
    n = base.width; ss = 4
    layer = Image.new('RGBA', (n * ss, n * ss), (0, 0, 0, 0)); d = ImageDraw.Draw(layer)
    rnd = random.Random(seed)
    radii = [rnd.uniform(rmin, rmax) for _ in range(count)] if even else None
    centres = spread(rnd, n, radii) if even else None
    for i in range(count):
        if even: (cx, cy), r = centres[i], radii[i]
        else: cx, cy = rnd.uniform(0, n), rnd.uniform(0, n); r = rnd.uniform(rmin, rmax)
        k = rnd.randint(*sides); a0 = rnd.uniform(0, math.tau)
        pts = [(cx + r * rnd.uniform(.75, 1.1) * math.cos(a0 + j * math.tau / k),
                cy + r * rnd.uniform(.75, 1.1) * math.sin(a0 + j * math.tau / k)) for j in range(k)]
        col = colours[i % len(colours)]
        for ox in (-n, 0, n):
            for oy in (-n, 0, n):
                d.polygon([((x + ox) * ss, (y + oy) * ss) for x, y in pts], fill=col)
    layer = layer.resize((n, n), Image.LANCZOS)
    return Image.alpha_composite(base, layer)


def veins(base, seed, count, colour, width):
    """Base tile with thin wandering veins (marble); seamless (veins wrap)."""
    n = base.width; ss = 4
    layer = Image.new('RGBA', (n * ss, n * ss), (0, 0, 0, 0)); d = ImageDraw.Draw(layer)
    rnd = random.Random(seed)
    for i in range(count):
        x, y = rnd.uniform(0, n), rnd.uniform(0, n); a = rnd.uniform(0, math.tau); pts = [(x, y)]
        for j in range(rnd.randint(8, 14)):
            a += rnd.uniform(-.6, .6); x += 7 * math.cos(a); y += 7 * math.sin(a); pts.append((x, y))
        for ox in (-n, 0, n):
            for oy in (-n, 0, n):
                d.line([((px + ox) * ss, (py + oy) * ss) for px, py in pts], fill=colour, width=width * ss)
    layer = layer.resize((n, n), Image.LANCZOS)
    return Image.alpha_composite(base, layer)


def tone(im, f, alpha=255):
    """The tile's mean colour scaled by f: a Kenney-style shape colour a few percent off the base."""
    r, g, b = [sum(c) / len(c) for c in zip(*[im.getpixel((x, y))[:3] for y in range(im.height) for x in range(im.width)])]
    return (min(255, int(r * f)), min(255, int(g * f)), min(255, int(b * f)), alpha)


def blots(base, seed, count, factors, rmin, rmax, sides=(4, 4)):
    """Kenney style: a few large flat shapes a few percent lighter or darker than the base (speckle with tones)."""
    return speckle(base, seed, count, [tone(base, f) for f in factors], rmin, rmax, sides)


# --- Ground and rock variety (batch 2026-11-24, WorldGen v2 V4, DEC-190) --------------------------------------------
# Simplified 2026-11-25: the first batch (many small grains in black, white and orange, thin veins) was far more
# detailed than the Kenney tiles. Owner rule: no detail smaller than in the Kenney tiles, whose smallest squares are
# about 6 px across (radius 4.5). Every tile is a recoloured Kenney tile plus flat shapes of Kenney size; moss and
# leaves are at least twice that.
# Round 7 (owner 2026-11-25: moss and litter too detailed and not organic, coarse dirt too detailed): every one is a
# recoloured Kenney tile plus a few flat shapes of the size of that tile's own big details. Round 8 (owner: blend
# with the neighbouring dirt and grass): fewer shapes, and all of them stay inside the tile, so a block's edge is plain
# base colour and runs into the dirt or grass next to it.


def inside(rnd, n, radii, pad=5, tries=300):
    """Centres for shapes of the given radii that stay `pad` px inside the tile edges (no shape crosses the border);
    of `tries` random layouts without overlaps, the one whose closest pair of shapes is furthest apart."""
    best, best_gap = None, -1e9
    for _ in range(tries):
        pts = []
        for r in radii:
            for _ in range(100):
                x, y = rnd.uniform(r + pad, n - r - pad), rnd.uniform(r + pad, n - r - pad)
                if all(math.hypot(x - px, y - py) >= r + pr + 6 for (px, py), pr in zip(pts, radii)):
                    pts.append((x, y)); break
            else:
                break
        if len(pts) < len(radii): continue
        gap = min([math.hypot(x1 - x2, y1 - y2) - r1 - r2 for i, ((x1, y1), r1) in enumerate(zip(pts, radii))
                   for (x2, y2), r2 in zip(pts[i + 1:], radii[i + 1:])] or [0])
        if gap > best_gap: best, best_gap = pts, gap
    return best


def _lin(c):
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _srgb(l):
    l = max(0.0, min(1.0, l))
    return round(255 * (12.92 * l if l <= 0.0031308 else 1.055 * l ** (1 / 2.4) - 0.055))


def tint_grey(im, grey_mean=0.70):
    """The tile in grey for a biome tint: relative luminance kept, linear mean scaled to grey_mean (TINT_GREY_MEAN,
    as Docs/Tools/make_tint_tiles.py does), so the shader's biome colour is the mean colour the tile shows."""
    px = im.load(); w, h = im.size
    lum = lambda r, g, b: 0.2126 * _lin(r) + 0.7152 * _lin(g) + 0.0722 * _lin(b)
    lums = [lum(*px[x, y][:3]) for y in range(h) for x in range(w)]
    # the scale that gives grey_mean after the bright texels clip at 1 (a plain mean / grey_mean scale ends low)
    lo, hi = 0.0, 4.0 * grey_mean / (sum(lums) / len(lums))
    for _ in range(40):
        k = (lo + hi) / 2
        if sum(min(1.0, l * k) for l in lums) / len(lums) < grey_mean: lo = k
        else: hi = k
    out = im.copy(); opx = out.load()
    for y in range(h):
        for x in range(w):
            v = _srgb(lums[x + y * w] * k); opx[x, y] = (v, v, v, px[x, y][3])
    return out


def mean_colour(im):
    """The linear-light mean colour of a tile, as sRGB 0..1 (for a biome tint colour)."""
    px = im.load(); w, h = im.size; n = w * h
    return tuple(round(_srgb(sum(_lin(px[x, y][i]) for y in range(h) for x in range(w)) / n) / 255, 3) for i in range(3))


def inner_shapes(base, seed, count, rmin, rmax, colours, sides, jitter=(.8, 1.0)):
    """A few flat polygons of radius rmin..rmax, kept inside the tile edges (inside())."""
    rnd = random.Random(seed); radii = [rnd.uniform(rmin, rmax) for _ in range(count)]; shapes = []
    for i, ((x, y), r) in enumerate(zip(inside(rnd, base.width, radii), radii)):
        k = rnd.randint(*sides); a0 = rnd.uniform(0, math.tau)
        pts = [(x + r * rnd.uniform(*jitter) * math.cos(a0 + j * math.tau / k), y + r * rnd.uniform(*jitter) * math.sin(a0 + j * math.tau / k))
               for j in range(k)]
        shapes.append(([pts], colours[i % len(colours)]))
    return flat_shapes(base, shapes)


# Coarse dirt: the Kenney dirt only a touch darker (so it runs into the dirt next to it), with five flat grey
# pebbles the size of the dirt's big squares.
tiles['coarse_dirt'] = inner_shapes(recolour(load('dirt'), ALL, None, .97, .96), 31, 5, 11, 15,
                                    [(148, 134, 118, 255), (124, 110, 96, 255), (164, 150, 132, 255)], (4, 4), (1.0, 1.0))
# Moss: the Kenney leaves (denser, darker triangles than the grass) in olive, so it does not read as grass, with a few
# moss cushions of pebble size in a lighter and a darker olive; its dirt side with the moss fringe in the same olive
# (fixed colour, no tint).
tiles['moss_top'] = inner_shapes(recolour(load('leaves'), ALL, 74, .66, .78), 37, 5, 11, 15,
                                 [(146, 166, 72, 255), (100, 120, 52, 255)], (6, 8))
tiles['dirt_moss'] = recolour(load('dirt_grass'), GREEN, 74, .66, .66)
# Leaf litter (DEC-206): tinted per biome like grass (Biome.LitterColor, tint layer 3). The tile is the Kenney leaves in
# the Jungle look picked by the owner (L1: dark green with olive, dark and ochre fallen leaves of pebble size), turned grey
# with a linear mean of TINT_GREY_MEAN, so the biome colour is its mean; the side is a copy of the grass side tint tile
# (a texture carries one tint word, so Grass and LitterLeaves cannot share it) with the grass overlay as its mask, so
# the fringe takes the litter colour (Litter is the Taiga needle litter, untinted).
_leaf_litter = inner_shapes(recolour(load('leaves'), ALL, 100, .62, .52), 43, 5, 11, 15,
                            [(118, 140, 52, 255), (88, 104, 40, 255), (140, 128, 60, 255)], (3, 5), (.75, 1.0))
tiles['leaf_litter_top_tint'] = tint_grey(_leaf_litter)
tiles['dirt_litter_tint'] = Image.open(os.path.join(ROOT, 'Assets/Engine/Resources/Artwork/KenneyDerived/dirt_grass_tint.png')).convert('RGBA')
print('Jungle LitterColor (mean of the L1 tile):', mean_colour(_leaf_litter))
# Rock types: granite pale pink-grey (tour round 7: a saturated red read as Badlands terracotta), diorite light grey,
# andesite mid grey, each with Kenney-size squares in clear lighter and darker tones (the ±12 % round was too plain);
# marble see below. Round 9 (owner 2026-11-25: they must work together with std:Stone):
# all four are the Kenney stone tile - faintly warm (granite, with pink flecks), and in its own cool hue lighter
# (diorite) or darker (andesite) - with about a dozen evenly spread flecks instead of 22 random ones (those lined up in rows).
_granite = recolour(load('stone'), ALL, 16, sat_abs=.07, val=1.0)
tiles['granite'] = speckle(_granite, 44, 12, [(196, 138, 124, 255), tone(_granite, .74), tone(_granite, 1.18)], 4.5, 9, (4, 4), True)
_diorite = recolour(load('stone'), ALL, None, .40, 1.15)
tiles['diorite'] = speckle(_diorite, 53, 12, [tone(_diorite, .72), tone(_diorite, .86)], 4.5, 9, (4, 4), True)
_andesite = recolour(load('stone'), ALL, None, .60, .84)
tiles['andesite'] = speckle(_andesite, 59, 12, [tone(_andesite, 1.26), tone(_andesite, .80)], 4.5, 9, (4, 4), True)
# Marble (round 9, owner: the wavy snow bands read as sand, veins rejected): the Kenney stone tile nearly white in the
# stone's own hue, with six soft grey patches a few percent darker so it is a little more than plain white stone.
# Fewer flecks than the stone (owner): the stone's lighter squares take the base colour, its darker ones keep about
# half their contrast.
_marble = recolour(recolour(recolour(load('stone'), lambda h, s, v: v > .665, 184, sat_abs=.035, val=1.306),
                            lambda h, s, v: v < .63, 184, sat_abs=.035, val=1.415),
                   lambda h, s, v: .63 <= v <= .665, 184, sat_abs=.035, val=1.36)
tiles['marble'] = speckle(_marble, 71, 6, [tone(_marble, .93), tone(_marble, .95)], 12, 18, (5, 7), True)

# Clay: soft blue-grey clay for swamp, lake and sea floors (the Clay* colours are the Badlands terracottas).
tiles['clay_grey'] = recolour(load('dirt'), ALL, 208, sat_abs=.14, val=1.02)
# Peat: dark brown-black swamp ground (the recoloured Kenney dirt, no streaks).
tiles['peat'] = recolour(load('dirt'), ALL, 18, .55, .30)


COBBLE_K = 48  # outline vertices of a cobble


def _cobble_profile(rnd, r):
    """Radius per outline direction of a fresh cobble: a rounded square (superellipse) squashed and turned, with a
    slight wobble."""
    asp = rnd.uniform(.75, 1.0); ang = rnd.uniform(0, math.pi); pw = rnd.uniform(2.4, 3.2)
    w2, p2, w3, p3 = rnd.uniform(0, .06), rnd.uniform(0, math.tau), rnd.uniform(0, .05), rnd.uniform(0, math.tau)
    out = []
    for j in range(COBBLE_K):
        t = j * math.tau / COBBLE_K; c, s_ = math.cos(t - ang), math.sin(t - ang)
        out.append(r * (abs(c) ** pw + abs(s_ / asp) ** pw) ** (-1 / pw) * (1 + w2 * math.sin(2 * t + p2) + w3 * math.sin(3 * t + p3)))
    return out


def _cobble_outline(cx, cy, radii, grow=0.0):
    return [(cx + (r + grow) * math.cos(j * math.tau / COBBLE_K), cy + (r + grow) * math.sin(j * math.tau / COBBLE_K))
            for j, r in enumerate(radii)]


def cobble(seed, sizes, fill, gap=5, cap=1.25, smooth=.5, tries=300, steps=40, joint=.82, tones=(.96, 1.0, 1.03),
           light=(1.05, 3)):
    """Kenney-style cobblestone (redesigned 2026-11-25, owner: no Voronoi; like the newer Minecraft cobblestone):
    rounded stones of clearly different sizes. The `sizes` are placed one by one, biggest first, each at the free
    spot that touches its neighbours most, at least `gap` px apart; stones of radius `fill` then go into the holes
    until none fits. Last, every stone grows outline point by outline point towards its neighbours, at most `cap`
    times its first radius and smoothed each step, so the joints close to about `gap` px but the stones stay round;
    drawn on the Kenney stone tile (round 7, owner: more like the stone): the joints darken it by `joint`, every stone
    shades it by one of `tones` and gets a light top band (`light` = factor, px), so the stone's own squares stay
    visible. Seamless: every stone and test wraps."""
    import numpy as np
    n = 128; K = COBBLE_K; rnd = random.Random(seed); stones = []; occ = np.zeros((n, n), bool)

    def raster(pts):
        m = Image.new('L', (n, n), 0); d = ImageDraw.Draw(m)
        for ox in (-n, 0, n):
            for oy in (-n, 0, n):
                d.polygon([(x + ox, y + oy) for x, y in pts], fill=1)
        return np.asarray(m).astype(bool)

    def place(r):
        nonlocal occ
        best, best_touch = None, -1
        for _ in range(tries):
            cx, cy, radii = rnd.uniform(0, n), rnd.uniform(0, n), _cobble_profile(rnd, r)
            if (raster(_cobble_outline(cx, cy, radii, gap)) & occ).any(): continue
            touch = (raster(_cobble_outline(cx, cy, radii, gap + 4)) & occ).sum()
            if touch > best_touch: best, best_touch = (cx, cy, np.array(radii)), touch
        if best is None: return False
        stones.append(best); occ |= raster(_cobble_outline(*best)); return True

    for r in sizes: place(r)
    while place(rnd.uniform(*fill)): pass
    first = [st[2].copy() for st in stones]
    disc = [(dx, dy) for dy in range(-gap, gap + 1) for dx in range(-gap, gap + 1) if dx * dx + dy * dy <= gap * gap]
    for _ in range(steps):
        masks = [raster(_cobble_outline(*st)) for st in stones]; grown = False
        for i, (cx, cy, radii) in enumerate(stones):
            others = np.zeros((n, n), bool)
            for j, m in enumerate(masks):
                if j != i: others |= m
            block = others.copy()
            for dx, dy in disc: block |= np.roll(np.roll(others, dy, 0), dx, 1)
            free = lambda k, r: not block[int(cy + r * math.sin(k * math.tau / K)) % n, int(cx + r * math.cos(k * math.tau / K)) % n]
            new = radii.copy()
            for k in range(K):
                if radii[k] + .5 <= first[i][k] * cap and free(k, radii[k] + .5): new[k] = radii[k] + .5; grown = True
            new = (1 - smooth) * new + smooth / 2 * (np.roll(new, 1) + np.roll(new, -1))
            for k in range(K):
                if not free(k, new[k]): new[k] = min(new[k], radii[k])
            stones[i] = (cx, cy, new)
        if not grown: break
    rnd_tone = random.Random(seed); outlines = [[_cobble_outline(cx, cy, radii)] for cx, cy, radii in stones]
    img = shade(load('stone'), 1 - np.clip(sum(_soft_mask(n, o) for o in outlines), 0, 1), joint)
    for o in outlines:
        m = _soft_mask(n, o)
        img = shade(img, m, rnd_tone.choice(tones))
        img = shade(img, np.clip(m - _soft_mask(n, o, 0, light[1]), 0, 1), light[0])
    return img


# Round 7 (owner: should look more like the stone): five big stones, then radius 11-14 ones in the holes.
tiles['cobblestone'] = cobble(81, (27, 24, 20, 17, 15), (11, 14))

# --- Leaves (opaque and cutout) and trunks -------------------------------------------------------------------------
LEAVES = {'jungle': (112, 1.10, .84), 'acacia': (74, .82, .92), 'swamp': (96, .66, .62), 'birch': (92, .80, 1.06)}
for n, (h, s, v) in LEAVES.items():
    tiles[f'leaves_{n}'] = recolour(load('leaves'), GREEN, h, s, v)
    tiles[f'leaves_{n}_transparent'] = recolour(load('leaves_transparent'), GREEN, h, s, v)
tiles['trunk_jungle_side'] = recolour(load('trunk_side'), ALL, 14, 1.10, .80)
tiles['trunk_jungle_top'] = recolour(load('trunk_top'), ALL, 20, 1.05, .92)
tiles['trunk_acacia_side'] = recolour(load('trunk_side'), ALL, 28, .30, .82)
tiles['trunk_acacia_top'] = recolour(load('trunk_top'), ALL, 14, 1.15, .95)

# --- Plants (cutout sprites) ---------------------------------------------------------------------------------------
tiles['flowers_red'] = recolour(load('Flowers'), WHITE, 2, val=.92, sat_abs=.72)
tiles['flowers_blue'] = recolour(load('Flowers'), WHITE, 215, val=.95, sat_abs=.62)
tiles['flowers_yellow'] = recolour(recolour(load('Flowers'), lambda h, s, v: 30 <= h <= 60 and s > .5, 22, 1.0, .85),
                                   WHITE, 50, val=1.0, sat_abs=.70)
# Swamp (round 2): its own bush leaves (yellow moss green, lighter than the willow's leaves_swamp).
tiles['leaves_bush_swamp_transparent'] = recolour(load('leaves_transparent'), GREEN, 72, .92, .90)



def torch():
    """Torch (2026-11-17, third look after the owner's notes): the front view of the Torch shape, one 2/16 stick standing
    in the block centre, 10/16 high; its top texels glow. The shape's quads sample exactly this rectangle (the top face
    the two glowing rows), so the tile is also the flat inventory icon. Flat colours on the 16-texel grid (8 px per
    texel): our plank wood in two tones, a charred band, a tip from the lava palette, pale yellow on top."""
    n = 128; t = 8
    im = Image.new('RGBA', (n, n), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    texel = lambda col, row, rgb: d.rectangle([col * t, row * t, col * t + t - 1, row * t + t - 1], fill=rgb + (255,))
    rows = {  # texel row from the top: (left texel, right texel) of the stick, columns 7 and 8
        6: ((255, 222, 120), (255, 240, 176)),  # pale yellow top
        7: ((255, 149, 19), (255, 222, 120)),   # yellow-orange
        8: ((232, 106, 23), (255, 149, 19)),    # lava orange
        9: ((92, 62, 40), (110, 74, 46)),       # charred band
    }
    for row in range(10, 16): rows[row] = ((170, 116, 61), (196, 134, 71))  # plank wood: dark and light side
    rows[12] = ((170, 116, 61), (208, 142, 74))  # one lighter grain texel
    for row, (left, right) in rows.items():
        texel(7, row, left)
        texel(8, row, right)
    return im


tiles['torch'] = torch()

for name, im in tiles.items():
    im.save(os.path.join(OUT, name + '.png'))

# --- Contact sheet: new tiles on the dark ground the cutouts are seen against ----------------------------------
names = list(tiles); cols = 9; cell = 136; lab = 14
rows = (len(names) + cols - 1) // cols
sheet = Image.new('RGB', (cols * cell, rows * (cell + lab)), (40, 40, 48)); d = ImageDraw.Draw(sheet)
for i, n in enumerate(names):
    im = tiles[n]; x = (i % cols) * cell + 4; y = (i // cols) * (cell + lab) + 2
    sheet.paste(im, (x, y + (128 - im.height)), im)
    d.text((x, y + 129), n[:22], fill=(225, 225, 225))
sheet.save(os.path.join(OUT, '_sheet.png'))
print(len(tiles), 'tiles ->', OUT)
