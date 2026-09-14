"""
Modelo 3D parametrico del Faro de La Guancha (Paseo Tablado La Guancha, Ponce,
Puerto Rico), reconstruido a partir de fotografia de referencia.

Genera el .blend, exporta .glb y opcionalmente renderiza vistas con Cycles.

    python3 models/guancha/build.py --render

Todas las medidas estan en metros y viven en el diccionario P: cambiar un valor
reconstruye el modelo completo de forma consistente.
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from meshlib import (  # noqa: E402
    MeshBuilder, add_box, add_cylinder, add_frustum, add_pyramid,
    frustum_face_mapper, wall_panels,
)

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEX = os.path.join(ROOT, "textures")

# --------------------------------------------------------------------------
# Parametros del edificio (metros)
# --------------------------------------------------------------------------
P = dict(
    base_h=4.60, base_w0=5.65, base_w1=5.25,      # zocalo de mamposteria
    plinth_h=0.42, plinth_w0=5.45, plinth_w1=5.10,  # cordon amarillo
    shaft_h=8.60, shaft_w0=4.72, shaft_w1=4.52,   # fuste con la bandera
    band_a_h=0.40, dentil_h=0.24, band_b_h=0.32,  # cornisa
    deck_h=0.36, deck_w=6.40,                      # losa del mirador
    rail_h=1.08, rail_w=6.26,
    col_h=2.80, col_s=0.40, col_ring=2.68,        # columnas del templete
    entab_h=0.44, entab_w=6.20,
    eave_w=7.55, skirt_h=0.62, roof_w=6.35,       # cubierta a cuatro aguas
    roof_h=1.78, roof_top=0.55,
)

# alturas acumuladas
Z_BASE = 0.0
Z_PLINTH = Z_BASE + P["base_h"]
Z_SHAFT = Z_PLINTH + P["plinth_h"]
Z_CORNICE = Z_SHAFT + P["shaft_h"]
Z_DENTIL = Z_CORNICE + P["band_a_h"]
Z_BAND_B = Z_DENTIL + P["dentil_h"]
Z_DECK = Z_BAND_B + P["band_b_h"]
Z_DECK_TOP = Z_DECK + P["deck_h"]
Z_ENTAB = Z_DECK_TOP + P["col_h"]
Z_EAVE = Z_ENTAB + P["entab_h"]
Z_ROOF = Z_EAVE + P["skirt_h"]
Z_APEX = Z_ROOF + P["roof_h"]

INWARD = [(0, 1), (-1, 0), (0, -1), (1, 0)]  # normal interior por cara


# --------------------------------------------------------------------------
# Materiales
# --------------------------------------------------------------------------
def _image(path):
    img = bpy.data.images.load(path, check_existing=True)
    img.pack()
    return img


def material(name, color=(0.8, 0.8, 0.8), roughness=0.6, metallic=0.0,
             texture=None, normal_map=None, emission=None, emission_power=0.0):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic

    if texture:
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = _image(texture)
        tex.location = (-600, 200)
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    if normal_map:
        ntex = nt.nodes.new("ShaderNodeTexImage")
        ntex.image = _image(normal_map)
        ntex.image.colorspace_settings.name = "Non-Color"
        ntex.location = (-600, -200)
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nmap.location = (-300, -200)
        nmap.inputs["Strength"].default_value = 1.0
        nt.links.new(ntex.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    if emission:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
        bsdf.inputs["Emission Strength"].default_value = emission_power
    return mat


def ensure_textures():
    """Las texturas son deterministas: si faltan, se generan al vuelo."""
    needed = ("pr_flag_atlas.png", "stone_base_color.png", "stone_base_normal.png")
    if all(os.path.exists(os.path.join(TEX, n)) for n in needed):
        return
    print("[guancha] generando texturas en", TEX)
    sys.path.insert(0, os.path.join(ROOT, "tools"))
    import textures
    textures.main(TEX)


def build_materials():
    ensure_textures()
    return dict(
        stone=material("Piedra_Zocalo", (0.68, 0.63, 0.54), 0.88,
                       texture=os.path.join(TEX, "stone_base_color.png"),
                       normal_map=os.path.join(TEX, "stone_base_normal.png")),
        flag=material("Bandera_PR", (0.8, 0.8, 0.8), 0.62,
                      texture=os.path.join(TEX, "pr_flag_atlas.png")),
        trim=material("Amarillo_Cornisa", (0.965, 0.800, 0.290), 0.55),
        cream=material("Crema_Columnas", (0.940, 0.908, 0.822), 0.52),
        roof=material("Verde_Cubierta", (0.072, 0.383, 0.186), 0.40, metallic=0.10),
        metal=material("Metal_Baranda", (0.105, 0.105, 0.118), 0.44, metallic=0.75),
        dark=material("Interior_Oscuro", (0.045, 0.038, 0.032), 0.92),
        speaker=material("Bocinas", (0.055, 0.055, 0.06), 0.55, metallic=0.2),
        lamp=material("Luz_Faro", (0.55, 1.0, 0.72), 0.3,
                      emission=(0.30, 1.0, 0.55), emission_power=3.5),
    )


# --------------------------------------------------------------------------
# Muros con huecos
# --------------------------------------------------------------------------
def build_wall(mb_wall, mb_recess, w0, w1, h, z0, face, openings,
               uv_span=(0.0, 1.0), uv_repeat=(1.0, 1.0), reveal=0.22):
    """Levanta una cara con sus huecos y las mochetas correspondientes."""
    width = (w0 + w1) / 2.0
    f = frustum_face_mapper(w0, w1, h, z0, face)
    u0, u1 = uv_span
    ru, rv = uv_repeat

    def uv(x, y):
        return (u0 + (x / width) * (u1 - u0) * ru, (y / h) * rv)

    panels, outlines = wall_panels(width, h, openings)
    for x0, x1, y0, y1 in panels:
        mb_wall.add(
            [f(x0 / width, y0 / h), f(x1 / width, y0 / h),
             f(x1 / width, y1 / h), f(x0 / width, y1 / h)],
            [uv(x0, y0), uv(x1, y0), uv(x1, y1), uv(x0, y1)],
        )

    ix, iy = INWARD[face]
    for op, outline in outlines:
        d = op.get("depth", reveal)
        pts = [f(x / width, y / h) for x, y in outline]
        inner = [(p[0] + ix * d, p[1] + iy * d, p[2]) for p in pts]
        for k in range(len(pts)):
            j = (k + 1) % len(pts)
            mb_recess.add([pts[k], pts[j], inner[j], inner[k]],
                          [(0, 0), (1, 0), (1, 1), (0, 1)])
        # fondo del hueco: rectangulo que cubre todo el contorno
        bx0, bx1 = op["x0"], op["x1"]
        by0, by1 = op["y0"], op["y1"]
        back = [f(bx0 / width, by0 / h), f(bx1 / width, by0 / h),
                f(bx1 / width, by1 / h), f(bx0 / width, by1 / h)]
        mb_recess.add([(p[0] + ix * d, p[1] + iy * d, p[2]) for p in back],
                      [(0, 0), (1, 0), (1, 1), (0, 1)])


def window_grille(mb, w0, w1, h, z0, face, op, bars=3):
    """Reja sencilla dentro del hueco de una ventana."""
    width = (w0 + w1) / 2.0
    f = frustum_face_mapper(w0, w1, h, z0, face)
    ix, iy = INWARD[face]
    d, t = 0.07, 0.030
    x0, x1, y0, y1 = op["x0"], op["x1"], op["y0"], op["y1"]
    for k in range(1, bars + 1):
        x = x0 + (x1 - x0) * k / (bars + 1)
        a = f(x / width, y0 / h)
        b = f(x / width, y1 / h)
        _bar(mb, a, b, ix, iy, d, t)
    for k in range(1, bars + 1):
        y = y0 + (y1 - y0) * k / (bars + 1)
        a = f(x0 / width, y / h)
        b = f(x1 / width, y / h)
        _bar(mb, a, b, ix, iy, d, t)


def _bar(mb, a, b, ix, iy, depth, t):
    """Barrote prismatico entre dos puntos de la cara, desplazado hacia dentro."""
    ax, ay, az = a
    bx, by, bz = b
    dx, dy, dz = bx - ax, by - ay, bz - az
    ln = math.sqrt(dx * dx + dy * dy + dz * dz) or 1.0
    dx, dy, dz = dx / ln, dy / ln, dz / ln
    # perpendicular contenida en la cara
    px, py, pz = (iy * dz - 0 * dy), (0 * dx - ix * dz), (ix * dy - iy * dx)
    pn = math.sqrt(px * px + py * py + pz * pz) or 1.0
    px, py, pz = px / pn * t, py / pn * t, pz / pn * t
    ox, oy = ix * depth, iy * depth
    nx, ny, nz = ix * t, iy * t, 0.0
    corners = []
    for base in (a, b):
        cx, cy, cz = base[0] + ox, base[1] + oy, base[2]
        corners.append([
            (cx + px - nx, cy + py - ny, cz + pz), (cx - px - nx, cy - py - ny, cz - pz),
            (cx - px + nx, cy - py + ny, cz - pz), (cx + px + nx, cy + py + ny, cz + pz),
        ])
    lo, hi = corners
    for k in range(4):
        j = (k + 1) % 4
        mb.add([lo[k], lo[j], hi[j], hi[k]])
    mb.add(list(reversed(lo)))
    mb.add(hi)


# --------------------------------------------------------------------------
# Piezas del edificio
# --------------------------------------------------------------------------
def build_base(mats):
    """Zocalo de mamposteria con la puerta de arco de medio punto."""
    wall, recess = MeshBuilder(), MeshBuilder()
    door = dict(x0=2.05, x1=3.35, y0=0.0, y1=2.55, arch=True, depth=0.34)
    vent = dict(x0=2.45, x1=2.95, y0=2.95, y1=3.30, depth=0.16)
    for face in range(4):
        ops = [door, vent] if face == 0 else []
        build_wall(wall, recess, P["base_w0"], P["base_w1"], P["base_h"], Z_BASE,
                   face, ops, uv_repeat=(2.2, 3.0))
    objs = [wall.to_object("Zocalo_Piedra", mats["stone"], merge=1e-4),
            recess.to_object("Zocalo_Huecos", mats["dark"], merge=1e-4)]

    cap = MeshBuilder()
    add_frustum(cap, P["plinth_w0"], P["plinth_w1"], P["plinth_h"], Z_PLINTH,
                cap_top=False, cap_bottom=True)
    objs.append(cap.to_object("Cordon_Amarillo", mats["trim"], merge=1e-4))
    return objs


def build_shaft(mats):
    """Fuste cuadrado con la bandera de Puerto Rico y sus ventanucos."""
    wall, recess, grilles = MeshBuilder(), MeshBuilder(), MeshBuilder()
    w0, w1, h = P["shaft_w0"], P["shaft_w1"], P["shaft_h"]
    width = (w0 + w1) / 2.0
    # el atlas tiene la bandera completa a la izquierda y solo franjas a la
    # derecha: caras frontal/trasera llevan el triangulo, las laterales no
    spans = [(0.0, 0.5), (0.5, 1.0), (0.0, 0.5), (0.5, 1.0)]

    for face in range(4):
        cx = width / 2.0
        ops = []
        for y in (2.05, 5.95):
            ops.append(dict(x0=cx - 0.36, x1=cx + 0.36, y0=y, y1=y + 0.86,
                            depth=0.20))
        build_wall(wall, recess, w0, w1, h, Z_SHAFT, face, ops,
                   uv_span=spans[face])
        for op in ops:
            window_grille(grilles, w0, w1, h, Z_SHAFT, face, op)

    return [wall.to_object("Fuste_Bandera", mats["flag"], merge=1e-4),
            recess.to_object("Fuste_Huecos", mats["dark"], merge=1e-4),
            grilles.to_object("Rejas", mats["cream"], merge=1e-4)]


def build_cornice(mats):
    """Cornisa escalonada, canecillos y losa del mirador."""
    trim = MeshBuilder()
    add_frustum(trim, P["shaft_w1"] + 0.04, 4.96, P["band_a_h"], Z_CORNICE)
    add_frustum(trim, 5.34, 6.06, P["band_b_h"], Z_BAND_B)
    objs = [trim.to_object("Cornisa_Amarilla", mats["trim"], merge=1e-4)]

    dentils = MeshBuilder()
    n, reach = 13, 2.62
    for side in range(4):
        for k in range(n):
            t = (k + 0.5) / n
            s = (t - 0.5) * (reach * 2.0)
            x, y = [(s, -reach), (reach, s), (-s, reach), (-reach, -s)][side]
            sx, sy = (0.17, 0.30) if side % 2 == 0 else (0.30, 0.17)
            add_box(dentils, (x, y, Z_DENTIL + P["dentil_h"] / 2),
                    (sx, sy, P["dentil_h"]))
    objs.append(dentils.to_object("Canecillos", mats["cream"], merge=1e-4))

    deck = MeshBuilder()
    add_frustum(deck, 6.06, P["deck_w"], P["deck_h"], Z_DECK,
                cap_top=True, cap_bottom=True)
    objs.append(deck.to_object("Losa_Mirador", mats["trim"], merge=1e-4))

    # pavimento del mirador, por encima del antepecho amarillo
    floor = MeshBuilder()
    add_box(floor, (0, 0, Z_DECK_TOP + 0.02), (P["deck_w"] - 0.12, P["deck_w"] - 0.12, 0.06))
    objs.append(floor.to_object("Piso_Mirador", mats["cream"], merge=1e-4))
    return objs


def build_railing(mats):
    """Barandal metalico oscuro del mirador."""
    mb = MeshBuilder()
    a = P["rail_w"] / 2.0
    z0 = Z_DECK_TOP
    h = P["rail_h"]

    for side in range(4):
        sx, sy = (P["rail_w"], 0.09) if side % 2 == 0 else (0.09, P["rail_w"])
        cx, cy = [(0, -a), (a, 0), (0, a), (-a, 0)][side]
        add_box(mb, (cx, cy, z0 + 0.09), (sx, sy, 0.10))          # zocalo
        add_box(mb, (cx, cy, z0 + h - 0.06), (sx, sy, 0.12))      # pasamanos
        add_box(mb, (cx, cy, z0 + h * 0.52), (sx, sy, 0.06))      # travesano
        n = 19
        for k in range(n):
            t = (k + 0.5) / n
            s = (t - 0.5) * P["rail_w"]
            bx, by = [(s, -a), (a, s), (-s, a), (-a, -s)][side]
            add_box(mb, (bx, by, z0 + h / 2), (0.045, 0.045, h - 0.1)
                    if side % 2 == 0 else (0.045, 0.045, h - 0.1))
    for sx in (-1, 1):
        for sy in (-1, 1):
            add_box(mb, (sx * a, sy * a, z0 + h / 2 + 0.05),
                    (0.16, 0.16, h + 0.10))
    return [mb.to_object("Barandal", mats["metal"], merge=1e-4)]


def build_pavilion(mats):
    """Templete: columnas, capiteles y arquitrabe."""
    mb = MeshBuilder()
    c = P["col_ring"]
    s = P["col_s"]
    positions = [(x, y) for x in (-c, 0, c) for y in (-c, 0, c) if (x, y) != (0, 0)]
    for x, y in positions:
        add_box(mb, (x, y, Z_DECK_TOP + 0.13), (s + 0.14, s + 0.14, 0.26))
        add_box(mb, (x, y, Z_DECK_TOP + P["col_h"] / 2), (s, s, P["col_h"]))
        add_box(mb, (x, y, Z_ENTAB - 0.16), (s + 0.10, s + 0.10, 0.32))
        add_box(mb, (x, y, Z_ENTAB - 0.44), (s + 0.05, s + 0.05, 0.14))
    objs = [mb.to_object("Columnas", mats["cream"], merge=1e-4)]

    entab = MeshBuilder()
    add_frustum(entab, P["entab_w"], P["entab_w"], P["entab_h"], Z_ENTAB)
    objs.append(entab.to_object("Arquitrabe", mats["cream"], merge=1e-4))
    return objs


def build_roof(mats):
    """Cubierta a cuatro aguas con faldon acampanado y costillas de chapa."""
    mb = MeshBuilder()
    # faldon inferior, mas tendido
    _ribbed_hip(mb, P["eave_w"], P["roof_w"], P["skirt_h"], Z_EAVE, ribs=22)
    # paños principales
    _ribbed_hip(mb, P["roof_w"], P["roof_top"], P["roof_h"], Z_ROOF, ribs=22)
    add_box(mb, (0, 0, Z_APEX + 0.10), (P["roof_top"] + 0.14, P["roof_top"] + 0.14, 0.20))
    add_cylinder(mb, (0, 0, Z_APEX + 0.20), 0.09, 0.09, 0.55, segments=12)
    add_cylinder(mb, (0, 0, Z_APEX + 0.75), 0.17, 0.02, 0.26, segments=12)
    objs = [mb.to_object("Cubierta", mats["roof"], merge=1e-4, smooth=False)]

    # alero volado + faja decorativa colgante
    fascia = MeshBuilder()
    a = P["eave_w"] / 2.0
    for side in range(4):
        sx, sy = (P["eave_w"], 0.10) if side % 2 == 0 else (0.10, P["eave_w"])
        cx, cy = [(0, -a), (a, 0), (0, a), (-a, 0)][side]
        add_box(fascia, (cx, cy, Z_EAVE - 0.11), (sx, sy, 0.22))
        n = 26
        for k in range(n):
            t = (k + 0.5) / n
            s = (t - 0.5) * P["eave_w"]
            bx, by = [(s, -a), (a, s), (-s, a), (-a, -s)][side]
            tab = 0.20 if k % 2 == 0 else 0.30
            add_box(fascia, (bx, by, Z_EAVE - 0.22 - tab / 2),
                    (0.13, 0.13, tab))
    objs.append(fascia.to_object("Faja_Alero", mats["roof"], merge=1e-4))
    return objs


def _ribbed_hip(mb, w_base, w_top, h, z0, ribs=20, rib_out=0.045):
    """Faldon a cuatro aguas dividido en franjas alternas -> aspecto de chapa."""
    a, b = w_base / 2.0, w_top / 2.0
    z1 = z0 + h
    base = [(-a, -a), (a, -a), (a, a), (-a, a)]
    top = [(-b, -b), (b, -b), (b, b), (-b, b)]
    for i in range(4):
        j = (i + 1) % 4
        p0, p1 = base[i], base[j]
        q0, q1 = top[i], top[j]
        # normal del paño
        ex, ey = p1[0] - p0[0], p1[1] - p0[1]
        ux, uy, uz = q0[0] - p0[0], q0[1] - p0[1], h
        nx = ey * uz
        ny = -ex * uz
        nz = ex * uy - ey * ux
        nl = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
        nx, ny, nz = nx / nl, ny / nl, nz / nl

        def pt(s, v, off):
            bx = p0[0] + (p1[0] - p0[0]) * s
            by = p0[1] + (p1[1] - p0[1]) * s
            tx = q0[0] + (q1[0] - q0[0]) * s
            ty = q0[1] + (q1[1] - q0[1]) * s
            return (bx + (tx - bx) * v + nx * off,
                    by + (ty - by) * v + ny * off,
                    z0 + h * v + nz * off)

        for k in range(ribs):
            s0, s1 = k / ribs, (k + 1) / ribs
            off = rib_out if k % 2 else 0.0
            mb.add([pt(s0, 0, off), pt(s1, 0, off), pt(s1, 1, off), pt(s0, 1, off)],
                   [(s0, 0), (s1, 0), (s1, 1), (s0, 1)])
            if off:  # laterales de la costilla
                mb.add([pt(s0, 0, 0), pt(s0, 0, off), pt(s0, 1, off), pt(s0, 1, 0)])
                mb.add([pt(s1, 0, off), pt(s1, 0, 0), pt(s1, 1, 0), pt(s1, 1, off)])


def build_speakers(mats):
    """Line arrays volados en una esquina del mirador: la torre es un anfiteatro.

    Mastil en la esquina de la losa, brazo en voladizo que sale por fuera del
    alero y montante donde cuelgan los dos racimos, que asi se recortan contra
    el cielo por encima de la cubierta, como en la foto.
    """
    mb = MeshBuilder()
    z_arm = Z_EAVE + 0.10
    arm = 1.55

    add_box(mb, (0, 0, (Z_DECK_TOP + z_arm) / 2), (0.18, 0.18, z_arm - Z_DECK_TOP))
    add_box(mb, (0, -arm / 2, z_arm - 0.09), (0.15, arm, 0.18))
    _tilted_box(mb, (0, -0.60, z_arm - 0.70), (0.10, 0.10, 1.45), math.radians(52))
    add_box(mb, (0, -1.45, z_arm + 1.05), (0.13, 0.13, 2.10))

    for sx in (-0.38, 0.38):
        z = z_arm + 1.88
        for k in range(7):
            hgt = 0.215
            z -= hgt + 0.016
            _tilted_box(mb, (sx, -1.45 + 0.030 * k, z), (0.62, 0.34, hgt),
                        math.radians(3.2 * k))

    obj = mb.to_object("Line_Array", mats["speaker"], merge=1e-4)
    obj.location = (-3.02, -3.02, 0.0)
    obj.rotation_euler = (0.0, 0.0, math.radians(-45.0))
    return [obj]


def _tilted_box(mb, center, size, pitch):
    cx, cy, cz = center
    sx, sy, sz = (s / 2.0 for s in size)
    ca, sa = math.cos(pitch), math.sin(pitch)
    pts = []
    for dz in (-sz, sz):
        for dy in (-sy, sy):
            pts.append((dy, dz))
    corners = []
    for dx in (-sx, sx):
        for dy, dz in [(-sy, -sz), (sy, -sz), (sy, sz), (-sy, sz)]:
            corners.append((cx + dx, cy + dy * ca - dz * sa, cz + dy * sa + dz * ca))
    lo, hi = corners[:4], corners[4:]
    for k in range(4):
        j = (k + 1) % 4
        mb.add([lo[k], lo[j], hi[j], hi[k]])
    mb.add(list(reversed(lo)))
    mb.add(hi)


def build_lamp(mats):
    """Luminaria del faro bajo el templete."""
    mb = MeshBuilder()
    add_cylinder(mb, (0, 0, Z_ENTAB - 0.92), 0.30, 0.30, 0.62, segments=20)
    return [mb.to_object("Farol", mats["lamp"], merge=1e-4, smooth=True)]


# --------------------------------------------------------------------------
def build_tower():
    mats = build_materials()
    objs = []
    for fn in (build_base, build_shaft, build_cornice, build_railing,
               build_pavilion, build_roof, build_speakers, build_lamp):
        objs.extend(o for o in fn(mats) if o is not None)

    root = bpy.data.objects.new("Faro_La_Guancha", None)
    bpy.context.collection.objects.link(root)
    for o in objs:
        o.parent = root
    return root, objs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--render", action="store_true")
    ap.add_argument("--samples", type=int, default=64)
    ap.add_argument("--res", type=int, default=1100)
    ap.add_argument("--out", default=os.path.join(ROOT, "exports"))
    args = ap.parse_args()

    bpy.ops.wm.read_factory_settings(use_empty=True)
    root, objs = build_tower()

    tris = sum(len(o.data.polygons) for o in objs if o.type == "MESH")
    verts = sum(len(o.data.vertices) for o in objs if o.type == "MESH")
    print(f"[guancha] {len(objs)} objetos, {verts} vertices, {tris} caras")
    print(f"[guancha] altura total = {Z_APEX + 1.0:.2f} m")

    os.makedirs(args.out, exist_ok=True)
    import render_views

    # el glb se exporta primero, con solo la geometria seleccionada: asi no
    # arrastra la camara ni el sol que se anaden justo despues
    for o in bpy.data.objects:
        o.select_set(o.type == "MESH")
    glb = os.path.join(args.out, "guancha.glb")
    bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB",
                              use_selection=True, export_apply=True)
    print("[guancha] glb   ->", glb, os.path.getsize(glb) // 1024, "KB")

    # el .blend se guarda ya montado: cielo, sol, camara, Cycles y las vistas
    # en Material Preview, para que se abra mostrando las texturas
    render_views.prepare_scene(args.samples, args.res, view="hero")
    blend = os.path.join(args.out, "guancha.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    print("[guancha] blend ->", blend)

    if args.render:
        render_views.render_all(args.out, args.samples, args.res, prepared=True)


if __name__ == "__main__":
    main()
