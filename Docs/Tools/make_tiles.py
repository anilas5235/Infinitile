"""Generator of the tiles in Assets/Engine/Resources/Artwork/KenneyDerived (batches of 2026-11-10): HSV recolours of
Kenney Voxel Pack tiles (CC0) plus moss composites, 128x128. Needs Pillow.
Usage: python Docs/Tools/make_tiles.py <project root> <out dir>
Writes every tile and a contact sheet (_sheet.png) to <out dir>; copy the tiles you want into KenneyDerived. The opaque
leaves_* tiles are generated but not in the project (the leaf voxels use the *_transparent ones)."""
import sys, os, colorsys, random, math
from PIL import Image, ImageDraw

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


def moss(base_name, seed, count):
    """Base tile with flat moss patches in two greens; patches wrap, so the tile stays seamless."""
    base = load(base_name); n = base.width; ss = 4
    layer = Image.new('RGBA', (n * ss, n * ss), (0, 0, 0, 0)); d = ImageDraw.Draw(layer)
    rnd = random.Random(seed)
    greens = [(72, 160, 96, 255), (58, 140, 84, 255)]
    for i in range(count):
        cx, cy = rnd.uniform(0, n), rnd.uniform(0, n); r = rnd.uniform(7, 15)
        k = rnd.randint(5, 7); a0 = rnd.uniform(0, math.tau)
        pts = [(cx + r * rnd.uniform(.7, 1.1) * math.cos(a0 + j * math.tau / k),
                cy + r * rnd.uniform(.7, 1.1) * math.sin(a0 + j * math.tau / k)) for j in range(k)]
        col = greens[i % 2]
        for ox in (-n, 0, n):
            for oy in (-n, 0, n):
                d.polygon([((x + ox) * ss, (y + oy) * ss) for x, y in pts], fill=col)
    layer = layer.resize((n, n), Image.LANCZOS)
    return Image.alpha_composite(base, layer)


tiles['stone_moss'] = moss('stone', 7, 9)
tiles['greystone_moss'] = moss('greystone', 11, 9)

def speckle(base, seed, count, colours, rmin, rmax, sides=(4, 6)):
    """Base tile with small flat polygons (pebbles, grains, lichen) in the given colours; seamless (patches wrap)."""
    n = base.width; ss = 4
    layer = Image.new('RGBA', (n * ss, n * ss), (0, 0, 0, 0)); d = ImageDraw.Draw(layer)
    rnd = random.Random(seed)
    for i in range(count):
        cx, cy = rnd.uniform(0, n), rnd.uniform(0, n); r = rnd.uniform(rmin, rmax)
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


# --- Ground and rock variety (batch 2026-11-24, WorldGen v2 V4, DEC-190) --------------------------------------------
# Coarse dirt: darker, greyer dirt with grey and brown pebbles (no gravel look).
tiles['coarse_dirt'] = speckle(recolour(load('dirt'), ALL, 26, .70, .74), 31, 30,
                               [(146, 142, 134, 255), (112, 86, 62, 255), (172, 166, 152, 255)], 3, 6)
# Moss: a yellow-green moss carpet with light tufts (tour round 7: the darker olive read as shadow), and its dirt side with a moss fringe (fixed colour, no biome tint).
tiles['moss_top'] = speckle(recolour(load('grass_top'), GREEN, 90, .78, .76), 37, 34,
                            [(138, 176, 62, 255), (96, 138, 48, 255), (156, 190, 84, 255)], 3, 6)
tiles['dirt_moss'] = recolour(load('dirt_grass'), GREEN, 90, .78, .76)
# Leaf litter: brown ground under red, orange and ochre broadleaf leaves for Forest and Jungle floors (Litter is the
# Taiga needle litter).
tiles['leaf_litter_top'] = speckle(recolour(load('dirt'), ALL, 20, .90, .62), 41, 46,
                                   [(196, 104, 44, 255), (186, 146, 58, 255), (144, 58, 34, 255), (112, 74, 46, 255)],
                                   5, 9, (3, 5))
tiles['dirt_leaf_litter'] = recolour(load('dirt_grass'), GREEN, 20, .90, .62)
# Lichen stone: dark stone with pale sage and rust-orange lichen spots.
tiles['greystone_lichen'] = speckle(load('greystone'), 43, 26,
                                    [(168, 178, 140, 255), (190, 116, 56, 255), (206, 186, 96, 255)], 3, 7)
# Rock types: granite (salmon with dark and light grains), diorite (white-grey with black grains), andesite (mid grey with
# light grains), marble (white with grey veins). Granite stays a pale pink-grey (tour round 7: a saturated red read as
# Badlands terracotta). Grains stay 3-5 px so they survive the mipmaps.
tiles['granite'] = speckle(recolour(load('stone'), ALL, 14, sat_abs=.16, val=1.02), 47, 44,
                           [(62, 58, 58, 255), (236, 228, 222, 255), (176, 128, 118, 255)], 2.5, 4.5)
tiles['diorite'] = speckle(recolour(load('stone'), ALL, 40, sat_abs=.04, val=1.22), 53, 40,
                           [(56, 56, 60, 255), (128, 128, 134, 255)], 2.5, 4.5)
tiles['andesite'] = speckle(recolour(load('stone'), ALL, 30, sat_abs=.05, val=.80), 59, 40,
                            [(156, 154, 150, 255), (84, 82, 80, 255)], 2.5, 4.5)
tiles['marble'] = veins(recolour(load('stone'), ALL, 40, sat_abs=.03, val=1.30), 61, 6, (120, 120, 132, 230), 2)

# Clay: soft blue-grey clay for swamp, lake and sea floors (the Clay* colours are the Badlands terracottas).
tiles['clay_grey'] = recolour(load('dirt'), ALL, 208, sat_abs=.14, val=1.02)
# Peat: dark brown-black swamp ground with fibre streaks.
tiles['peat'] = veins(recolour(load('dirt'), ALL, 18, .55, .30), 67, 10, (104, 78, 52, 200), 1)


def cobble(seed, count):
    """Rounded stones in four greys between grout lines: a wrapping Voronoi pattern, so the tile stays seamless."""
    n = 128; rnd = random.Random(seed)
    seeds = [(rnd.uniform(0, n), rnd.uniform(0, n)) for _ in range(count)]
    greys = [(146, 148, 146), (124, 126, 128), (164, 162, 154), (108, 110, 114)]
    im = Image.new('RGBA', (n, n)); px = im.load()
    for y in range(n):
        for x in range(n):
            ds = []
            for i, (sx, sy) in enumerate(seeds):
                dx = min(abs(x - sx), n - abs(x - sx)); dy = min(abs(y - sy), n - abs(y - sy))
                ds.append((math.hypot(dx, dy), i))
            ds.sort(); (d1, i1), (d2, _) = ds[0], ds[1]
            edge = d2 - d1
            if edge < 2.2: px[x, y] = (72, 74, 78, 255)
            else:
                g = greys[i1 % 4]; k = 1.0 + .10 * max(0, 1 - d1 / 9) - (.12 if edge < 4 else 0)
                px[x, y] = tuple(min(255, int(c * k)) for c in g) + (255,)
    return im


tiles['cobblestone'] = cobble(71, 30)

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
