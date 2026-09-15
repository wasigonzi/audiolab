"""
Generacion procedural de texturas para el modelo de La Guancha (Ponce, PR).

No depende de Pillow: escribe los PNG a mano (zlib + CRC32) y rasteriza con
numpy usando supersampling para obtener bordes suaves.

Uso:
    python3 tools/textures.py [directorio_salida]
"""

import os
import sys
import zlib
import struct

import numpy as np

# --------------------------------------------------------------------------
# Paleta tomada de la foto de referencia (version "celeste" de la bandera)
# --------------------------------------------------------------------------
CYAN = (61, 199, 216)      # triangulo turquesa
RED = (226, 79, 102)       # franjas rojo-coral desvaido por el sol
WHITE = (243, 241, 234)    # blanco hueso
STONE_LIGHT = (214, 199, 169)
STONE_DARK = (163, 145, 117)
MORTAR = (126, 113, 95)


# --------------------------------------------------------------------------
# Escritura de PNG (RGB de 8 bits, sin dependencias externas)
# --------------------------------------------------------------------------
def write_png(path, rgb):
    """Guarda un array HxWx3 uint8 como PNG."""
    rgb = np.ascontiguousarray(rgb.astype(np.uint8))
    h, w, _ = rgb.shape
    # cada scanline va precedida de un byte de filtro (0 = None)
    raw = np.concatenate(
        [np.zeros((h, 1), np.uint8), rgb.reshape(h, w * 3)], axis=1
    ).tobytes()

    def chunk(tag, data):
        body = tag + data
        return (
            struct.pack(">I", len(data))
            + body
            + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
        )

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    with open(path, "wb") as fh:
        fh.write(png)
    return path


# --------------------------------------------------------------------------
# Utilidades de rasterizado
# --------------------------------------------------------------------------
def poly_mask(X, Y, pts):
    """Mascara booleana punto-en-poligono (regla par/impar), vectorizada."""
    inside = np.zeros(X.shape, dtype=bool)
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        if y1 == y2:
            continue
        straddles = (y1 > Y) != (y2 > Y)
        xint = (x2 - x1) * (Y - y1) / (y2 - y1) + x1
        inside ^= straddles & (X < xint)
    return inside


def star_points(cx, cy, r_outer, r_inner, rotation=-np.pi / 2):
    """Vertices de una estrella de 5 puntas (punta hacia arriba por defecto)."""
    pts = []
    for i in range(10):
        r = r_outer if i % 2 == 0 else r_inner
        a = rotation + i * np.pi / 5
        pts.append((cx + r * np.cos(a), cy + r * np.sin(a)))
    return pts


def _value_noise(h, w, cells, seed, octaves=4):
    """Ruido fractal suave en [0,1] por interpolacion bilineal de una rejilla.

    `cells` puede ser un entero o la pareja (filas, columnas), que permite
    estirar el ruido en un eje: asi se consigue la veta de la madera.
    """
    rng = np.random.default_rng(seed)
    total = np.zeros((h, w), np.float64)
    cy, cx = cells if isinstance(cells, (tuple, list)) else (cells, cells)
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        ny = max(2, int(cy * (2 ** o)))
        nx = max(2, int(cx * (2 ** o)))
        grid = rng.random((ny + 1, nx + 1))
        yi = np.linspace(0, ny, h, endpoint=False)
        xi = np.linspace(0, nx, w, endpoint=False)
        y0, x0 = yi.astype(int), xi.astype(int)
        fy, fx = (yi - y0)[:, None], (xi - x0)[None, :]
        # suavizado smoothstep para evitar el aspecto de rejilla
        fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
        g = grid[np.ix_(y0, x0)]
        gx = grid[np.ix_(y0, x0 + 1)]
        gy = grid[np.ix_(y0 + 1, x0)]
        gxy = grid[np.ix_(y0 + 1, x0 + 1)]
        total += amp * ((g * (1 - fx) + gx * fx) * (1 - fy) + (gy * (1 - fx) + gxy * fx) * fy)
        norm += amp
        amp *= 0.5
    return total / norm


# --------------------------------------------------------------------------
# Bandera de Puerto Rico girada 90 grados (izada arriba, franjas verticales)
# --------------------------------------------------------------------------
def _flag_tile(w, h, with_triangle, ss=3):
    """Dibuja un panel de la torre. `with_triangle` anade el triangulo y la estrella."""
    W, H = w * ss, h * ss
    img = np.zeros((H, W, 3), np.float64)

    xs = (np.arange(W) + 0.5) / W          # 0..1 a lo ancho
    ys = (np.arange(H) + 0.5) / H          # 0..1 a lo alto (0 = arriba)
    X, Y = np.meshgrid(xs, ys)

    # cinco franjas verticales: rojo, blanco, rojo, blanco, rojo
    band = np.clip((X * 5).astype(int), 0, 4)
    for i, color in enumerate((RED, WHITE, RED, WHITE, RED)):
        img[band == i] = color

    if with_triangle:
        # Triangulo equilatero con la base en el borde superior. La profundidad
        # se calcula en proporcion real de la cara (4.6 m de ancho / 7.7 m de
        # alto) para que el triangulo siga siendo equilatero en el mundo 3D.
        depth_v = 0.866 * (w / h)                   # fraccion de la altura
        tri = [(-0.002, -0.002), (1.002, -0.002), (0.5, depth_v)]
        m = poly_mask(X, Y, tri)
        img[m] = CYAN

        # estrella blanca en el baricentro del triangulo, con la punta hacia arriba
        cy = depth_v / 3.0
        r_out = 0.19
        star = star_points(0.5, cy, r_out, r_out * 0.382)
        # la estrella se dibuja en espacio normalizado no uniforme: corregimos
        # el aspecto para que no salga aplastada
        aspect = (w / h)
        star = [(sx, cy + (sy - cy) * aspect) for sx, sy in star]
        img[poly_mask(X, Y, star)] = WHITE

    # desgaste: manchas suaves + escurridos verticales de lluvia
    stains = _value_noise(H, W, 3, seed=7, octaves=4)
    streaks = _value_noise(H, W, 2, seed=11, octaves=2)
    streaks = np.repeat(streaks[:1, :], H, axis=0) * 0.5 + streaks * 0.5
    wear = 0.90 + 0.10 * stains - 0.06 * (streaks > 0.62)
    img *= wear[:, :, None]

    # grano fino de la pintura sobre hormigon
    rng = np.random.default_rng(3)
    img += rng.normal(0, 2.2, img.shape)

    # downsample (antialiasing)
    img = img.reshape(h, ss, w, ss, 3).mean(axis=(1, 3))
    return np.clip(img, 0, 255)


def flag_atlas(path, tile_w=688, tile_h=1152):
    """Atlas de 2 paneles: izquierda = bandera completa, derecha = solo franjas.

    Las caras frontal y trasera de la torre usan el panel izquierdo; las
    laterales, el derecho. Asi se reproduce la foto, donde el triangulo con la
    estrella aparece en una cara y las franjas continuan en la contigua.
    """
    left = _flag_tile(tile_w, tile_h, with_triangle=True)
    right = _flag_tile(tile_w, tile_h, with_triangle=False)
    return write_png(path, np.concatenate([left, right], axis=1))


# --------------------------------------------------------------------------
# Mamposteria de piedra para la base
# --------------------------------------------------------------------------
def _worley(h, w, cells, seed):
    """Voronoi sobre rejilla jitterada -> (F1, F2, indice de celda)."""
    rng = np.random.default_rng(seed)
    gx, gy = cells, cells
    pts = rng.random((gy, gx, 2))
    # piedras mas anchas que altas, como la mamposteria de la foto
    pts[:, :, 0] *= 1.0

    ys = np.arange(h) / h * gy
    xs = np.arange(w) / w * gx
    Y, X = np.meshgrid(ys, xs, indexing="ij")
    cy, cx = Y.astype(int), X.astype(int)

    f1 = np.full((h, w), 1e9)
    f2 = np.full((h, w), 1e9)
    idx = np.zeros((h, w), np.int32)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            ny, nx = (cy + dy) % gy, (cx + dx) % gx
            px = (cx + dx) + pts[ny, nx, 0]
            py = (cy + dy) + pts[ny, nx, 1]
            # las juntas horizontales pesan mas: piedras tumbadas
            d = np.sqrt(((X - px) * 0.75) ** 2 + (Y - py) ** 2)
            closer = d < f1
            f2 = np.where(closer, f1, np.minimum(f2, d))
            idx = np.where(closer, ny * gx + nx, idx)
            f1 = np.where(closer, d, f1)
    return f1, f2, idx


def stone_textures(color_path, normal_path, size=1024, cells=13, seed=5):
    f1, f2, idx = _worley(size, size, cells, seed)
    edge = f2 - f1                       # ~0 en las juntas entre piedras

    rng = np.random.default_rng(seed + 1)
    tint = rng.random(cells * cells + 1)

    lo = np.array(STONE_DARK, float)
    hi = np.array(STONE_LIGHT, float)
    t = tint[idx][:, :, None]
    img = lo + (hi - lo) * t

    # grano interno de cada piedra
    grain = _value_noise(size, size, 16, seed=seed + 2, octaves=4)
    img *= (0.86 + 0.28 * grain)[:, :, None]

    # mortero oscuro en las juntas
    joint = np.clip(1.0 - edge / 0.10, 0, 1) ** 1.6
    img = img * (1 - joint[:, :, None]) + np.array(MORTAR, float) * joint[:, :, None]

    # manchas de humedad en la parte baja
    damp = _value_noise(size, size, 3, seed=seed + 3, octaves=3)
    grad = np.linspace(0, 1, size)[:, None] ** 2
    img *= (1.0 - 0.22 * damp * grad)[:, :, None]

    img += rng.normal(0, 3.0, img.shape)
    write_png(color_path, np.clip(img, 0, 255))

    # mapa de normales a partir de la altura (piedras abombadas, juntas hundidas)
    height = np.clip(edge / 0.16, 0, 1) * 0.75 + grain * 0.25
    gy, gx = np.gradient(height.astype(np.float64))
    strength = 9.0
    nx, ny, nz = -gx * strength, gy * strength, np.ones_like(height)
    norm = np.sqrt(nx * nx + ny * ny + nz * nz)
    nrm = np.stack([nx / norm, ny / norm, nz / norm], axis=-1)
    write_png(normal_path, (nrm * 0.5 + 0.5) * 255)
    return color_path, normal_path


# --------------------------------------------------------------------------
# Superficies del entorno: tablado, hormigon y arena
# --------------------------------------------------------------------------
WOOD_WARM = (150, 92, 58)
WOOD_GREY = (131, 106, 88)


def deck_planks(path, size=1024, rows=15, seed=21):
    """Entablado del paseo: tablas horizontales, veta, juntas y topes."""
    rng = np.random.default_rng(seed)
    h = w = size
    ys = np.arange(h)[:, None]
    xs = np.arange(w)[None, :]

    row = (ys * rows) // h                       # indice de tabla
    frac = (ys * rows) / h - row                 # posicion dentro de la tabla

    warm = np.array(WOOD_WARM, float)
    grey = np.array(WOOD_GREY, float)
    # cada tabla se sitúa en algún punto entre la madera cálida y la agrisada
    mix = rng.random(rows + 1)[row] * np.ones_like(xs)
    img = warm[None, None, :] + (grey - warm)[None, None, :] * mix[:, :, None]

    # cada tabla, un poco mas clara o mas oscura
    img *= (0.80 + 0.40 * rng.random(rows + 1)[row])[:, :, None]

    # veta: ruido muy estirado en horizontal
    grain = _value_noise(h, w, (rows * 7, 3), seed=seed + 1, octaves=3)
    img *= (0.84 + 0.30 * grain)[:, :, None]

    # juntas entre tablas y topes a tresbolillo
    joint = np.clip(1.0 - np.minimum(frac, 1 - frac) / 0.045, 0, 1) ** 1.4
    butt_at = (rng.random(rows + 1)[row] * w).astype(int)
    butt = (np.abs(xs - butt_at) < 2).astype(float)
    dark = np.clip(joint + butt, 0, 1)
    img = img * (1 - dark[:, :, None]) + np.array((46, 33, 26), float) * dark[:, :, None]

    img += rng.normal(0, 4.0, img.shape)
    return write_png(path, np.clip(img, 0, 255))


def concrete(path, size=768, seed=31):
    rng = np.random.default_rng(seed)
    base = np.array((176, 172, 163), float)
    blotch = _value_noise(size, size, 5, seed=seed, octaves=4)
    fine = _value_noise(size, size, 40, seed=seed + 1, octaves=3)
    img = base[None, None, :] * (0.80 + 0.26 * blotch + 0.10 * fine)[:, :, None]
    img += rng.normal(0, 4.5, img.shape)
    return write_png(path, np.clip(img, 0, 255))


def sand(path, size=512, seed=41):
    rng = np.random.default_rng(seed)
    base = np.array((222, 205, 172), float)
    ripple = _value_noise(size, size, (6, 26), seed=seed, octaves=3)
    img = base[None, None, :] * (0.88 + 0.18 * ripple)[:, :, None]
    img += rng.normal(0, 5.0, img.shape)
    return write_png(path, np.clip(img, 0, 255))


# --------------------------------------------------------------------------
# Hormigon pintado de clima tropical y asfalto
# --------------------------------------------------------------------------
def weathered_wall(path, size=1024, base=(201, 203, 197), seed=51,
                   streaks=0.55, mildew=0.40):
    """Paramento de hormigon pintado, con el desgaste propio del tropico.

    Lo que define estas fachadas no es el color sino los escurridos: regueros
    verticales de suciedad que bajan desde el pretil y los alfeizares, mas las
    manchas de moho en las zonas que no secan.
    """
    rng = np.random.default_rng(seed)
    img = np.array(base, float)[None, None, :] * np.ones((size, size, 1))

    # veladura general de la pintura
    patchy = _value_noise(size, size, 4, seed=seed, octaves=4)
    img *= (0.90 + 0.16 * patchy)[:, :, None]

    # juntas de encofrado, muy tenues
    grid = np.zeros((size, size))
    for step, weight in ((size // 4, 0.55), (size // 8, 0.25)):
        ys = np.arange(size)[:, None]
        grid = np.maximum(grid, weight * (ys % step < 2))
    img *= (1.0 - 0.06 * grid)[:, :, None]

    # regueros verticales: ruido muy estirado en vertical, entrando desde arriba
    run = _value_noise(size, size, (2, 22), seed=seed + 1, octaves=3)
    fall = np.clip(np.linspace(0.0, 1.0, size) * 2.2, 0, 1)[:, None]
    dirt = np.clip((run - 0.48) * 3.2, 0, 1) * fall * streaks
    img *= (1.0 - 0.34 * dirt)[:, :, None]

    # moho: manchas verdosas en las bandas bajas y los rincones
    moss = _value_noise(size, size, 7, seed=seed + 2, octaves=4)
    moss = np.clip((moss - 0.56) * 3.6, 0, 1) * mildew
    tint = np.array((104, 116, 92), float)
    img = img * (1 - moss[:, :, None]) + tint[None, None, :] * moss[:, :, None]

    img += rng.normal(0, 3.2, img.shape)
    return write_png(path, np.clip(img, 0, 255))


def asphalt(path, size=512, seed=61):
    rng = np.random.default_rng(seed)
    base = np.array((62, 62, 64), float)
    fine = _value_noise(size, size, 60, seed=seed, octaves=3)
    wide = _value_noise(size, size, 5, seed=seed + 1, octaves=3)
    img = base[None, None, :] * (0.72 + 0.46 * fine + 0.20 * wide)[:, :, None]
    # arido visto
    chips = rng.random((size, size)) > 0.986
    img[chips] = np.clip(img[chips] * 1.85, 0, 255)
    img += rng.normal(0, 4.0, img.shape)
    return write_png(path, np.clip(img, 0, 255))


# --------------------------------------------------------------------------
def main(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    made = [
        flag_atlas(os.path.join(out_dir, "pr_flag_atlas.png")),
        *stone_textures(
            os.path.join(out_dir, "stone_base_color.png"),
            os.path.join(out_dir, "stone_base_normal.png"),
        ),
        deck_planks(os.path.join(out_dir, "deck_planks.png")),
        concrete(os.path.join(out_dir, "concrete.png")),
        sand(os.path.join(out_dir, "sand.png")),
        weathered_wall(os.path.join(out_dir, "painted_concrete.png")),
        weathered_wall(os.path.join(out_dir, "painted_teal.png"),
                       base=(64, 108, 124), seed=57, streaks=0.42, mildew=0.24),
        asphalt(os.path.join(out_dir, "asphalt.png")),
    ]
    for p in made:
        print("  ->", p, os.path.getsize(p) // 1024, "KB")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "textures")
