"""
Construye la version autocontenida del visor: `viewer/standalone.html`.

Rehace las texturas a menor resolucion (para no servir 5 MB), exporta un .glb
con ellas y lo incrusta en el HTML en base64, de modo que la pagina funcione
como un unico archivo sin peticiones externas.

    python3 tools/make_web_viewer.py
"""

import base64
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
sys.path.insert(0, os.path.join(ROOT, "models"))
sys.path.insert(0, os.path.join(ROOT, "models", "guancha"))

import textures  # noqa: E402


def build_web_textures(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    # el ruido fino por pixel es lo que dispara el peso del PNG: a esta
    # resolucion no se aprecia y si cuesta megabytes
    textures.flag_atlas(os.path.join(out_dir, "pr_flag_atlas.png"),
                        tile_w=344, tile_h=576)
    textures.stone_textures(os.path.join(out_dir, "stone_base_color.png"),
                            os.path.join(out_dir, "stone_base_normal.png"),
                            size=512, cells=9)
    textures.deck_planks(os.path.join(out_dir, "deck_planks.png"), size=384, rows=11)
    textures.concrete(os.path.join(out_dir, "concrete.png"), size=320)
    textures.sand(os.path.join(out_dir, "sand.png"), size=256)
    total = sum(os.path.getsize(os.path.join(out_dir, f))
                for f in os.listdir(out_dir))
    print(f"  texturas web: {total // 1024} KB")


def main():
    import bpy
    import build
    import build_scene
    import entorno

    web_tex = os.path.join(ROOT, "textures", "web")
    build_web_textures(web_tex)

    build.TEX = web_tex                       # la escena usa las texturas ligeras
    entorno.WATER.update(extent=210.0, divisions=44)
    build_scene.build_scene()

    for o in bpy.data.objects:
        o.select_set(True)
    glb = os.path.join(ROOT, "exports", "guancha_web.glb")
    bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB",
                              use_selection=True, export_apply=True)
    size = os.path.getsize(glb)
    print(f"  glb web: {size // 1024} KB")

    src = open(os.path.join(ROOT, "viewer", "index.html"), encoding="utf-8").read()
    b64 = base64.b64encode(open(glb, "rb").read()).decode("ascii")
    tag = f'<script id="model-b64" type="application/octet-stream">{b64}</script>\n'
    marker = '<script type="importmap">'
    out = src.replace(marker, tag + marker, 1)

    dst = os.path.join(ROOT, "viewer", "standalone.html")
    with open(dst, "w", encoding="utf-8") as fh:
        fh.write(out)
    print(f"  {dst}: {os.path.getsize(dst) // 1024} KB")


if __name__ == "__main__":
    main()
