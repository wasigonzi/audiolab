"""
Vistas renderizadas del faro de La Guancha.

Monta cielo, sol, mar y camaras sobre la escena ya construida por build.py y
saca varias tomas con Cycles (CPU).
"""

import math
import os

import addon_utils
import bpy
from mathutils import Vector


def _enable_cycles():
    try:
        addon_utils.enable("cycles", default_set=True, persistent=True)
    except Exception:
        pass
    try:
        bpy.context.scene.render.engine = "CYCLES"
        return True
    except TypeError:
        bpy.context.scene.render.engine = "BLENDER_EEVEE"
        return False


def setup_world(sun_elevation=38.0, sun_rotation=225.0):
    """Cielo caribeno + sol de media tarde."""
    world = bpy.data.worlds.new("Cielo")
    bpy.context.scene.world = world
    world.use_nodes = True
    nt = world.node_tree
    bg = nt.nodes["Background"]
    bg.inputs["Strength"].default_value = 1.0

    sky = nt.nodes.new("ShaderNodeTexSky")
    try:
        sky.sky_type = "NISHITA"
        sky.sun_elevation = math.radians(sun_elevation)
        sky.sun_rotation = math.radians(sun_rotation)
        sky.altitude = 10.0
        sky.air_density = 1.05
        sky.dust_density = 1.6
        sky.sun_intensity = 0.7
    except Exception:
        sky.sky_type = "PREETHAM"
    nt.links.new(sky.outputs["Color"], bg.inputs["Color"])

    sun_data = bpy.data.lights.new("Sol", type="SUN")
    sun_data.energy = 4.2
    sun_data.angle = math.radians(1.2)
    sun_data.color = (1.0, 0.95, 0.86)
    sun = bpy.data.objects.new("Sol", sun_data)
    bpy.context.collection.objects.link(sun)
    sun.rotation_euler = (
        math.radians(90.0 - sun_elevation), 0.0, math.radians(sun_rotation - 90.0)
    )
    return sun


def setup_site():
    """Mar turquesa y plataforma de hormigon: solo para las vistas, no se exporta."""
    objs = []

    water_mat = bpy.data.materials.new("Mar")
    water_mat.use_nodes = True
    b = water_mat.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (0.031, 0.35, 0.40, 1.0)
    b.inputs["Roughness"].default_value = 0.08
    try:
        b.inputs["IOR"].default_value = 1.33
    except KeyError:
        pass

    mesh = bpy.data.meshes.new("Mar")
    s = 900.0
    mesh.from_pydata([(-s, -s, -0.35), (s, -s, -0.35), (s, s, -0.35), (-s, s, -0.35)],
                     [], [(0, 1, 2, 3)])
    mesh.update()
    mesh.materials.append(water_mat)
    water = bpy.data.objects.new("Mar", mesh)
    bpy.context.collection.objects.link(water)
    objs.append(water)

    quay_mat = bpy.data.materials.new("Muelle")
    quay_mat.use_nodes = True
    qb = quay_mat.node_tree.nodes["Principled BSDF"]
    qb.inputs["Base Color"].default_value = (0.58, 0.55, 0.49, 1.0)
    qb.inputs["Roughness"].default_value = 0.9

    qm = bpy.data.meshes.new("Muelle")
    q = 7.5
    qm.from_pydata([(-q, -q, 0.0), (q, -q, 0.0), (q, q, 0.0), (-q, q, 0.0)],
                   [], [(0, 1, 2, 3)])
    qm.update()
    qm.materials.append(quay_mat)
    quay = bpy.data.objects.new("Muelle", qm)
    bpy.context.collection.objects.link(quay)
    objs.append(quay)
    return objs


def setup_camera(location, target, lens=45.0):
    cam_data = bpy.data.cameras.new("Camara")
    cam_data.lens = lens
    cam = bpy.data.objects.new("Camara", cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location = location

    direction = Vector(target) - Vector(location)
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = cam
    return cam


VIEWS = {
    # nombre: (posicion, objetivo, lente, ancho, alto)
    "hero":      ((24.0, -26.0, 17.5), (0.0, 0.0, 10.5), 52.0, 0.80, 1.0),
    "frontal":   ((0.0, -52.0, 10.5),  (0.0, 0.0, 10.5), 70.0, 0.62, 1.0),
    "templete":  ((13.0, -13.5, 20.5), (0.0, 0.0, 17.6), 78.0, 1.0, 0.78),
    "contrapicado": ((7.0, -8.5, 1.6), (0.0, 0.0, 13.0), 26.0, 0.72, 1.0),
}


def render_all(out_dir, samples=64, res=1100, views=None):
    using_cycles = _enable_cycles()
    scene = bpy.context.scene
    if using_cycles:
        scene.cycles.device = "CPU"
        scene.cycles.samples = samples
        scene.cycles.use_denoising = True
        scene.cycles.max_bounces = 6
        scene.cycles.transmission_bounces = 2
    else:
        scene.eevee.taa_render_samples = max(samples, 32)

    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    # AgX apaga demasiado los colores de la bandera; Standard se acerca
    # mucho mas al aspecto saturado de la foto de referencia
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.exposure = -0.45
    scene.view_settings.look = "None"

    setup_world()
    setup_site()

    renders_dir = os.path.join(out_dir, "renders")
    os.makedirs(renders_dir, exist_ok=True)

    out = []
    for name, (loc, tgt, lens, aw, ah) in VIEWS.items():
        if views and name not in views:
            continue
        setup_camera(loc, tgt, lens)
        scene.render.resolution_x = int(res * aw)
        scene.render.resolution_y = int(res * ah)
        path = os.path.join(renders_dir, f"guancha_{name}.png")
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print("[render]", path)
        out.append(path)
    return out
