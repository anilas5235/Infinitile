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

# --- Grass per climate: top face and dirt side with the grass band -----------------------------------------------
GRASS = {  # name: (hue, sat, val)
    'dry': (52, .72, .90),      # Savanna: yellow-olive
    'jungle': (128, 1.05, .80),  # Jungle: deep green
    'taiga': (138, .80, .56),   # Taiga: dark fir green (round 2: darker and greener than the first 150 / .70 / .66)
    'swamp': (84, .72, .68),    # Swamp: murky olive
}
for n, (h, s, v) in GRASS.items():
    tiles[f'grass_{n}_top'] = recolour(load('grass_top'), GREEN, h, s, v)
    tiles[f'dirt_grass_{n}'] = recolour(load('dirt_grass'), GREEN, h, s, v)
# Needle litter (podzol) for the Taiga forest floor.
tiles['litter_top'] = recolour(load('grass_top'), GREEN, 26, .80, .58)
tiles['dirt_litter'] = recolour(load('dirt_grass'), GREEN, 26, .80, .58)
# Mountain meadow (tour 2, 2026-11-11): Taiga grass on stone, so the step sides of a slope show stone and no dirt.
tiles['stone_grass_taiga'] = recolour(load('stone_grass'), GREEN, *GRASS['taiga'])

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
tiles['fern'] = recolour(load('grass2'), GREEN, 132, 1.0, .72)
tiles['reeds'] = recolour(load('grass4'), GREEN, 88, .75, .70)
tiles['grass_tall_dry'] = recolour(load('grass3'), GREEN, 52, .70, 1.0)
tiles['grass_tall_jungle'] = recolour(load('grass3'), GREEN, 126, 1.05, .80)
# Swamp (round 2): its own bush leaves (yellow moss green, lighter than the willow's leaves_swamp) and a dark sedge tuft.
tiles['leaves_bush_swamp_transparent'] = recolour(load('leaves_transparent'), GREEN, 72, .92, .90)
tiles['grass_swamp'] = recolour(load('grass1'), GREEN, 100, 1.0, .62)

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
