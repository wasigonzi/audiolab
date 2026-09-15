"""
Utilidades de construccion de malla sobre bpy.

Todo se acumula en un MeshBuilder (poligonos con sus UV) y al final se vuelca a
un objeto de Blender. Se evitan los operadores de Blender (bpy.ops) siempre que
es posible porque en modo headless dependen del contexto y son fragiles.
"""

import math

import bmesh
import bpy


# --------------------------------------------------------------------------
class MeshBuilder:
    """Acumula poligonos sueltos y los convierte en un objeto."""

    def __init__(self):
        self.verts = []
        self.faces = []
        self.uvs = []

    def add(self, pts, uvs=None):
        if len(pts) < 3:
            return
        i0 = len(self.verts)
        self.verts.extend(tuple(p) for p in pts)
        self.faces.append(tuple(range(i0, i0 + len(pts))))
        self.uvs.append(list(uvs) if uvs else [(0.0, 0.0)] * len(pts))

    def extend(self, other):
        off = len(self.verts)
        self.verts.extend(other.verts)
        self.faces.extend(tuple(i + off for i in f) for f in other.faces)
        self.uvs.extend(other.uvs)

    @property
    def empty(self):
        return not self.faces

    def to_object(self, name, material=None, merge=0.0, smooth=False, parent=None):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(self.verts, [], self.faces)
        mesh.update()

        uv_layer = mesh.uv_layers.new(name="UVMap")
        loop = 0
        for face_uvs in self.uvs:
            for uv in face_uvs:
                uv_layer.data[loop].uv = uv
                loop += 1

        if material is not None:
            mesh.materials.append(material)

        obj = bpy.data.objects.new(name, mesh)
        bpy.context.collection.objects.link(obj)

        if merge > 0.0:
            bm = bmesh.new()
            bm.from_mesh(mesh)
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=merge)
            bm.to_mesh(mesh)
            bm.free()
            mesh.update()

        if smooth:
            for poly in mesh.polygons:
                poly.use_smooth = True

        if parent is not None:
            obj.parent = parent
        return obj


# --------------------------------------------------------------------------
# Primitivas
# --------------------------------------------------------------------------
def frustum_face_mapper(w0, w1, h, z0, face):
    """Devuelve f(u, v) -> punto 3D sobre una cara de un tronco de piramide.

    `face` 0..3 recorre -Y, +X, +Y, -X. `u` avanza en sentido antihorario visto
    desde arriba y `v` va de la base (0) al remate (1).
    """
    def f(u, v):
        w = w0 + (w1 - w0) * v
        a = w / 2.0
        s = (u - 0.5) * w
        z = z0 + h * v
        if face == 0:
            return (s, -a, z)
        if face == 1:
            return (a, s, z)
        if face == 2:
            return (-s, a, z)
        return (-a, -s, z)
    return f


def add_frustum(mb, w0, w1, h, z0, uv_faces=None, cap_top=False, cap_bottom=False,
                center=(0.0, 0.0)):
    """Tronco de piramide de base cuadrada. `uv_faces[i]` = (u0, u1) en el atlas."""
    cx, cy = center

    def off(p):
        return (p[0] + cx, p[1] + cy, p[2])

    for face in range(4):
        f = frustum_face_mapper(w0, w1, h, z0, face)
        u0, u1 = uv_faces[face] if uv_faces else (0.0, 1.0)
        mb.add(
            [off(f(0, 0)), off(f(1, 0)), off(f(1, 1)), off(f(0, 1))],
            [(u0, 0), (u1, 0), (u1, 1), (u0, 1)],
        )
    a0, a1 = w0 / 2.0, w1 / 2.0
    if cap_bottom:
        mb.add([off((-a0, -a0, z0)), off((-a0, a0, z0)),
                off((a0, a0, z0)), off((a0, -a0, z0))])
    if cap_top:
        z1 = z0 + h
        mb.add([off((-a1, -a1, z1)), off((a1, -a1, z1)),
                off((a1, a1, z1)), off((-a1, a1, z1))])


def add_box(mb, center, size, uv_scale=1.0):
    """Caja alineada a los ejes: center = (x, y, z) del centro, size = (sx, sy, sz)."""
    cx, cy, cz = center
    sx, sy, sz = (s / 2.0 for s in size)
    v = [
        (cx - sx, cy - sy, cz - sz), (cx + sx, cy - sy, cz - sz),
        (cx + sx, cy + sy, cz - sz), (cx - sx, cy + sy, cz - sz),
        (cx - sx, cy - sy, cz + sz), (cx + sx, cy - sy, cz + sz),
        (cx + sx, cy + sy, cz + sz), (cx - sx, cy + sy, cz + sz),
    ]
    quads = [
        (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7),
        (4, 5, 6, 7), (3, 2, 1, 0),
    ]
    uv = [(0, 0), (uv_scale, 0), (uv_scale, uv_scale), (0, uv_scale)]
    for q in quads:
        mb.add([v[i] for i in q], uv)


def add_cylinder(mb, center, r0, r1, h, segments=24, caps=True, axis="Z"):
    """Cilindro o tronco de cono. `axis` elige la direccion del eje: util para
    ruedas y barras horizontales sin tener que rotar despues."""
    cx, cy, cz = center

    def at(r, t, i):
        a = 2 * math.pi * i / segments
        u, v = r * math.cos(a), r * math.sin(a)
        if axis == "X":
            return (cx + t, cy + u, cz + v)
        if axis == "Y":
            return (cx + u, cy + t, cz + v)
        return (cx + u, cy + v, cz + t)

    ring0 = [at(r0, 0.0, i) for i in range(segments)]
    ring1 = [at(r1, h, i) for i in range(segments)]
    for i in range(segments):
        j = (i + 1) % segments
        u0, u1 = i / segments, (i + 1) / segments
        mb.add([ring0[i], ring0[j], ring1[j], ring1[i]],
               [(u0, 0), (u1, 0), (u1, 1), (u0, 1)])
    if caps:
        mb.add(list(reversed(ring0)))
        mb.add(ring1)


def add_pyramid(mb, w_base, w_top, h, z0, center=(0.0, 0.0), depth_base=None,
                depth_top=None):
    """Cubierta a cuatro aguas. w_top = 0 da una piramide pura.

    `center` desplaza la pieza en planta; `depth_*` permiten una planta
    rectangular (por defecto, cuadrada).
    """
    cx, cy = center
    a, b = w_base / 2.0, w_top / 2.0
    c = (depth_base if depth_base is not None else w_base) / 2.0
    dtop = (depth_top if depth_top is not None else w_top) / 2.0
    z1 = z0 + h
    base = [(cx - a, cy - c, z0), (cx + a, cy - c, z0),
            (cx + a, cy + c, z0), (cx - a, cy + c, z0)]
    top = [(cx - b, cy - dtop, z1), (cx + b, cy - dtop, z1),
           (cx + b, cy + dtop, z1), (cx - b, cy + dtop, z1)]
    for i in range(4):
        j = (i + 1) % 4
        if w_top <= 1e-6:
            mb.add([base[i], base[j], top[0]],
                   [(0, 0), (1, 0), (0.5, 1)])
        else:
            mb.add([base[i], base[j], top[j], top[i]],
                   [(0, 0), (1, 0), (1, 1), (0, 1)])
    if w_top > 1e-6:
        mb.add(top)


def add_ring(mb, primitive, count, radius, z, **kwargs):
    """Repite una primitiva en las `count` posiciones de un anillo cuadrado."""
    for i in range(count):
        a = 2 * math.pi * i / count
        primitive(mb, (radius * math.cos(a), radius * math.sin(a), z), **kwargs)


# --------------------------------------------------------------------------
# Muros con huecos (puertas y ventanas) sin recurrir a booleanas
# --------------------------------------------------------------------------
def _span_at(op, y):
    """Semiancho del hueco a la altura y; contempla el arco de medio punto."""
    x0, x1 = op["x0"], op["x1"]
    if not op.get("arch"):
        return x0, x1
    r = (x1 - x0) / 2.0
    y_spring = op["y1"] - r
    if y <= y_spring:
        return x0, x1
    dy = min(y - y_spring, r)
    hw = math.sqrt(max(r * r - dy * dy, 0.0))
    cx = (x0 + x1) / 2.0
    return cx - hw, cx + hw


def wall_panels(width, height, openings, arch_segments=14):
    """Descompone un muro rectangular con huecos en cuadrilateros.

    Devuelve (paneles, contornos): los paneles son el muro macizo y los
    contornos son el perimetro escalonado de cada hueco, listo para extruir
    como mocheta. Trabaja en coordenadas (u, v) normalizadas del muro.
    """
    cuts = {0.0, height}
    for op in openings:
        cuts.add(op["y0"])
        cuts.add(op["y1"])
        if op.get("arch"):
            r = (op["x1"] - op["x0"]) / 2.0
            y_spring = op["y1"] - r
            cuts.add(y_spring)
            for k in range(1, arch_segments + 1):
                cuts.add(y_spring + r * k / arch_segments)
    levels = sorted(c for c in cuts if -1e-9 <= c <= height + 1e-9)

    panels = []
    bands = {id(op): [] for op in openings}
    for a, b in zip(levels, levels[1:]):
        if b - a < 1e-7:
            continue
        spans = []
        for op in openings:
            if op["y0"] - 1e-9 <= a and b <= op["y1"] + 1e-9:
                # el ancho se toma en la base de la franja: asi el muro y la
                # mocheta comparten exactamente el mismo escalonado
                s0, s1 = _span_at(op, a)
                if s1 - s0 > 1e-6:
                    spans.append((s0, s1))
                    bands[id(op)].append((a, b, s0, s1))
        spans.sort()
        x = 0.0
        for s0, s1 in spans:
            if s0 - x > 1e-6:
                panels.append((x, s0, a, b))
            x = max(x, s1)
        if width - x > 1e-6:
            panels.append((x, width, a, b))

    outlines = []
    for op in openings:
        bs = bands[id(op)]
        if not bs:
            continue
        left, right = [], []
        for k, (a, b, s0, s1) in enumerate(bs):
            if k == 0:
                left.append((s0, a))
                right.append((s1, a))
            else:
                pa, pb, ps0, ps1 = bs[k - 1]
                if abs(ps0 - s0) > 1e-9:
                    left.append((s0, a))
                    right.append((s1, a))
            left.append((s0, b))
            right.append((s1, b))
        outlines.append((op, left + list(reversed(right))))
    return panels, outlines


# --------------------------------------------------------------------------
# Muros planos (planta rectangular, no solo cuadrada)
# --------------------------------------------------------------------------
def add_flat_wall(wall_mb, recess_mb, p0, p1, z0, height, openings=(),
                  tile=2.5, reveal=0.22, uv_origin=0.0):
    """Muro vertical entre dos puntos de la planta, con sus huecos y mochetas.

    Caminando de p0 a p1 el exterior queda a la derecha. Los huecos se dan en
    coordenadas del muro: x a lo largo desde p0, y en altura desde z0. `tile`
    son los metros que ocupa una repeticion de la textura.
    """
    (x0, y0), (x1, y1) = p0, p1
    dx, dy = x1 - x0, y1 - y0
    length = math.hypot(dx, dy)
    if length < 1e-6:
        return 0.0
    ux, uy = dx / length, dy / length
    inx, iny = -uy, ux                      # normal hacia el interior

    def f(x, y):
        return (x0 + ux * x, y0 + uy * x, z0 + y)

    def uv(x, y):
        return ((uv_origin + x) / tile, y / tile)

    openings = list(openings)
    panels, outlines = wall_panels(length, height, openings)
    for a, b, c, d in panels:
        wall_mb.add([f(a, c), f(b, c), f(b, d), f(a, d)],
                    [uv(a, c), uv(b, c), uv(b, d), uv(a, d)])

    for op, outline in outlines:
        depth = op.get("depth", reveal)
        pts = [f(x, y) for x, y in outline]
        inner = [(p[0] + inx * depth, p[1] + iny * depth, p[2]) for p in pts]
        for k in range(len(pts)):
            j = (k + 1) % len(pts)
            recess_mb.add([pts[k], pts[j], inner[j], inner[k]],
                          [(0, 0), (1, 0), (1, 1), (0, 1)])
        if op.get("cap", True):              # fondo del hueco
            back = [f(op["x0"], op["y0"]), f(op["x1"], op["y0"]),
                    f(op["x1"], op["y1"]), f(op["x0"], op["y1"])]
            recess_mb.add([(p[0] + inx * depth, p[1] + iny * depth, p[2])
                           for p in back],
                          [(0, 0), (1, 0), (1, 1), (0, 1)])
    return length


def add_box_rot(mb, center, size, rx=0.0, ry=0.0, rz=0.0):
    """Caja con rotacion en radianes, aplicada en el orden X, Y, Z."""
    cx, cy, cz = center
    sx, sy, sz = (s / 2.0 for s in size)
    cosx, sinx = math.cos(rx), math.sin(rx)
    cosy, siny = math.cos(ry), math.sin(ry)
    cosz, sinz = math.cos(rz), math.sin(rz)

    def place(px, py, pz):
        py, pz = py * cosx - pz * sinx, py * sinx + pz * cosx
        px, pz = px * cosy + pz * siny, -px * siny + pz * cosy
        px, py = px * cosz - py * sinz, px * sinz + py * cosz
        return (cx + px, cy + py, cz + pz)

    v = [place(x, y, z)
         for x, y, z in ((-sx, -sy, -sz), (sx, -sy, -sz), (sx, sy, -sz), (-sx, sy, -sz),
                         (-sx, -sy, sz), (sx, -sy, sz), (sx, sy, sz), (-sx, sy, sz))]
    uvq = [(0, 0), (1, 0), (1, 1), (0, 1)]
    for q in ((0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7),
              (4, 5, 6, 7), (3, 2, 1, 0)):
        mb.add([v[i] for i in q], uvq)


# --------------------------------------------------------------------------
# Instanciado: duplicados que comparten malla
# --------------------------------------------------------------------------
def link_copy(obj, location=(0.0, 0.0, 0.0), rotation_z=0.0, name=None,
              parent=None):
    """Duplicado enlazado. Comparte los datos de malla, asi el archivo no crece
    y el glTF exporta una sola geometria referenciada varias veces."""
    dup = obj.copy()
    dup.location = location
    dup.rotation_euler = (0.0, 0.0, rotation_z)
    if name:
        dup.name = name
    if parent is not None:
        dup.parent = parent
    bpy.context.collection.objects.link(dup)
    return dup


def instance_group(objs, location=(0.0, 0.0, 0.0), rotation_z=0.0, parent=None):
    """Replica un conjunto de objetos conservando sus posiciones relativas."""
    import mathutils
    rot = mathutils.Matrix.Rotation(rotation_z, 4, "Z")
    base = mathutils.Matrix.Translation(location) @ rot
    out = []
    for o in objs:
        dup = o.copy()
        dup.matrix_world = base @ o.matrix_world
        if parent is not None:
            dup.parent = parent
            dup.matrix_parent_inverse = parent.matrix_world.inverted()
        bpy.context.collection.objects.link(dup)
        out.append(dup)
    return out
