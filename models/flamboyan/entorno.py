"""
Entorno del conjunto: vial de acceso, aparcamiento, verja perimetral, el muro
del rotulo de entrada, alumbrado, tendido electrico y arbolado.

Origen en el eje del vial, a la altura de la entrada. +Y hacia el interior del
conjunto, +X al este.
"""

import math
import os
import random
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))

import vegetacion  # noqa: E402
from matlib import material  # noqa: E402
from meshlib import (  # noqa: E402
    MeshBuilder, add_box, add_box_rot, add_cylinder, link_copy,
)

ROOT = os.path.dirname(os.path.dirname(HERE))
TEX = os.path.join(ROOT, "textures")

S = dict(
    road_half=4.2, road_y=(-26.0, 96.0),
    walk=1.7, curb_z=0.15,
    park=(-40.0, -7.0, 0.0, 30.0),
    ground=(-82.0, 72.0, -30.0, 104.0),
    # (centro, giro): el bloque mira a -Y en local, el giro lo orienta
    blocks=[((16.0, 22.0), -math.pi / 2),      # el del primer termino, al vial
            ((-22.0, 52.0), 0.0),
            ((20.0, 52.0), 0.0),
            ((-50.0, 20.0), math.pi / 2)],
)

CAR_COLORS = [
    (0.552, 0.560, 0.575), (0.036, 0.040, 0.046), (0.720, 0.724, 0.716),
    (0.330, 0.060, 0.070), (0.055, 0.115, 0.250), (0.145, 0.240, 0.155),
]


def _tex(n):
    return os.path.join(TEX, n)


def build_site_materials():
    m = dict(
        asphalt=material("Asfalto", (0.5, 0.5, 0.5), 0.90, texture=_tex("asphalt.png")),
        walk=material("Acera", (0.6, 0.6, 0.58), 0.88, texture=_tex("concrete.png")),
        grass=material("Cesped", (0.128, 0.238, 0.104), 0.94),
        paint=material("Pintura_Vial", (0.880, 0.720, 0.130), 0.70),
        fence_base=material("Verja_Zocalo", (0.06, 0.16, 0.20), 0.82,
                            texture=_tex("painted_teal.png")),
        picket=material("Verja_Barrotes", (0.700, 0.712, 0.720), 0.42, metallic=0.55),
        sign=material("Rotulo_Muro", (0.055, 0.150, 0.195), 0.72),
        letters=material("Rotulo_Letras", (0.900, 0.905, 0.895), 0.50),
        pole=material("Poste", (0.545, 0.535, 0.510), 0.88),
        wood_pole=material("Poste_Madera", (0.250, 0.205, 0.160), 0.90),
        fixture=material("Luminaria", (0.470, 0.478, 0.482), 0.42, metallic=0.5),
        wire=material("Tendido", (0.045, 0.045, 0.048), 0.70),
        glass=material("Lunas", (0.028, 0.038, 0.048), 0.18, metallic=0.3),
        tyre=material("Neumatico", (0.030, 0.030, 0.032), 0.86),
        trunk=material("Tronco", (0.262, 0.208, 0.152), 0.90),
        leaf=material("Follaje", (0.088, 0.230, 0.092), 0.72),
        flower=material("Flor_Flamboyan", (0.640, 0.075, 0.048), 0.66),
        palm=material("Palma", (0.118, 0.318, 0.118), 0.68),
    )
    for i, c in enumerate(CAR_COLORS):
        m[f"car{i}"] = material(f"Carroceria_{i}", c, 0.30, metallic=0.55)
    return m


# --------------------------------------------------------------------------
def _slab(mb, x0, x1, y0, y1, z, depth=0.0, uv=1.0):
    mb.add([(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)],
           [(0, 0), (uv, 0), (uv, uv), (0, uv)])
    if depth <= 0:
        return
    zb = z - depth
    for (ax, ay), (bx, by) in (((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)),
                               ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))):
        mb.add([(ax, ay, zb), (bx, by, zb), (bx, by, z), (ax, ay, z)],
               [(0, 0), (uv, 0), (uv, 1), (0, 1)])


def build_ground(mats):
    """Cesped de fondo, calzada, aparcamiento, aceras y bordillos pintados."""
    gx0, gx1, gy0, gy1 = S["ground"]
    r = S["road_half"]
    ry0, ry1 = S["road_y"]
    px0, px1, py0, py1 = S["park"]

    grass = MeshBuilder()
    _slab(grass, gx0, gx1, gy0, gy1, 0.0, uv=30.0)

    road = MeshBuilder()
    _slab(road, -r, r, ry0, ry1, 0.03, uv=26.0)
    _slab(road, px0, px1, py0, py1, 0.03, uv=22.0)
    _slab(road, px1, -r, 9.0, 23.0, 0.03, uv=4.0)          # acceso al parking

    walk = MeshBuilder()
    w = S["walk"]
    for sx in (-1, 1):
        _slab(walk, sx * r, sx * (r + w), ry0, ry1, S["curb_z"], depth=0.15, uv=24.0)

    paint = MeshBuilder()
    for sx in (-1, 1):                                      # bordillo pintado
        add_box(paint, (sx * (r + 0.07), (ry0 + ry1) / 2, S["curb_z"] - 0.06),
                (0.16, ry1 - ry0, 0.18))
    y = ry0 + 2.0                                           # eje discontinuo
    while y < ry1 - 2.0:
        add_box(paint, (0.0, y, 0.04), (0.14, 1.9, 0.02))
        y += 4.6
    for k in range(13):                                     # marcas del parking
        x = px0 + 1.4 + k * 3.1
        for yy in (py0 + 1.0, py0 + 11.0, py0 + 21.0):
            add_box(paint, (x, yy + 2.4, 0.04), (0.10, 4.8, 0.02))

    return [grass.to_object("Cesped", mats["grass"], merge=1e-4),
            road.to_object("Asfalto", mats["asphalt"], merge=1e-4),
            walk.to_object("Aceras", mats["walk"], merge=1e-4),
            paint.to_object("Pintura_Vial", mats["paint"], merge=1e-4)]


# --------------------------------------------------------------------------
def build_fence(mats):
    """Verja perimetral: zocalo de hormigon turquesa y barrotes metalicos."""
    base, pickets = MeshBuilder(), MeshBuilder()
    px0, px1, py0, py1 = S["park"]
    runs = [((px0, py0), (px1, py0)), ((px1, py0), (px1, py1)),
            ((px0, py1), (px0, py0)),
            ((8.0, -3.0), (26.0, -3.0)), ((26.0, -3.0), (26.0, 16.0))]
    for p0, p1 in runs:
        _fence_run(base, pickets, p0, p1)
    return [base.to_object("Verja_Zocalo", mats["fence_base"], merge=1e-4),
            pickets.to_object("Verja_Barrotes", mats["picket"], merge=1e-4)]


def _fence_run(base, pickets, p0, p1, h_base=0.74, h_rail=1.06, pier=3.6):
    (x0, y0), (x1, y1) = p0, p1
    dx, dy = x1 - x0, y1 - y0
    length = math.hypot(dx, dy)
    if length < 0.5:
        return
    ux, uy = dx / length, dy / length
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    sx = abs(ux) * length + abs(uy) * 0.24
    sy = abs(uy) * length + abs(ux) * 0.24
    add_box(base, (cx, cy, h_base / 2), (sx, sy, h_base), uv_scale=length / 3.0)

    n = max(1, int(length / pier))
    for k in range(n + 1):                                  # machones
        t = k / n
        add_box(base, (x0 + dx * t, y0 + dy * t, (h_base + 0.55) / 2),
                (0.42, 0.42, h_base + 0.55))

    nb = max(2, int(length / 0.155))
    for k in range(nb):                                     # barrotes
        t = (k + 0.5) / nb
        add_box(pickets, (x0 + dx * t, y0 + dy * t, h_base + h_rail / 2),
                (0.035, 0.035, h_rail))
    for z in (h_base + 0.10, h_base + h_rail - 0.06):       # travesanos
        add_box(pickets, (cx, cy, z), (sx, sy, 0.05))


# --------------------------------------------------------------------------
def build_sign(mats):
    """Muro del rotulo de entrada, con las letras en relieve."""
    wall = MeshBuilder()
    cx, cy, w, h, t = 13.5, -3.0, 8.4, 2.35, 0.36
    add_box(wall, (cx, cy, h / 2), (w, t, h))
    add_box(wall, (cx, cy, h + 0.07), (w + 0.22, t + 0.22, 0.14))   # albardilla
    add_box(wall, (cx - w / 2 - 0.45, cy, 1.35), (0.9, t + 0.5, 2.7))  # machon
    objs = [wall.to_object("Rotulo_Muro", mats["sign"], merge=1e-4)]

    letters = _text_mesh("CONDOMINIO", 0.30, mats["letters"], (cx, cy - t / 2, 1.62))
    if letters:
        objs.append(letters)
    letters = _text_mesh("EL FLAMBOYÁN", 0.52, mats["letters"], (cx, cy - t / 2, 1.05))
    if letters:
        objs.append(letters)
    return objs


def _text_mesh(body, size, mat, location):
    """Texto en relieve sobre el muro. Blender trae su propia tipografia, asi
    que no hace falta ningun archivo de fuente."""
    try:
        bpy.ops.object.text_add(location=(0.0, 0.0, 0.0))
        ob = bpy.context.object
        ob.data.body = body
        ob.data.size = size
        ob.data.extrude = 0.022
        ob.data.align_x = "CENTER"
        ob.data.align_y = "CENTER"
        bpy.ops.object.convert(target="MESH")
        ob = bpy.context.object
        ob.name = f"Rotulo_{body.split()[0]}"
        # align_x no sobrevive a la conversion a malla: se centra la geometria
        # a mano sobre el origen, que es lo que luego coloca el objeto
        co = [v.co for v in ob.data.vertices]
        if co:
            cx = (min(c.x for c in co) + max(c.x for c in co)) / 2.0
            cy = (min(c.y for c in co) + max(c.y for c in co)) / 2.0
            for v in ob.data.vertices:
                v.co.x -= cx
                v.co.y -= cy
        ob.rotation_euler = (math.pi / 2, 0.0, 0.0)
        ob.location = location
        ob.data.materials.append(mat)
        return ob
    except Exception as exc:                 # sin contexto de operador utilizable
        print("[rotulo] no se pudo generar el texto:", exc)
        return None


# --------------------------------------------------------------------------
def build_cars(mats, seed=12):
    """Coches aparcados. Se modela un coche por color y se instancia el resto."""
    rng = random.Random(seed)
    masters = []
    for i in range(len(CAR_COLORS)):
        body, glass, tyre = MeshBuilder(), MeshBuilder(), MeshBuilder()
        _car(body, glass, tyre)
        b = body.to_object(f"Coche_{i}", mats[f"car{i}"], merge=1e-4)
        g = glass.to_object(f"Coche_Lunas_{i}", mats["glass"], merge=1e-4)
        t = tyre.to_object(f"Coche_Ruedas_{i}", mats["tyre"], merge=1e-4)
        g.parent, t.parent = b, b
        masters.append((b, g, t))

    px0, _, py0, _ = S["park"]
    placed = []
    for row, yy in enumerate((py0 + 3.4, py0 + 13.4, py0 + 23.4)):
        for k in range(10):
            if rng.random() < 0.18:                 # plazas libres
                continue
            x = px0 + 2.9 + k * 3.1
            yaw = math.pi / 2 if row % 2 == 0 else -math.pi / 2
            group = masters[rng.randrange(len(masters))]
            loc = (x, yy + rng.uniform(-0.2, 0.2), 0.03)
            parent = link_copy(group[0], loc, yaw + rng.uniform(-0.03, 0.03))
            for child in group[1:]:
                dup = link_copy(child, loc, parent.rotation_euler.z)
                dup.parent = parent
                dup.location = (0.0, 0.0, 0.0)
                dup.rotation_euler = (0.0, 0.0, 0.0)
                placed.append(dup)
            placed.append(parent)

    for group in masters:                           # las maestras no se ven
        for o in group:
            o.hide_render = True
            o.hide_viewport = True
    return [o for g in masters for o in g] + placed


def _car(body, glass, tyre, length=4.34, width=1.79, seed=0):
    """Turismo de bajo poligonaje, mirando a +X."""
    add_box(body, (0.0, 0.0, 0.62), (length, width, 0.74))
    add_box(body, (-0.18, 0.0, 1.24), (length * 0.50, width * 0.90, 0.56))
    add_box(body, (length / 2 - 0.10, 0.0, 0.44), (0.16, width * 0.86, 0.22))
    for sx in (-1, 1):                                       # lunas
        add_box(glass, (-0.18, sx * width * 0.452, 1.26),
                (length * 0.47, 0.03, 0.42))
    add_box(glass, (-0.18 + length * 0.25, 0.0, 1.26), (0.04, width * 0.84, 0.44))
    add_box(glass, (-0.18 - length * 0.25, 0.0, 1.26), (0.04, width * 0.84, 0.44))
    for sx in (-1, 1):
        for sy in (-1, 1):
            add_cylinder(tyre, (sx * length * 0.33, sy * (width / 2 - 0.11), 0.33),
                         0.33, 0.33, 0.22, segments=12, axis="Y")


# --------------------------------------------------------------------------
def build_services(mats):
    """Alumbrado publico y tendido electrico aereo."""
    poles, heads, wood, wires = MeshBuilder(), MeshBuilder(), MeshBuilder(), MeshBuilder()
    r = S["road_half"]

    for y in (6.0, 32.0, 58.0, 84.0):                        # farolas de calzada
        x = r + 1.0
        add_cylinder(poles, (x, y, 0.0), 0.17, 0.11, 8.4, segments=10)
        add_box(heads, (x - 0.85, y, 8.35), (1.9, 0.26, 0.20))
        add_box(heads, (x - 1.75, y, 8.16), (0.86, 0.40, 0.26))

    # postes de madera con cruceta, y los cables colgando entre ellos
    pole_xy = [(7.0, -16.0), (7.0, 12.0), (7.0, 42.0), (7.0, 72.0)]
    for x, y in pole_xy:
        add_cylinder(wood, (x, y, 0.0), 0.22, 0.16, 10.2, segments=8)
        add_box(wood, (x, y, 9.35), (0.14, 2.6, 0.16))
    for (x0, y0), (x1, y1) in zip(pole_xy, pole_xy[1:]):
        for off, z in ((-1.05, 9.42), (0.0, 9.42), (1.05, 9.42), (0.0, 7.9)):
            _wire(wires, (x0, y0 + off, z), (x1, y1 + off, z), sag=0.9)

    return [poles.to_object("Farolas", mats["pole"], merge=1e-4),
            heads.to_object("Luminarias", mats["fixture"], merge=1e-4),
            wood.to_object("Postes_Electricos", mats["wood_pole"], merge=1e-4),
            wires.to_object("Tendido", mats["wire"], merge=1e-4)]


def _wire(mb, p0, p1, sag=0.8, segments=10, thick=0.045):
    """Cable con catenaria, resuelto como una cadena de prismas finos."""
    (x0, y0, z0), (x1, y1, z1) = p0, p1
    pts = []
    for k in range(segments + 1):
        t = k / segments
        droop = sag * 4 * t * (1 - t)
        pts.append((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t,
                    z0 + (z1 - z0) * t - droop))
    for a, b in zip(pts, pts[1:]):
        mid = tuple((u + v) / 2 for u, v in zip(a, b))
        dy = b[1] - a[1]
        dz = b[2] - a[2]
        span = math.hypot(dy, dz)
        add_box_rot(mb, mid, (thick, span, thick), rx=math.atan2(dz, dy))


# --------------------------------------------------------------------------
def build_planting(mats, seed=8):
    trunks, leaves, flowers = MeshBuilder(), MeshBuilder(), MeshBuilder()
    palm_t, palm_f = MeshBuilder(), MeshBuilder()
    rng = random.Random(seed)

    for x, y, h in ((30.0, -12.0, 9.4), (-16.0, -11.0, 8.4), (33.0, 34.0, 10.2),
                    (-62.0, 34.0, 8.8), (-2.0, 86.0, 9.6), (44.0, 16.0, 8.0)):
        vegetacion.flamboyan(trunks, leaves, flowers, x, y, 0.0, h, rng)

    for x, y in ((-64.0, -6.0), (-64.0, 8.0), (-64.0, 46.0), (38.0, -14.0),
                 (44.0, 2.0), (44.0, 30.0), (-8.0, 82.0), (14.0, 82.0)):
        vegetacion.palm(palm_t, palm_f, x, y, 0.0, rng.uniform(7.0, 11.0), rng)

    for x in range(-78, 70, 7):                              # masa de fondo
        vegetacion.round_tree(leaves, x + rng.uniform(-2, 2),
                              92.0 + rng.uniform(-4, 8), 0.0,
                              rng.uniform(4.0, 7.5), rng)

    return [trunks.to_object("Troncos", mats["trunk"], merge=1e-4),
            leaves.to_object("Follaje", mats["leaf"], merge=1e-4),
            flowers.to_object("Flor_Flamboyan", mats["flower"], merge=1e-4),
            palm_t.to_object("Troncos_Palma", mats["trunk"], merge=1e-4),
            palm_f.to_object("Palmas", mats["palm"], merge=1e-4)]


# --------------------------------------------------------------------------
def build_site(mats):
    objs = []
    for fn in (build_ground, build_fence, build_sign, build_cars,
               build_services, build_planting):
        objs.extend(o for o in fn(mats) if o is not None)
    return objs
