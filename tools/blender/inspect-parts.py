"""
逐个网格单独渲染，用来认人：NinjaRipper 的网格名是 Mesh_00xxout，跟家具名字没有关系，
光看名字或多网格合图分不清哪个是衣柜、哪个是显示器。

    blender -b <scene.blend> -P tools/blender/inspect-parts.py -- <outdir> Mesh_0025out Mesh_0146out ...

输出 <outdir>/<网格名>_0.png、_1.png（两个相反视角），Workbench + TEXTURE 着色。
"""
import bpy, mathutils, os, sys

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(argv) < 2:
    raise SystemExit("usage: blender -b scene.blend -P inspect-parts.py -- outdir MESH [MESH ...]")
out_dir = os.path.abspath(argv[0])
names = argv[1:]
os.makedirs(out_dir, exist_ok=True)

scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'TEXTURE'
scene.render.resolution_x = 560
scene.render.resolution_y = 420
scene.render.film_transparent = False

cam_data = bpy.data.cameras.new("inspect_cam")
cam = bpy.data.objects.new("inspect_cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam

meshes = [o for o in bpy.data.objects if o.type == 'MESH']
for name in names:
    ob = bpy.data.objects.get(name)
    if ob is None:
        print("MISSING", name)
        continue
    for o in meshes:
        o.hide_render = (o.name != name)
    pts = [ob.matrix_world @ mathutils.Vector(c) for c in ob.bound_box]
    target = sum(pts, mathutils.Vector()) / 8.0
    size = max((max(p[i] for p in pts) - min(p[i] for p in pts)) for i in range(3))
    for i, off in enumerate([(0.4, 1.0, 0.35), (0.4, -1.0, 0.35)]):
        d = mathutils.Vector(off).normalized()
        cam.location = target + d * (size * 1.6 + 1.0)
        cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
        cam_data.lens = 35
        scene.render.filepath = os.path.join(out_dir, "%s_%d.png" % (name, i))
        bpy.ops.render.render(write_still=True)
        print("RENDER", scene.render.filepath)
print("DONE")
