"""
Entorno del Complejo Recreativo y Cultural La Guancha (Ponce, PR).

Reconstruye el conjunto alrededor del faro: la explanada, el paseo tablado, la
darsena con sus muelles flotantes y lanchas, los 24 kioscos, el anfiteatro, la
playa y el arbolado.

La planta se deduce de la fotografia de referencia; las cantidades (24 kioscos,
marina, anfiteatro, playa) provienen de la documentacion del complejo. No es un
levantamiento topografico: es una reconstruccion a escala verosimil.

Origen en la base del faro. +X al este, +Y tierra adentro, -Y hacia el mar.
"""

import math
import random

import bpy

import vegetacion
from meshlib import MeshBuilder, add_box, add_cylinder, add_frustum, add_pyramid

# --------------------------------------------------------------------------
S = dict(
    water_z=0.0,
    quay_z=1.70,       # explanada de hormigon donde se levanta el faro
    deck_z=1.55,       # entablado del paseo
    float_z=0.55,      # francobordo de los muelles flotantes
    quay=(-17.0, 11.0, -5.0, 17.0),        # x0, x1, y0, y1
    walk_y=-3.2, walk_w=5.2, walk_x=(11.0, 74.0),
    spur_x=(30.0, 35.2), spur_y=(-0.6, 24.0),   # arranca al borde del paseo
    dock_rows=(-19.5, -33.0),
    dock_x=(15.0, 67.0),
    land=(-62.0, 122.0, 4.0, 82.0),        # tierra principal
    mole=(-18.0, 12.0, -6.5, 4.2),         # promontorio del faro
    beach_y=(-66.0, -46.0),                # lengua de playa al otro lado
    kiosks_x=(-36.0, 26.0), kiosks_n=12,   # 12 + 12 = los 24 kioscos
    kiosks_y=(17.5, 29.5),
)

# las cubiertas de los kioscos tampoco son todas iguales
ROOF_COLORS = [
    (0.340, 0.118, 0.086), (0.076, 0.208, 0.206),
    (0.424, 0.328, 0.168), (0.106, 0.128, 0.158),
]

KIOSK_COLORS = [
    (0.85, 0.24, 0.27), (0.96, 0.66, 0.16), (0.16, 0.52, 0.72),
    (0.93, 0.44, 0.18), (0.26, 0.60, 0.36), (0.83, 0.30, 0.52),
    (0.98, 0.82, 0.25), (0.35, 0.40, 0.70),
]


# --------------------------------------------------------------------------
# Materiales del entorno
# --------------------------------------------------------------------------
def build_site_materials(material, tex_path):
    m = dict(
        deck=material("Tablado", (0.5, 0.3, 0.2), 0.78,
                      texture=tex_path("deck_planks.png")),
        rail=material("Baranda_Madera", (0.404, 0.184, 0.118), 0.72),
        concrete=material("Hormigon", (0.6, 0.6, 0.57), 0.86,
                          texture=tex_path("concrete.png")),
        sand=material("Arena", (0.82, 0.74, 0.60), 0.95,
                      texture=tex_path("sand.png")),
        water=material("Agua_Darsena", (0.022, 0.284, 0.322), 0.055),
        lawn=material("Cesped", (0.145, 0.270, 0.125), 0.92),
        asphalt=material("Asfalto", (0.052, 0.052, 0.055), 0.88),
        float_dock=material("Muelle_Flotante", (0.505, 0.492, 0.455), 0.80),
        piling=material("Pilotes", (0.238, 0.180, 0.128), 0.86),
        hull=material("Casco_Lancha", (0.880, 0.886, 0.878), 0.35),
        hull_trim=material("Franja_Lancha", (0.075, 0.232, 0.452), 0.40),
        canopy=material("Toldo", (0.900, 0.906, 0.890), 0.62),
        outboard=material("Fueraborda", (0.055, 0.058, 0.062), 0.42, metallic=0.5),
        trunk=material("Tronco_Palma", (0.296, 0.234, 0.170), 0.88),
        frond=material("Palma", (0.128, 0.352, 0.128), 0.66),
        shrub=material("Arbolado", (0.106, 0.286, 0.122), 0.78),
        wall=material("Muro_Edificio", (0.847, 0.855, 0.545), 0.70),
        wall_alt=material("Muro_Claro", (0.930, 0.912, 0.828), 0.68),
        lamp_post=material("Farola", (0.045, 0.048, 0.050), 0.45, metallic=0.4),
        lamp_glass=material("Luminaria", (1.0, 0.96, 0.85), 0.20,
                            emission=(1.0, 0.93, 0.74), emission_power=1.2),
    )
    for i, c in enumerate(KIOSK_COLORS):
        m[f"kiosk{i}"] = material(f"Kiosco_{i}", c, 0.62)
    for i, c in enumerate(ROOF_COLORS):
        m[f"kroof{i}"] = material(f"Kiosco_Cubierta_{i}", c, 0.68)
    return m


# --------------------------------------------------------------------------
# Agua y terreno
# --------------------------------------------------------------------------
# la lamina de agua es, con diferencia, lo que mas caras aporta: la version
# ligera del visor web la rebaja sin que se note a esa escala
WATER = dict(extent=340.0, divisions=120)


def build_water(mats, extent=None, divisions=None, amp=0.055, seed=9):
    """Lamina de agua con oleaje suave, desplazada vertice a vertice."""
    extent = WATER["extent"] if extent is None else extent
    divisions = WATER["divisions"] if divisions is None else divisions
    rng = random.Random(seed)
    phases = [(rng.uniform(0, math.tau), rng.uniform(0, math.tau)) for _ in range(4)]
    freqs = [(0.09, 0.13), (0.21, 0.07), (0.05, 0.28), (0.42, 0.33)]
    amps = [1.0, 0.55, 0.42, 0.20]

    def z_at(x, y):
        z = 0.0
        for (fx, fy), (px, py), a in zip(freqs, phases, amps):
            z += a * math.sin(x * fx + px) * math.cos(y * fy + py)
        return S["water_z"] + amp * z

    mb = MeshBuilder()
    step = extent * 2 / divisions
    for i in range(divisions):
        for j in range(divisions):
            x0, y0 = -extent + i * step, -extent + j * step
            x1, y1 = x0 + step, y0 + step
            mb.add([(x0, y0, z_at(x0, y0)), (x1, y0, z_at(x1, y0)),
                    (x1, y1, z_at(x1, y1)), (x0, y1, z_at(x0, y1))],
                   [(0, 0), (1, 0), (1, 1), (0, 1)])
    return [mb.to_object("Agua", mats["water"], merge=1e-4, smooth=True)]


def build_ground(mats):
    """Tierra firme: explanada, promontorio del faro, cesped, aparcamiento y playa.

    La darsena no se excava: es el hueco que queda entre la tierra principal
    (al norte) y la lengua de playa (al sur), con el agua asomando entre ambas.
    """
    objs = []
    qz = S["quay_z"]

    quay = MeshBuilder()
    lx0, lx1, ly0, ly1 = S["land"]
    _slab(quay, lx0, lx1, ly0, ly1, qz, depth=3.2, uv=30.0)
    mx0, mx1, my0, my1 = S["mole"]
    _slab(quay, mx0, mx1, my0, my1, qz, depth=3.2, uv=7.0)
    objs.append(quay.to_object("Explanada", mats["concrete"], merge=1e-4))

    lawn = MeshBuilder()
    _slab(lawn, -60.0, -19.0, 5.0, 44.0, qz + 0.05, depth=0.6, uv=12.0)
    _slab(lawn, -19.0, 40.0, 27.0, 46.0, qz + 0.05, depth=0.6, uv=12.0)
    _slab(lawn, 56.0, 100.0, 6.0, 26.0, qz + 0.05, depth=0.6, uv=12.0)
    objs.append(lawn.to_object("Cesped", mats["lawn"], merge=1e-4))

    park = MeshBuilder()
    _slab(park, -40.0, 110.0, 50.0, 80.0, qz + 0.03, depth=0.4, uv=26.0)
    objs.append(park.to_object("Aparcamiento", mats["asphalt"], merge=1e-4))

    by0, by1 = S["beach_y"]
    beach = MeshBuilder()
    _slab(beach, -62.0, 132.0, by0, by1, 0.95, depth=1.9, uv=26.0)
    objs.append(beach.to_object("Playa", mats["sand"], merge=1e-4))
    return objs


def _slab(mb, x0, x1, y0, y1, z, depth=1.0, uv=1.0):
    """Losa con canto: cara superior mas los cuatro paramentos verticales."""
    mb.add([(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)],
           [(0, 0), (uv, 0), (uv, uv), (0, uv)])
    zb = z - depth
    edges = [((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)),
             ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))]
    for (ax, ay), (bx, by) in edges:
        mb.add([(ax, ay, zb), (bx, by, zb), (bx, by, z), (ax, ay, z)],
               [(0, 0), (uv, 0), (uv, 1), (0, 1)])


# --------------------------------------------------------------------------
# Paseo tablado
# --------------------------------------------------------------------------
def build_boardwalk(mats):
    """Entablado sobre pilotes, con barandas de listones y farolas."""
    deck, rail, piles = MeshBuilder(), MeshBuilder(), MeshBuilder()
    dz = S["deck_z"]
    wx0, wx1 = S["walk_x"]
    wy, ww = S["walk_y"], S["walk_w"]

    # tramo principal este-oeste
    _slab(deck, wx0, wx1, wy - ww / 2, wy + ww / 2, dz, depth=0.42, uv=24.0)
    _railing(rail, [(wx0, wy - ww / 2), (wx1, wy - ww / 2)], dz)
    _railing(rail, [(wx0, wy + ww / 2), (34.0, wy + ww / 2)], dz)
    _railing(rail, [(38.0, wy + ww / 2), (wx1, wy + ww / 2)], dz)

    # ramal hacia los kioscos
    sx0, sx1 = S["spur_x"]
    sy0, sy1 = S["spur_y"]
    _slab(deck, sx0, sx1, sy0, sy1, dz, depth=0.42, uv=10.0)
    _railing(rail, [(sx0, sy0), (sx0, sy1)], dz)
    _railing(rail, [(sx1, sy0), (sx1, sy1)], dz)

    for x in _frange(wx0 + 1.5, wx1, 4.5):
        for y in (wy - ww / 2 + 0.5, wy + ww / 2 - 0.5):
            add_cylinder(piles, (x, y, -1.8), 0.20, 0.17, dz + 1.4, segments=8)
    for y in _frange(sy0 + 3.0, sy1, 4.5):
        for x in (sx0 + 0.5, sx1 - 0.5):
            add_cylinder(piles, (x, y, -1.8), 0.20, 0.17, dz + 1.4, segments=8)

    objs = [deck.to_object("Tablado", mats["deck"], merge=1e-4),
            rail.to_object("Barandas_Tablado", mats["rail"], merge=1e-4),
            piles.to_object("Pilotes_Tablado", mats["piling"], merge=1e-4)]
    objs.extend(_lamp_posts(mats, dz))
    return objs


def _railing(mb, path, z, height=1.08, slats=5):
    """Baranda de listones horizontales, como la del paseo."""
    for (ax, ay), (bx, by) in zip(path, path[1:]):
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        if length < 0.1:
            continue
        ux, uy = dx / length, dy / length
        px, py = -uy, ux                      # normal en planta
        cx, cy = (ax + bx) / 2, (ay + by) / 2
        sx = abs(ux) * length + abs(px) * 0.07
        sy = abs(uy) * length + abs(py) * 0.07

        for k in range(slats):                # listones horizontales
            zz = z + 0.22 + k * (height - 0.30) / (slats - 1)
            add_box(mb, (cx, cy, zz), (sx, sy, 0.085))
        add_box(mb, (cx, cy, z + height), (sx + 0.06, sy + 0.06, 0.10))  # pasamanos

        n = max(2, int(length / 2.4) + 1)     # montantes
        for k in range(n + 1):
            t = k / n
            add_box(mb, (ax + dx * t, ay + dy * t, z + height / 2),
                    (0.13, 0.13, height))


def _lamp_posts(mats, dz):
    post, glass = MeshBuilder(), MeshBuilder()
    wx0, wx1 = S["walk_x"]
    wy, ww = S["walk_y"], S["walk_w"]
    spots = [(x, wy + ww / 2 - 0.45) for x in _frange(wx0 + 6.0, wx1, 13.0)]
    spots += [(S["spur_x"][0] + 0.5, y) for y in _frange(4.0, S["spur_y"][1], 13.0)]
    for x, y in spots:
        add_cylinder(post, (x, y, dz), 0.11, 0.08, 3.6, segments=10)
        add_box(post, (x, y, dz + 3.72), (0.36, 0.36, 0.14))
        add_frustum(glass, 0.30, 0.20, 0.28, dz + 3.80, cap_top=True,
                    center=(x, y))
    return [post.to_object("Farolas", mats["lamp_post"], merge=1e-4),
            glass.to_object("Luminarias", mats["lamp_glass"], merge=1e-4)]


# --------------------------------------------------------------------------
# Darsena: muelles flotantes, pantalanes y lanchas
# --------------------------------------------------------------------------
def build_marina(mats, seed=17):
    rng = random.Random(seed)
    dock, piles = MeshBuilder(), MeshBuilder()
    hulls, trims, tops, motors = MeshBuilder(), MeshBuilder(), MeshBuilder(), MeshBuilder()

    fz = S["float_z"]
    dx0, dx1 = S["dock_x"]
    slips = []

    for row, dy in enumerate(S["dock_rows"]):
        _slab(dock, dx0, dx1, dy - 1.3, dy + 1.3, fz, depth=0.34, uv=18.0)
        for x in _frange(dx0 + 3.0, dx1, 6.5):
            _slab(dock, x - 0.55, x + 0.55, dy - 8.4, dy - 1.3, fz, depth=0.30, uv=4.0)
            slips.append((x, dy, row))
        for x in _frange(dx0 + 1.0, dx1 + 1.0, 13.0):
            add_cylinder(piles, (x, dy + 1.9, -2.2), 0.19, 0.16, 5.4, segments=8)
            add_cylinder(piles, (x, dy - 8.8, -2.2), 0.19, 0.16, 5.0, segments=8)

    # pasarela de acceso desde el tablado
    _slab(dock, 16.0, 19.2, S["dock_rows"][0] + 1.3, S["walk_y"] - S["walk_w"] / 2,
          fz + 0.55, depth=0.26, uv=5.0)

    for i, (x, dy, row) in enumerate(slips):
        if rng.random() < 0.22:                    # algunos amarres vacios
            continue
        side = 1 if rng.random() < 0.5 else -1
        length = rng.uniform(4.6, 7.4)
        beam = length * rng.uniform(0.32, 0.38)
        _boat(hulls, trims, tops, motors,
              x + side * (0.75 + beam / 2), dy - 3.0 - rng.uniform(0, 2.2),
              length, beam, rng)

    return [dock.to_object("Muelles_Flotantes", mats["float_dock"], merge=1e-4),
            piles.to_object("Pilotes_Darsena", mats["piling"], merge=1e-4),
            hulls.to_object("Lanchas_Casco", mats["hull"], merge=1e-4),
            trims.to_object("Lanchas_Franja", mats["hull_trim"], merge=1e-4),
            tops.to_object("Lanchas_Toldo", mats["canopy"], merge=1e-4),
            motors.to_object("Lanchas_Motor", mats["outboard"], merge=1e-4)]


# secciones del casco: (posicion a lo largo, semimanga relativa)
_STATIONS = [(0.00, 0.06), (0.14, 0.44), (0.34, 0.80), (0.58, 0.98),
             (0.82, 1.00), (1.00, 0.94)]


def _boat(hull, trim, top, motor, cx, cy, length, beam, rng):
    """Lancha amarrada: casco lofteado con regala, banera abierta y fueraborda."""
    wl = S["water_z"]
    draft = beam * 0.32
    yaw = rng.uniform(-0.10, 0.10)
    ca, sa = math.cos(yaw), math.sin(yaw)
    inner = 0.70                    # semimanga util de la banera

    def P(u, v, z):
        """u a lo largo (proa 0 -> popa 1), v semimanga con signo, z altura."""
        x = (u - 0.55) * length
        y = v * beam / 2
        return (cx + x * ca - y * sa, cy + x * sa + y * ca, wl + z)

    def sheer(u):
        """Linea de regala: sube hacia la proa."""
        return beam * (0.24 + 0.30 * (1 - u) ** 1.6)

    def rocker(u):
        return draft * (0.30 + 0.70 * math.sin(math.pi * min(u * 1.12, 1.0)))

    for (u0, k0), (u1, k1) in zip(_STATIONS, _STATIONS[1:]):
        d0, d1 = rocker(u0), rocker(u1)
        f0, f1 = sheer(u0), sheer(u1)
        for sgn in (-1, 1):
            hull.add([P(u0, sgn * k0, -d0), P(u1, sgn * k1, -d1),
                      P(u1, sgn * k1, f1), P(u0, sgn * k0, f0)],
                     [(u0, 0), (u1, 0), (u1, 1), (u0, 1)])
            hull.add([P(u0, 0, -d0 * 0.5), P(u1, 0, -d1 * 0.5),
                      P(u1, sgn * k1, -d1), P(u0, sgn * k0, -d0)])
            trim.add([P(u0, sgn * k0 * 1.006, f0 * 0.45), P(u1, sgn * k1 * 1.006, f1 * 0.45),
                      P(u1, sgn * k1 * 1.006, f1), P(u0, sgn * k0 * 1.006, f0)])

        if u1 <= 0.42:                                  # cubierta de proa
            hull.add([P(u0, -k0, f0), P(u1, -k1, f1), P(u1, k1, f1), P(u0, k0, f0)])
        else:                                           # trancanil + banera
            s0, s1 = f0 * 0.18, f1 * 0.18
            for sgn in (-1, 1):
                hull.add([P(u0, sgn * k0, f0), P(u1, sgn * k1, f1),
                          P(u1, sgn * k1 * inner, f1), P(u0, sgn * k0 * inner, f0)])
                hull.add([P(u0, sgn * k0 * inner, f0), P(u1, sgn * k1 * inner, f1),
                          P(u1, sgn * k1 * inner, s1), P(u0, sgn * k0 * inner, s0)])
            hull.add([P(u0, -k0 * inner, s0), P(u1, -k1 * inner, s1),
                      P(u1, k1 * inner, s1), P(u0, k0 * inner, s0)])

    u, k = _STATIONS[-1]                                # espejo de popa
    d, f = rocker(u), sheer(u)
    hull.add([P(u, -k, -d), P(u, k, -d), P(u, k, f), P(u, -k, f)])

    # consola central y pequeno toldo
    cu0, cu1 = 0.46, 0.46 + length * 0.17 / length
    cv = 0.40
    ch = sheer(cu0) + beam * 0.34
    for sgn in (-1, 1):
        top.add([P(cu0, sgn * cv, sheer(cu0)), P(cu1, sgn * cv, sheer(cu1)),
                 P(cu1, sgn * cv, ch), P(cu0, sgn * cv, ch)])
    top.add([P(cu0, -cv, ch), P(cu1, -cv, ch), P(cu1, cv, ch), P(cu0, cv, ch)])
    top.add([P(cu0, -cv, sheer(cu0)), P(cu0, cv, sheer(cu0)),
             P(cu0, cv, ch), P(cu0, -cv, ch)])

    # fueraborda colgado del espejo
    mu, f = 1.03, sheer(1.0)
    motor.add([P(mu, -0.12, f * 0.1), P(mu, 0.12, f * 0.1),
               P(mu, 0.12, f + beam * 0.20), P(mu, -0.12, f + beam * 0.20)])
    motor.add([P(mu - 0.04, 0.12, f * 0.1), P(mu - 0.04, -0.12, f * 0.1),
               P(mu - 0.04, -0.12, f + beam * 0.20), P(mu - 0.04, 0.12, f + beam * 0.20)])
    for sgn in (-1, 1):
        motor.add([P(mu - 0.04, sgn * 0.12, f * 0.1), P(mu, sgn * 0.12, f * 0.1),
                   P(mu, sgn * 0.12, f + beam * 0.20), P(mu - 0.04, sgn * 0.12, f + beam * 0.20)])


# --------------------------------------------------------------------------
# Kioscos, edificios y anfiteatro
# --------------------------------------------------------------------------
def build_kiosks(mats, seed=23):
    """Los 24 kioscos al aire libre, en dos hileras que forman una L."""
    rng = random.Random(seed)
    bodies = [MeshBuilder() for _ in KIOSK_COLORS]
    roofs = [MeshBuilder() for _ in ROOF_COLORS]
    frame, counter = MeshBuilder(), MeshBuilder()
    qz = S["quay_z"]

    # dos hileras enfrentadas: los mostradores dan al paseo central
    spots = []
    x0, x1 = S["kiosks_x"]
    n = S["kiosks_n"]
    ya, yb = S["kiosks_y"]
    for i in range(n):
        x = x0 + (x1 - x0) * i / (n - 1)
        spots.append((x, ya, math.pi))     # hilera sur, mira al norte
        spots.append((x, yb, 0.0))         # hilera norte, mira al sur

    for x, y, rot in spots:
        c = rng.randrange(len(KIOSK_COLORS))
        r = rng.randrange(len(ROOF_COLORS))
        _kiosk(bodies[c], frame, roofs[r], counter, x, y, rot, qz, rng)

    objs = [b.to_object(f"Kioscos_{i}", mats[f"kiosk{i}"], merge=1e-4)
            for i, b in enumerate(bodies) if not b.empty]
    objs += [r.to_object(f"Kioscos_Cubierta_{i}", mats[f"kroof{i}"], merge=1e-4)
             for i, r in enumerate(roofs) if not r.empty]
    objs += [frame.to_object("Kioscos_Estructura", mats["wall_alt"], merge=1e-4),
             counter.to_object("Kioscos_Mostrador", mats["concrete"], merge=1e-4)]
    return objs


def _kiosk(body, frame, roof, counter, cx, cy, rot, z, rng):
    """Kiosco de 4,2 x 3,6 m: cuerpo de color, mostrador al frente y tejadillo."""
    w, d, h = 3.8, 3.3, 2.70
    ca, sa = math.cos(rot), math.sin(rot)

    def at(dx, dy):
        return (cx + dx * ca - dy * sa, cy + dx * sa + dy * ca)

    # tres paramentos cerrados; el frente queda abierto sobre el mostrador
    for dx, dy, sx, sy in ((0, d / 2, w, 0.18), (-w / 2, 0, 0.18, d), (w / 2, 0, 0.18, d)):
        px, py = at(dx, dy)
        add_box(body, (px, py, z + h / 2),
                (abs(sx * ca) + abs(sy * sa), abs(sx * sa) + abs(sy * ca), h))

    px, py = at(0, -d / 2 + 0.25)
    add_box(counter, (px, py, z + 0.55),
            (abs(w * ca) + 0.5 * abs(sa), abs(w * sa) + 0.5 * abs(ca), 1.10))

    for sx in (-1, 1):                       # postes del frente
        px, py = at(sx * (w / 2 - 0.1), -d / 2)
        add_box(frame, (px, py, z + h / 2), (0.18, 0.18, h))

    px, py = at(0, 0)
    rw, rd = (w + 1.1, d + 1.1) if abs(ca) > 0.5 else (d + 1.1, w + 1.1)
    add_pyramid(roof, rw, 0.35, 1.45, z + h, center=(px, py), depth_base=rd,
                depth_top=0.35)
    add_box(roof, (px, py, z + h + 0.08), (rw + 0.2, rd + 0.2, 0.16))


def build_shore_building(mats):
    """El edificio de dos plantas con cubierta verde que aparece en la foto."""
    walls, roof, trim = MeshBuilder(), MeshBuilder(), MeshBuilder()
    qz = S["quay_z"]
    cx, cy, w, d = -26.0, 10.0, 17.0, 12.0

    add_box(walls, (cx, cy, qz + 3.3), (w, d, 6.6))
    add_box(trim, (cx, cy, qz + 3.35), (w + 0.55, d + 0.55, 0.34))      # forjado
    add_box(trim, (cx, cy - d / 2 - 0.9, qz + 3.55), (w, 1.9, 0.16))    # balcon
    for x in _frange(cx - w / 2 + 0.6, cx + w / 2, 1.5):                # barandilla
        add_box(trim, (x, cy - d / 2 - 1.75, qz + 4.05), (0.09, 0.09, 1.0))
    add_box(trim, (cx, cy - d / 2 - 1.75, qz + 4.55), (w, 0.13, 0.11))

    add_pyramid(roof, w + 2.4, 1.2, 2.5, qz + 6.6, center=(cx, cy),
                depth_base=d + 2.4, depth_top=1.2)
    objs = [walls.to_object("Edificio_Muros", mats["wall"], merge=1e-4),
            trim.to_object("Edificio_Forjados", mats["wall_alt"], merge=1e-4)]
    objs.append(roof.to_object("Edificio_Cubierta", mats["roof"], merge=1e-4))
    return objs


def build_amphitheater(mats):
    """Anfiteatro al aire libre: graderio en abanico y concha del escenario."""
    steps, stage = MeshBuilder(), MeshBuilder()
    qz = S["quay_z"]
    cx, cy = 66.0, 30.0
    rows, r0 = 9, 7.0
    for k in range(rows):
        r = r0 + k * 1.35
        z = qz + k * 0.42
        segs = 26
        a0, a1 = math.radians(200), math.radians(340)
        for i in range(segs):
            t0 = a0 + (a1 - a0) * i / segs
            t1 = a0 + (a1 - a0) * (i + 1) / segs
            p = [(cx + r * math.cos(t), cy + r * math.sin(t)) for t in (t0, t1)]
            q = [(cx + (r + 1.3) * math.cos(t), cy + (r + 1.3) * math.sin(t)) for t in (t0, t1)]
            steps.add([(p[0][0], p[0][1], z), (p[1][0], p[1][1], z),
                       (q[1][0], q[1][1], z), (q[0][0], q[0][1], z)],
                      [(0, 0), (1, 0), (1, 1), (0, 1)])
            steps.add([(p[0][0], p[0][1], z), (p[1][0], p[1][1], z),
                       (p[1][0], p[1][1], z - 0.42), (p[0][0], p[0][1], z - 0.42)])
    add_box(stage, (cx, cy - 3.0, qz + 0.45), (13.0, 8.0, 0.9))
    add_cylinder(stage, (cx, cy - 3.0, qz + 0.9), 7.4, 6.6, 0.4, segments=24, caps=True)
    return [steps.to_object("Anfiteatro_Graderio", mats["concrete"], merge=1e-4),
            stage.to_object("Anfiteatro_Escenario", mats["wall_alt"], merge=1e-4)]


# --------------------------------------------------------------------------
# Arbolado
# --------------------------------------------------------------------------
def build_planting(mats, seed=5):
    rng = random.Random(seed)
    trunks, fronds, shrubs = MeshBuilder(), MeshBuilder(), MeshBuilder()

    palms = [(-30, 8), (-22, 9), (-14, 9), (-6, 9), (2, 9), (10, 9),
             (18, 9), (26, 9), (-42, 12), (-42, 24), (-42, 36),
             (-30, 39), (-18, 39), (-6, 39), (6, 39), (18, 39),
             (44, 10), (44, 22), (44, 34), (62, 10), (78, 14), (94, 12)]
    for x, y in palms:
        vegetacion.palm(trunks, fronds, x + rng.uniform(-1, 1), y + rng.uniform(-1, 1),
              S["quay_z"], rng.uniform(6.5, 10.5), rng)

    # hilera de arbolado tras la playa, como en la foto
    by0, by1 = S["beach_y"]
    for x in _frange(-56.0, 126.0, 4.2):
        if rng.random() < 0.22:                 # claros en la hilera
            continue
        vegetacion.round_tree(shrubs, x + rng.uniform(-2.0, 2.0), by1 - rng.uniform(1.0, 7.5),
                    0.95, rng.uniform(1.9, 5.2), rng)

    return [trunks.to_object("Troncos_Palma", mats["trunk"], merge=1e-4),
            fronds.to_object("Palmas", mats["frond"], merge=1e-4),
            shrubs.to_object("Arbolado_Playa", mats["shrub"], merge=1e-4)]


# --------------------------------------------------------------------------
def _frange(a, b, step):
    v = a
    while v < b - 1e-6:
        yield v
        v += step


def build_waterfront_rail(mats):
    """Barandilla que recorre el borde del agua en la explanada y el promontorio."""
    mb = MeshBuilder()
    qz = S["quay_z"]
    mx0, mx1, my0, my1 = S["mole"]
    _railing(mb, [(mx0, my0), (mx1, my0)], qz)          # frente del promontorio
    _railing(mb, [(mx0, my0), (mx0, my1)], qz)
    _railing(mb, [(mx1, my0), (mx1, -4.4)], qz)
    lx0, lx1, ly0, _ = S["land"]
    _railing(mb, [(lx0, ly0), (mx0, ly0)], qz)          # malecon al oeste
    _railing(mb, [(mx1, ly0), (78.0, ly0)], qz)         # malecon al este
    return [mb.to_object("Barandilla_Malecon", mats["rail"], merge=1e-4)]


def build_benches(mats):
    """Bancos a lo largo del tablado y del paseo de kioscos."""
    mb = MeshBuilder()
    dz, wy, ww = S["deck_z"], S["walk_y"], S["walk_w"]
    for x in _frange(S["walk_x"][0] + 9.0, S["walk_x"][1], 13.0):
        _bench(mb, x, wy - ww / 2 + 0.9, 0.0, dz)
    ya, yb = S["kiosks_y"]
    for x in _frange(S["kiosks_x"][0] + 3.0, S["kiosks_x"][1], 11.0):
        _bench(mb, x, (ya + yb) / 2, 0.0, S["quay_z"])
    return [mb.to_object("Bancos", mats["rail"], merge=1e-4)]


def _bench(mb, cx, cy, rot, z):
    ca, sa = math.cos(rot), math.sin(rot)
    sx, sy = 1.9, 0.52
    add_box(mb, (cx, cy, z + 0.44),
            (abs(sx * ca) + abs(sy * sa), abs(sx * sa) + abs(sy * ca), 0.09))
    add_box(mb, (cx - sa * 0.24, cy + ca * 0.24, z + 0.72),
            (abs(sx * ca) + 0.09 * abs(sa), abs(sx * sa) + 0.09 * abs(ca), 0.46))
    for t in (-0.72, 0.72):
        add_box(mb, (cx + t * ca, cy + t * sa, z + 0.22), (0.12, 0.12, 0.44))


def build_site(mats):
    objs = []
    for fn in (build_water, build_ground, build_boardwalk, build_waterfront_rail,
               build_marina, build_kiosks, build_benches, build_shore_building,
               build_amphitheater, build_planting):
        objs.extend(o for o in fn(mats) if o is not None)
    return objs
