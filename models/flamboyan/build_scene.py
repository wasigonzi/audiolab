"""
Escena del Condominio El Flamboyan: cuatro bloques dentro de su parcela.

    python3 models/flamboyan/build_scene.py --render
"""

import argparse
import math
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))
ROOT = os.path.dirname(os.path.dirname(HERE))

import build as blk       # noqa: E402
import entorno            # noqa: E402
from meshlib import instance_group  # noqa: E402


SCENE_VIEWS = {
    # a pie de calle desde la entrada, como la fotografia de referencia
    "entrada":  ((-3.4, -13.0, 2.65), (7.0, 20.0, 6.2), 17.0, 1.0, 0.52),
    # panoramica del conjunto
    "conjunto": ((-52.0, -40.0, 38.0), (-4.0, 30.0, 6.0), 42.0, 1.0, 0.72),
    # un bloque de cerca, con la escalera y las celosias
    "bloque":   ((-9.0, 2.0, 6.5), (15.0, 26.0, 6.0), 38.0, 1.0, 0.70),
    # el aparcamiento y la verja
    "patio":    ((-26.0, -13.0, 4.0), (-26.0, 20.0, 4.5), 28.0, 1.0, 0.64),
}


def build_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)

    mats = blk.build_materials()
    mats.update(entorno.build_site_materials())

    # se construye un bloque y se replica: los duplicados comparten malla
    master_root, master_objs = blk.build_block(mats, name="Bloque_Maestro")

    blocks = []
    for i, ((x, y), rot) in enumerate(entorno.S["blocks"]):
        # el vacio solo agrupa: la colocacion la hace ya instance_group, en
        # coordenadas de mundo. Si ademas se transforma el padre, el bloque
        # acaba desplazado y girado dos veces.
        root = bpy.data.objects.new(f"Bloque_{i + 1}", None)
        bpy.context.collection.objects.link(root)
        copies = instance_group(master_objs, (x, y, 0.0), rot)
        for c in copies:
            c.parent = root
        blocks.extend(copies)

    # el bloque maestro solo aportaba la geometria: se oculta una vez copiado,
    # nunca antes, para que instance_group lea sus matrices ya resueltas
    master_root.hide_render = True
    for o in master_objs:
        o.hide_render = True
        o.hide_viewport = True
    bpy.context.view_layer.update()

    site = entorno.build_site(mats)
    return blocks + site


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--render", action="store_true")
    ap.add_argument("--samples", type=int, default=96)
    ap.add_argument("--res", type=int, default=1300)
    ap.add_argument("--views", default="")
    ap.add_argument("--out", default=os.path.join(ROOT, "exports"))
    args = ap.parse_args()

    objs = build_scene()
    meshes = [o for o in objs if o.type == "MESH" and not o.hide_render]
    print(f"[flamboyan] {len(meshes)} objetos visibles, "
          f"{sum(len(o.data.polygons) for o in meshes)} caras "
          f"(malla unica: {len({o.data.name for o in meshes})} geometrias)")

    os.makedirs(args.out, exist_ok=True)
    import render_views
    render_views.VIEWS = SCENE_VIEWS

    for o in bpy.data.objects:
        o.select_set(o.type == "MESH" and not o.hide_render)
    glb = os.path.join(args.out, "flamboyan.glb")
    bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB",
                              use_selection=True, export_apply=True)
    print("[flamboyan] glb ->", glb, os.path.getsize(glb) // 1024, "KB")

    render_views.prepare_scene(args.samples, args.res, with_site=False,
                               sun_elevation=46.0, sun_rotation=196.0,
                               exposure=-0.52, view="entrada")
    blend = os.path.join(args.out, "flamboyan.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    print("[flamboyan] blend ->", blend)

    if args.render:
        only = set(v for v in args.views.split(",") if v) or None
        render_views.render_all(args.out, args.samples, args.res, views=only,
                                with_site=False, sun_elevation=46.0,
                                sun_rotation=196.0, prefix="flamboyan",
                                exposure=-0.52, prepared=True)


if __name__ == "__main__":
    main()
