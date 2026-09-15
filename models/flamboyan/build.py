"""
Bloque de viviendas del tipo del Condominio El Flamboyan (Puerto Rico).

Walk-up de cuatro plantas en hormigon visto pintado, con los rasgos que definen
esta arquitectura en el Caribe: bandas turquesa en el canto de cada forjado,
celosias de bloque ornamental, ventanas de persiana ("Miami"), balcones
recogidos con reja y la escalera exterior abierta en el centro de la fachada.

Las piezas que se repiten -celosia, persiana, reja- se construyen una sola vez
y se instancian: los duplicados comparten malla, de modo que el archivo no
crece y el glTF exporta una unica geometria referenciada muchas veces.

Sistema local: el bloque va centrado en el origen, el eje largo en X y la
fachada principal mirando a -Y.
"""

import math
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))       # models/
ROOT = os.path.dirname(os.path.dirname(HERE))

from matlib import material  # noqa: E402
from meshlib import (  # noqa: E402
    MeshBuilder, add_box, add_box_rot, add_cylinder, add_flat_wall, link_copy,
)

TEX = os.path.join(ROOT, "textures")

# --------------------------------------------------------------------------
B = dict(
    bays=7, bay_w=5.20, depth=10.60,
    floor_h=2.72, floors=4,
    parapet=0.92, coping=0.12,
    band_h=0.95,            # banda turquesa; hace tambien de pretil del balcon
    slab=0.24,              # canto de forjado que se ve en fachada
    stair_bay=2,
    lattice_bays=(1, 3),
)
LENGTH = B["bays"] * B["bay_w"]
DEPTH = B["depth"]
TOP = B["floors"] * B["floor_h"]


def _tex(name):
    return os.path.join(TEX, name)


def build_materials():
    return dict(
        wall=material("Hormigon_Pintado", (0.62, 0.63, 0.60), 0.86,
                      texture=_tex("painted_concrete.png")),
        band=material("Banda_Turquesa", (0.06, 0.16, 0.20), 0.80,
                      texture=_tex("painted_teal.png")),
        lattice=material("Celosia_Bloque", (0.855, 0.859, 0.835), 0.82),
        louver=material("Persiana", (0.900, 0.906, 0.890), 0.55),
        rail=material("Reja", (0.052, 0.055, 0.058), 0.46, metallic=0.55),
        shade=material("Interior", (0.030, 0.030, 0.032), 0.94),
        soffit=material("Hormigon_Visto", (0.520, 0.522, 0.505), 0.90),
        door=material("Puerta", (0.360, 0.238, 0.150), 0.60),
        lamp=material("Proyector", (0.115, 0.118, 0.120), 0.40, metallic=0.6),
    )


# --------------------------------------------------------------------------
# Piezas repetidas
# --------------------------------------------------------------------------
def make_lattice_panel(mats, w, h, cell=0.38, name="Celosia"):
    """Celosia de bloque ornamental.

    Cada celda lleva un aspa; las aspas contiguas se encuentran en las esquinas
    y el conjunto lee como la reticula de rombos de estos bloques. Construir por
    celda evita tener que recortar listones diagonales contra el marco.
    """
    mb = MeshBuilder()
    nx = max(1, round(w / cell))
    nz = max(1, round(h / cell))
    cw, ch = w / nx, h / nz
    diag = math.hypot(cw, ch)
    ang = math.atan2(ch, cw)
    for i in range(nx):
        for k in range(nz):
            cx = -w / 2 + (i + 0.5) * cw
            cz = -h / 2 + (k + 0.5) * ch
            add_box_rot(mb, (cx, 0.0, cz), (diag, 0.11, 0.055), ry=-ang)
            add_box_rot(mb, (cx, 0.0, cz), (diag, 0.11, 0.055), ry=ang)
    for cz, sz in ((-h / 2, 0.10), (h / 2, 0.10)):      # marco
        add_box(mb, (0.0, 0.0, cz), (w + 0.2, 0.13, sz))
    for cx in (-w / 2, w / 2):
        add_box(mb, (cx, 0.0, 0.0), (0.10, 0.13, h))
    return mb.to_object(name, mats["lattice"], merge=1e-4)


def make_louver_panel(mats, w, h, slats=8, name="Persiana"):
    """Ventana de persiana: lamas horizontales inclinadas."""
    mb = MeshBuilder()
    pitch = h / slats
    for k in range(slats):
        cz = -h / 2 + (k + 0.5) * pitch
        add_box_rot(mb, (0.0, 0.0, cz), (w, 0.085, pitch * 0.95),
                    rx=math.radians(-24))
    for cx in (-w / 2, w / 2):                      # solo los montantes
        add_box(mb, (cx, 0.0, 0.0), (0.05, 0.07, h + 0.08))
    return mb.to_object(name, mats["louver"], merge=1e-4)


def make_grille(mats, w, h, bars=11, name="Reja"):
    """Reja de balcon: barrotes verticales entre dos travesanos."""
    mb = MeshBuilder()
    for k in range(bars):
        cx = -w / 2 + (k + 0.5) * w / bars
        add_box(mb, (cx, 0.0, 0.0), (0.035, 0.035, h))
    for cz in (-h / 2 + 0.05, 0.0, h / 2 - 0.05):
        add_box(mb, (0.0, 0.0, cz), (w, 0.045, 0.045))
    return mb.to_object(name, mats["rail"], merge=1e-4)


# --------------------------------------------------------------------------
# Fachadas
# --------------------------------------------------------------------------
def _bay_openings(floor, bay):
    """Huecos de una crujia, en coordenadas del muro (x a lo largo, y en alto)."""
    z0 = floor * B["floor_h"]
    bx = bay * B["bay_w"]
    top = z0 + B["floor_h"] - B["slab"]
    sill = z0 + B["band_h"]
    ops = []
    if bay == B["stair_bay"]:
        ops.append(dict(x0=bx + 0.32, x1=bx + B["bay_w"] - 0.32, y0=sill,
                        y1=top, depth=0.34, cap=False, kind="escalera"))
    elif bay in B["lattice_bays"]:
        ops.append(dict(x0=bx + 0.42, x1=bx + B["bay_w"] - 0.42, y0=sill,
                        y1=top, depth=0.26, cap=False, kind="celosia"))
    else:
        ops.append(dict(x0=bx + 0.38, x1=bx + 2.92, y0=sill, y1=top,
                        depth=1.15, kind="balcon"))
        ops.append(dict(x0=bx + 3.40, x1=bx + 4.82, y0=z0 + 1.24,
                        y1=z0 + 2.34, depth=0.22, kind="ventana"))
    return ops


def build_facades(mats):
    """Los cuatro paramentos, con sus huecos, mochetas y bandas de forjado."""
    wall, recess, band = MeshBuilder(), MeshBuilder(), MeshBuilder()
    hx, hy = LENGTH / 2, DEPTH / 2

    front_ops = []
    for f in range(B["floors"]):
        for b in range(B["bays"]):
            front_ops.extend(_bay_openings(f, b))
    add_flat_wall(wall, recess, (-hx, -hy), (hx, -hy), 0.0, TOP,
                  openings=front_ops, tile=2.6)

    # fachada posterior: dos ventanas de persiana por crujia y planta
    back_ops = []
    for f in range(B["floors"]):
        z0 = f * B["floor_h"]
        for b in range(B["bays"]):
            bx = b * B["bay_w"]
            for off in (0.75, 3.05):
                back_ops.append(dict(x0=bx + off, x1=bx + off + 1.42,
                                     y0=z0 + 1.24, y1=z0 + 2.34, depth=0.22,
                                     kind="ventana"))
    add_flat_wall(wall, recess, (hx, hy), (-hx, hy), 0.0, TOP,
                  openings=back_ops, tile=2.6)

    # testeros: practicamente ciegos, con un respiradero por planta
    for sign, (p0, p1) in ((-1, ((-hx, hy), (-hx, -hy))),
                           (1, ((hx, -hy), (hx, hy)))):
        ops = [dict(x0=DEPTH / 2 - 0.55, x1=DEPTH / 2 + 0.55,
                    y0=f * B["floor_h"] + 1.75, y1=f * B["floor_h"] + 2.30,
                    depth=0.20, kind="respiradero")
               for f in range(B["floors"])]
        add_flat_wall(wall, recess, p0, p1, 0.0, TOP, openings=ops, tile=2.6)

    # banda turquesa en el canto de cada forjado, ligeramente volada
    for f in range(B["floors"]):
        z0 = f * B["floor_h"]
        add_box(band, (0.0, 0.0, z0 + B["band_h"] / 2 - 0.10),
                (LENGTH + 0.14, DEPTH + 0.14, B["band_h"]), uv_scale=2.2)

    return [wall.to_object("Muros", mats["wall"], merge=1e-4),
            recess.to_object("Mochetas", mats["shade"], merge=1e-4),
            band.to_object("Bandas", mats["band"], merge=1e-4)]


def build_openings_fittings(mats):
    """Coloca celosias, persianas y rejas instanciando una sola malla de cada."""
    hy = DEPTH / 2
    bw = B["bay_w"]
    lat_w = bw - 0.84
    lat_h = B["floor_h"] - B["band_h"] - B["slab"]
    bal_w, win_w, win_h = 2.54, 1.42, 1.10

    lattice = make_lattice_panel(mats, lat_w, lat_h)
    louver = make_louver_panel(mats, win_w, win_h)
    grille = make_grille(mats, bal_w, lat_h)
    masters = [lattice, louver, grille]
    placed = []

    for f in range(B["floors"]):
        z0 = f * B["floor_h"]
        zmid = z0 + B["band_h"] + lat_h / 2
        for b in range(B["bays"]):
            cx = -LENGTH / 2 + b * bw + bw / 2
            if b == B["stair_bay"]:
                continue
            if b in B["lattice_bays"]:
                placed.append(link_copy(lattice, (cx, -hy + 0.13, zmid)))
                continue
            placed.append(link_copy(grille, (-LENGTH / 2 + b * bw + 1.65,
                                             -hy + 0.09, zmid)))
            placed.append(link_copy(louver, (-LENGTH / 2 + b * bw + 4.11,
                                             -hy + 0.13, z0 + 1.79)))
            for off in (0.75, 3.05):
                placed.append(link_copy(
                    louver, (-LENGTH / 2 + b * bw + off + win_w / 2,
                             hy - 0.13, z0 + 1.79), rotation_z=math.pi))

    # las mallas maestras se quedan fuera de la vista: solo aportan geometria
    for m in masters:
        m.hide_render = True
        m.hide_viewport = True
    return masters + placed


# --------------------------------------------------------------------------
# Escalera exterior
# --------------------------------------------------------------------------
def build_stair(mats):
    """Escalera abierta de ida y vuelta, con mesetas y barandilla."""
    steps, rail = MeshBuilder(), MeshBuilder()
    bw = B["bay_w"]
    cx = -LENGTH / 2 + B["stair_bay"] * bw + bw / 2
    y_front = -DEPTH / 2 + 0.40
    n = 8
    run, half = 0.285, B["floor_h"] / 2
    rise = half / n
    flight_w = 1.28
    xa, xb = cx - 0.72, cx + 0.72          # ejes de los dos tiros

    for f in range(B["floors"]):
        z0 = f * B["floor_h"]
        for k in range(n):                  # tiro de ida, hacia el fondo
            add_box(steps, (xa, y_front + (k + 0.5) * run, z0 + (k + 0.5) * rise),
                    (flight_w, run, rise * 1.9))
        y_back = y_front + n * run
        add_box(steps, (cx, y_back + 0.62, z0 + half - rise / 2),
                (flight_w * 2 + 0.16, 1.30, 0.22))          # meseta intermedia
        for k in range(n):                  # tiro de vuelta, hacia la fachada
            add_box(steps, (xb, y_back - (k + 0.5) * run,
                            z0 + half + (k + 0.5) * rise),
                    (flight_w, run, rise * 1.9))
        add_box(steps, (cx, y_front - 0.30, z0 + B["floor_h"] - 0.11),
                (flight_w * 2 + 0.16, 1.40, 0.22))          # rellano de planta

        for k in range(0, n + 1, 2):        # barandilla del hueco central
            add_box(rail, (cx, y_front + k * run, z0 + k * rise + 0.55),
                    (0.05, 0.05, 1.10))
        add_box(rail, (cx, y_front + n * run / 2, z0 + half / 2 + 1.05),
                (0.06, n * run, 0.06))

    return [steps.to_object("Escalera", mats["soffit"], merge=1e-4),
            rail.to_object("Barandilla_Escalera", mats["rail"], merge=1e-4)]


# --------------------------------------------------------------------------
# Cubierta
# --------------------------------------------------------------------------
def build_roof(mats):
    slab, coping, kit = MeshBuilder(), MeshBuilder(), MeshBuilder()
    hx, hy = LENGTH / 2, DEPTH / 2
    add_box(slab, (0.0, 0.0, TOP + 0.12), (LENGTH, DEPTH, 0.24), uv_scale=8.0)

    p = B["parapet"]
    for cx, cy, sx, sy in ((0, -hy, LENGTH, 0.22), (0, hy, LENGTH, 0.22),
                           (-hx, 0, 0.22, DEPTH), (hx, 0, 0.22, DEPTH)):
        add_box(slab, (cx, cy, TOP + 0.24 + p / 2), (sx, sy, p), uv_scale=5.0)
        add_box(coping, (cx, cy, TOP + 0.24 + p + 0.06),
                (sx + 0.16, sy + 0.16, B["coping"]))

    for sx in (-1, 1):                       # proyectores en las esquinas
        x, y = sx * (hx - 0.6), -hy
        add_box(kit, (x, y - 0.30, TOP + 0.24 + p + 0.42), (0.52, 0.26, 0.30))
        add_cylinder(kit, (x, y, TOP + 0.24 + p), 0.05, 0.05, 0.45, segments=6)
    for k in range(3):                       # ventilaciones
        add_cylinder(kit, (-6.0 + k * 6.0, 1.8, TOP + 0.24), 0.14, 0.14, 0.75,
                     segments=8)

    return [slab.to_object("Cubierta", mats["wall"], merge=1e-4),
            coping.to_object("Albardilla", mats["soffit"], merge=1e-4),
            kit.to_object("Cubierta_Equipos", mats["lamp"], merge=1e-4)]


# --------------------------------------------------------------------------
def build_block(mats, name="Bloque"):
    """Devuelve el vacio raiz y los objetos visibles del bloque.

    Las mallas maestras de celosia, persiana y reja quedan ocultas y fuera de
    la lista: solo existen para que los duplicados enlazados tengan de donde
    colgar su geometria.
    """
    objs = []
    for fn in (build_facades, build_openings_fittings, build_stair, build_roof):
        objs.extend(o for o in fn(mats) if o is not None)
    root = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(root)
    for o in objs:
        if o.parent is None:
            o.parent = root
    # sin esta actualizacion, matrix_world de los duplicados sigue sin reflejar
    # el emparentado y quien los instancie los colocara todos en el origen
    bpy.context.view_layer.update()
    return root, [o for o in objs if not o.hide_render]
