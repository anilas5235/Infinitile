"""Generator of the biome-tinted grass and plant tiles (DEC-122) in Assets/Engine/Resources/Artwork/KenneyDerived:
greyscale versions of Kenney Voxel Pack tiles (CC0) whose tinted pixels have a linear mean of exactly TINT_GREY_MEAN, so
the shader's biome colour (Biome.GrassColor / FoliageColor) is the mean colour the tinted part shows before light.
Side tiles keep their dirt or stone and get an overlay tile (Voxel.biomeTintMask): alpha 255 where the side is tinted,
its grey there, so the shader reads the alpha as the mask and a block icon draws the overlay tinted over the side.
Needs Pillow.
Usage: python Docs/Tools/make_tint_tiles.py <project root> <out dir> [--preview <palette.json>]
Writes the tiles to <out dir>; copy them into KenneyDerived. With --preview it also writes _palette.png: one row per
biome, today's tiles next to the grey tiles tinted with the palette's colours."""
import sys, os, colorsys, json
from PIL import Image, ImageDraw

# Linear-light mean of the tinted pixels of every tint tile; VoxelShaderCommon.hlsl TINT_GREY_MEAN must match.
TINT_GREY_MEAN = 0.70

GREEN = lambda h, s, v: 90 <= h <= 175 and s > .25
ANY = lambda h, s, v: True


def lin(c):
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def srgb(l):
    l = max(0.0, min(1.0, l))
    return round(255 * (12.92 * l if l <= 0.0031308 else 1.055 * l ** (1 / 2.4) - 0.055))


def luminance(r, g, b):
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)


def grey(im, mask):
    """The tile with every masked opaque pixel turned grey (relative luminance kept, mean scaled to TINT_GREY_MEAN)
    and its overlay: the grey pixels with alpha 255, every other pixel the mean grey with alpha 0."""
    px = im.load(); sel = []
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a == 0: continue
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if mask(h * 360, s, v): sel.append((x, y))
    mean = sum(luminance(*px[x, y][:3]) for x, y in sel) / len(sel)
    out = im.copy(); opx = out.load()
    g = srgb(TINT_GREY_MEAN)
    m = Image.new('RGBA', im.size, (g, g, g, 0)); mpx = m.load()
    for x, y in sel:
        r, g, b, a = px[x, y]
        v = srgb(luminance(r, g, b) / mean * TINT_GREY_MEAN)
        opx[x, y] = (v, v, v, a)
        mpx[x, y] = (v, v, v, 255)
    return out, m


def tiles(root):
    k = os.path.join(root, 'Assets/Engine/Resources/Artwork/kenney_voxel-pack/PNG/Tiles')
    load = lambda n: Image.open(os.path.join(k, n + '.png')).convert('RGBA')
    t = {}
    t['grass_top_tint'], _ = grey(load('grass_top'), ANY)  # Grass and StoneGrassy tops
    t['dirt_grass_tint'], t['dirt_grass_tint_overlay'] = grey(load('dirt_grass'), GREEN)  # Grass sides
    t['stone_grass_tint'], t['stone_grass_tint_overlay'] = grey(load('stone_grass'), GREEN)  # StoneGrassy sides
    t['grass1_tint'], _ = grey(load('grass1'), ANY)  # GrassFSwamp
    t['grass2_tint'], _ = grey(load('grass2'), ANY)  # Fern
    t['grass3_tint'], _ = grey(load('grass3'), ANY)  # GrassFTall
    t['grass4_tint'], _ = grey(load('grass4'), ANY)  # GrassF, Reeds
    t['grass_tan_tint'], _ = grey(load('grass_tan'), ANY)  # GrassFDry
    return t


def tint(im, mask, colour):
    """What the shader shows (before light): tinted pixels × colour / TINT_GREY_MEAN in linear light."""
    out = im.copy(); px = out.load(); mpx = mask.load() if mask else None
    cl = [lin(c) for c in colour]
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a == 0 or (mpx and mpx[x, y][3] < 128): continue
            px[x, y] = tuple(srgb(lin(c) * cl[i] / TINT_GREY_MEAN) for i, c in enumerate((r, g, b))) + (a,)
    return out


def preview(root, t, palette, path):
    k = os.path.join(root, 'Assets/Engine/Resources/Artwork')
    load = lambda n: Image.open(os.path.join(k, n + '.png')).convert('RGBA')
    hexc = lambda s: tuple(int(s[i:i + 2], 16) for i in (1, 3, 5))
    cell, pad, label = 64, 6, 120
    rows = palette['biomes']
    cols = 4 + 1 + 5
    sheet = Image.new('RGBA', (label + cols * (cell + pad) + 60, 30 + len(rows) * (cell + pad)), (58, 62, 70, 255))
    d = ImageDraw.Draw(sheet)
    d.text((label, 8), 'today', fill=(230, 230, 230)); d.text((label + 5 * (cell + pad), 8), 'proposed (grass | plants)', fill=(230, 230, 230))
    for i, b in enumerate(rows):
        y = 30 + i * (cell + pad)
        d.text((6, y + 4), b['name'], fill=(240, 240, 240))
        d.text((6, y + 20), b['grass'], fill=(200, 200, 200)); d.text((6, y + 34), b['plants'], fill=(200, 200, 200))
        today = [load(n) if n else None for n in b['today']]
        g, p = hexc(b['grass']), hexc(b['plants'])
        new = [tint(t['grass_top_tint'], None, g),
               tint(t[b.get('side', 'dirt_grass') + '_tint'], t[b.get('side', 'dirt_grass') + '_tint_overlay'], g),
               tint(t[b['tuft'] + '_tint'], None, p), tint(t['grass3_tint'], None, p), tint(t['grass2_tint'], None, p)]
        for j, im in enumerate(today + [None] + new):
            if im is None: continue
            x = label + j * (cell + pad)
            sheet.alpha_composite(im.resize((cell, cell), Image.NEAREST), (x, y))
        d.rectangle([label + 4 * (cell + pad) + 8, y + 8, label + 4 * (cell + pad) + 28, y + 28], fill=g)
        d.rectangle([label + 4 * (cell + pad) + 8, y + 36, label + 4 * (cell + pad) + 28, y + 56], fill=p)
    sheet.save(path)


if __name__ == '__main__':
    root, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    t = tiles(root)
    for name, im in t.items():
        # An opaque tile goes out as RGB, so the importer compresses it like the other solid tiles (DXT1)
        if im.getextrema()[3][0] == 255: im = im.convert('RGB')
        im.save(os.path.join(out, name + '.png'))
    if '--preview' in sys.argv:
        with open(sys.argv[sys.argv.index('--preview') + 1]) as f: palette = json.load(f)
        preview(root, t, palette, os.path.join(out, '_palette.png'))
