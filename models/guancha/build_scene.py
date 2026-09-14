"""
Escena completa de La Guancha: el faro dentro de su complejo.

Ensambla la torre de build.py sobre la explanada y anade el entorno de site.py
(tablado, darsena, muelles, kioscos, anfiteatro, playa y arbolado), exporta el
conjunto a glTF y saca las vistas.

    python3 models/guancha/build_scene.py --render
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import build            # noqa: E402
import entorno        # noqa: E402  (se llama asi para no chocar con el modulo 'site' de la stdlib)

ROOT = build.ROOT


# Vistas del conjunto: (posicion, objetivo, lente, ancho, alto)
SCENE_VIEWS = {
    # encuadre tipo dron, parecido al de la fotografia de referencia
    "conjunto":  ((-40.0, 68.0, 40.0), (18.0, -14.0, 6.0), 46.0, 1.0, 0.86),
    # desde la darsena, con las lanchas en primer termino
    "darsena":   ((44.0, -46.0, 9.5), (6.0, -12.0, 8.0), 52.0, 1.0, 0.75),
    # a pie de tablado, mirando hacia el faro
    "tablado":   ((56.0, -6.6, 3.4), (2.0, -2.0, 9.0), 34.0, 0.78, 1.0),
    # los kioscos con el faro detras
    "kioscos":   ((-16.0, 46.0, 12.0), (14.0, 6.0, 7.0), 46.0, 1.0, 0.72),
}


def build_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)

    mats = build.build_materials()
    mats.update(entorno.build_site_materials(
        build.material, lambda n: os.path.join(build.TEX, n)))

    tower_root, tower_objs = build.build_tower()
    tower_root.location = (0.0, 0.0, entorno.S["quay_z"])

    site_objs = entorno.build_site(mats)
    site_root = bpy.data.objects.new("Complejo_La_Guancha", None)
    bpy.context.collection.objects.link(site_root)
    for o in site_objs:
        o.parent = site_root

    return tower_objs + site_objs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--render", action="store_true")
    ap.add_argument("--samples", type=int, default=96)
    ap.add_argument("--res", type=int, default=1200)
    ap.add_argument("--views", default="")
    ap.add_argument("--out", default=os.path.join(ROOT, "exports"))
    args = ap.parse_args()

    objs = build_scene()
    meshes = [o for o in objs if o.type == "MESH"]
    print(f"[escena] {len(meshes)} objetos, "
          f"{sum(len(o.data.vertices) for o in meshes)} vertices, "
          f"{sum(len(o.data.polygons) for o in meshes)} caras")

    os.makedirs(args.out, exist_ok=True)
    import render_views
    render_views.VIEWS = SCENE_VIEWS

    # primero el glb, con solo la geometria: la camara y el sol llegan despues
    for o in bpy.data.objects:
        o.select_set(o.type == "MESH")
    glb = os.path.join(args.out, "guancha_complejo.glb")
    bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB",
                              use_selection=True, export_apply=True)
    print("[escena] glb ->", glb, os.path.getsize(glb) // 1024, "KB")

    # el .blend se guarda listo para abrir y renderizar
    render_views.prepare_scene(args.samples, args.res, with_site=False,
                               sun_elevation=34.0, sun_rotation=208.0,
                               exposure=-0.62, view="conjunto")
    blend = os.path.join(args.out, "guancha_complejo.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    print("[escena] blend ->", blend)

    if args.render:
        only = set(v for v in args.views.split(",") if v) or None
        render_views.render_all(args.out, args.samples, args.res, views=only,
                                with_site=False, sun_elevation=34.0,
                                sun_rotation=208.0, prefix="complejo",
                                exposure=-0.62, prepared=True)


if __name__ == "__main__":
    main()
