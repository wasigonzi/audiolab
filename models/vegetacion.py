"""
Arbolado de bajo poligonaje, compartido por las escenas.

Las copas se arman con troncos de piramide poco estrechados y mas anchos que
altos; si se afilan, el arbol acaba pareciendo un abeto.
"""

import math

from meshlib import MeshBuilder, add_cylinder, add_frustum  # noqa: F401


def palm(trunks, fronds, x, y, z, height, rng):
    lean = rng.uniform(0.04, 0.16)
    lean_a = rng.uniform(0, math.tau)
    segs = 7
    prev = None
    for k in range(segs + 1):
        t = k / segs
        r = 0.30 * (1 - 0.45 * t)
        dx = math.cos(lean_a) * lean * height * t * t
        dy = math.sin(lean_a) * lean * height * t * t
        ring = [(x + dx + r * math.cos(a), y + dy + r * math.sin(a), z + height * t)
                for a in [math.tau * i / 8 for i in range(8)]]
        if prev:
            for i in range(8):
                j = (i + 1) % 8
                trunks.add([prev[i], prev[j], ring[j], ring[i]],
                           [(0, t), (1, t), (1, t), (0, t)])
        prev = ring

    tx = x + math.cos(lean_a) * lean * height
    ty = y + math.sin(lean_a) * lean * height
    tz = z + height
    n = rng.randint(8, 11)
    for i in range(n):
        a = math.tau * i / n + rng.uniform(-0.15, 0.15)
        _frond(fronds, tx, ty, tz, a, rng.uniform(2.6, 3.7), rng)


def _frond(mb, x, y, z, angle, length, rng):
    """Hoja de palma: tira que se estrecha y cae describiendo una parabola."""
    ca, sa = math.cos(angle), math.sin(angle)
    segs = 6
    droop = rng.uniform(0.45, 0.95)
    pts = []
    for k in range(segs + 1):
        t = k / segs
        w = 0.42 * math.sin(math.pi * min(t * 1.25, 1.0)) * (1 - 0.35 * t)
        pts.append((x + ca * length * t, y + sa * length * t,
                    z + 0.35 * math.sin(math.pi * t * 0.6) - droop * t * t * length * 0.35,
                    w))
    for (x0, y0, z0, w0), (x1, y1, z1, w1) in zip(pts, pts[1:]):
        mb.add([(x0 - sa * w0, y0 + ca * w0, z0), (x1 - sa * w1, y1 + ca * w1, z1),
                (x1 + sa * w1, y1 - ca * w1, z1), (x0 + sa * w0, y0 - ca * w0, z0)],
               [(0, 0), (1, 0), (1, 1), (0, 1)])


def round_tree(mb, x, y, z, height, rng):
    """Arbol de copa redondeada, de baja resolucion.

    Los volumenes van poco estrechados y mas anchos que altos; si se afilan,
    el arbol acaba pareciendo un abeto, que no pinta nada en el Caribe.
    """
    add_cylinder(mb, (x, y, z), 0.14, 0.11, height * 0.48, segments=6)
    cz = z + height * 0.44
    for _ in range(rng.randint(3, 5)):
        r = rng.uniform(0.85, 1.40)
        ox, oy = rng.uniform(-0.75, 0.75), rng.uniform(-0.75, 0.75)
        oz = rng.uniform(0.0, height * 0.30)
        add_frustum(mb, r * 2.1, r * 1.5, r * 1.05, cz + oz,
                    cap_top=True, cap_bottom=True, center=(x + ox, y + oy))


def flamboyan(trunk_mb, leaf_mb, flower_mb, x, y, z, height, rng):
    """Flamboyan (Delonix regia): copa ancha y aparasolada, de flor encendida.

    Es el arbol que da nombre al conjunto. Lo caracteristico no es la altura
    sino la proporcion: la copa mide dos o tres veces el alto del tronco y se
    extiende casi plana, como un parasol.
    """
    lean_a = rng.uniform(0, math.tau)
    lean = rng.uniform(0.05, 0.13)
    bole = height * 0.42
    add_cylinder(trunk_mb, (x, y, z), 0.34, 0.24, bole, segments=8)

    tips = []
    for _ in range(rng.randint(6, 8)):        # ramas principales, muy abiertas
        a = rng.uniform(0, math.tau)
        reach = height * rng.uniform(0.34, 0.52)
        tx = x + math.cos(a) * reach + math.cos(lean_a) * lean * height
        ty = y + math.sin(a) * reach + math.sin(lean_a) * lean * height
        tz = z + bole + height * rng.uniform(0.14, 0.26)
        add_cylinder(trunk_mb, (x, y, z + bole * 0.85), 0.17, 0.09,
                     math.dist((x, y, z + bole), (tx, ty, tz)), segments=6)
        tips.append((tx, ty, tz))

    # la masa foliar se solapa mucho: si los volumenes quedan sueltos, el arbol
    # lee como un racimo de setas en vez de como una copa continua
    for tx, ty, tz in tips:
        for _ in range(rng.randint(4, 6)):
            r = rng.uniform(1.8, 3.1)
            ox, oy = rng.uniform(-1.9, 1.9), rng.uniform(-1.9, 1.9)
            oz = rng.uniform(-0.45, 0.45)
            add_frustum(leaf_mb, r * 2.5, r * 2.0, r * 0.62, tz + oz,
                        cap_top=True, cap_bottom=True, center=(tx + ox, ty + oy))
            # la flor no corona el arbol: salpica la copa entera
            if rng.random() < 0.8:
                fr = r * rng.uniform(0.45, 0.85)
                add_frustum(flower_mb, fr * 2.1, fr * 1.5, fr * 0.42,
                            tz + oz + r * 0.50, cap_top=True, cap_bottom=True,
                            center=(tx + ox + rng.uniform(-1.4, 1.4),
                                    ty + oy + rng.uniform(-1.4, 1.4)))
