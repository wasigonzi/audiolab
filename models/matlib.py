"""
Materiales Principled con textura y mapa de normales, para todas las escenas.

Las imagenes se empaquetan en el .blend nada mas cargarlas, de modo que el
archivo guardado viaja completo aunque cambien las rutas.
"""

import bpy


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
