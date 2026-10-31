"""
把一间 Blender 场景（这里是从 .blend 里的 NinjaRipper 抓取）导成 three.js 能直接加载的 GLB。

    blender -b <scene.blend> -P tools/blender/export-room.py -- <out.glb> [--hide Mesh_0167out ...]

做三件事：
1. 按名字隐藏不要的东西（角色本体、墙外的天空平面等）；
2. 把内嵌贴图缩到最长边 1024（原图 2048 的 26 张 DDS 会让 GLB 到 150 MB 级）；
3. 只选网格导出 GLB + WebP 贴图，Y-up（three.js 约定）。

注意：导出的房间素材不许进仓库——它是别人的游戏资产，只在本机用。
"""
import bpy, os, sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    raise SystemExit("usage: blender -b scene.blend -P export-room.py -- out.glb [--hide NAME ...]")
out = os.path.abspath(argv[0])
hide = []
if "--hide" in argv:
    hide = argv[argv.index("--hide") + 1:]

scene = bpy.context.scene
for name in hide:
    ob = bpy.data.objects.get(name)
    if ob:
        ob.hide_render = True
        ob.hide_viewport = True

for img in bpy.data.images:
    if not img.packed_file:
        continue
    w, h = img.size
    if max(w, h) > 1024:
        k = 1024.0 / max(w, h)
        img.scale(int(w * k), int(h * k))

bpy.ops.object.select_all(action='DESELECT')
kept = 0
for ob in bpy.data.objects:
    if ob.type == 'MESH' and ob.name not in hide:
        ob.select_set(True)
        kept += 1

os.makedirs(os.path.dirname(out), exist_ok=True)
bpy.ops.export_scene.gltf(
    filepath=out,
    export_format='GLB',
    use_selection=True,
    export_image_format='WEBP',
    export_image_quality=80,
    export_apply=True,
    export_yup=True,
)
print("KEPT", kept)
print("EXPORTED", os.path.getsize(out))
