# Design

## D1 为什么是导入房间而不是继续改程序化几何

程序化教室的问题不是参数没调好，而是"没有 UV 与贴图"这件事在地板、墙、天花板、窗框、灯带五处同时存在。要把它改成有质感的卧室，等于给每个盒子重画 UV、烘一套贴图、再摆十几件家具——工作量在 12–20 h 且结果仍是"自己画的"。导入的房间一次拿到手绘贴图、真实家具布局与斜屋顶，收尾只要 4–5 h。取舍是素材不能分发，而本项目本就按非商用、自己玩处理。

## D2 素材不入库，工具入库

NinjaRipper 抓的是别人游戏的帧内几何，许可不明，因此 `apps/web/public/assets/room/mita-room.glb` 与贴图不进仓库（`.gitignore` 的 `/apps/web/public/assets/*` 已经覆盖）。入库的是可复现的导出脚本与整备代码：别人拿到自己的 `.blend` 也能跑同一套流程。

## D3 探路式默认加载

`buildClassroom()` 的顺序是：显式 `VITE_ROOM_GLB=none` → 程序化；否则先试导入房间（显式 URL 优先，其次仓库内约定路径），失败后**只有显式指定时**才打 `console.error`，最后退回程序化教室。

理由：仓库 clone 下来跑 `pnpm dev` 必须是可玩状态——本机没放素材要静，配置写错要吵。另一种做法是"检测到文件才用"，但前端拿不到目录列表，只能靠加载失败来判断；把失败当信号就必然要么吵要么静，所以用"是否显式指定"区分意图。

## D4 灯光在代码里补，不动 GLB

导出时不动 Blender 的灯光，而是 three 侧加一盏吸顶暖光 + 一盏半球补光 + 月光，并把全局三盏（key/fill/rim）按 `lightProfile: { key: 0.25, fill: 0.2, rim: 0 }` 压下去。

理由：GLB 里的灯光与 Blender 的渲染设置不保证一致，每次调参都要重导；放在代码里改一行就能重拍。`lightProfile` 做成 `ClassroomBuild` 的可选字段，程序化教室不提供这个字段，因此不会被压暗。

## D5 三处白模按位置认，材质按用途给

导出后有三处网格没有贴图（材质名指向不存在的 DDS）：`Mesh_0138out`（床边抽屉柜）、`Mesh_0014out`（靠墙立式镜）、`Mesh_0200out`（窗边四格置物架）。`materialKindFor(meshName)` 用一张显式表把它们映射成 `paintedWhite` / `mirror` / `ceiling`，其余 `keep`。

镜子第一版给 `metalness: 0.96`，在没有 envMap 的场景里读作纯黑板；改成 `metalness: 0.18, roughness: 0.16` 才是"深色玻璃"。真镜面要 cube camera，每帧多一遍渲染，本机不划算，因此接受"看不清自己"的镜子。

## D6 家具必须整组复制（NinjaRipper 按 draw call 切网格）

右墙从门框到 +z 约 2 m 是空墙。第一版复制了 `Mesh_0025out`（当时认为是"衣柜"），结果右墙上出现悬浮显示器、蓝格子架与主机塔：`Mesh_0025out` 实际是"显示器 + 主机 + 支架"三件散件，衣柜的观感来自母本位置的巧合。用 `VITE_ROOM_DECOR=tag` 给副本上纯色截图才定位到。

因此 `decorPlacements()` 以 `sources: string[]` 表达"一个组合"，书桌组必须是 `["Mesh_0146out", "Mesh_0025out"]`，并由单测钉住"书桌不能单独出现"。

## D7 世界空间的旋转：premultiply 世界 Y

Blender 导出的节点自带 -90° X 旋转。`holder.rotateY()` 绕的是对象局部 Y，长条家具会绕自己的长轴翻倒（实测衣柜副本包围盒从 0.95×1.64×2.00 变成 1.64×2.01×0.94）。修法是 `holder.quaternion.premultiply(quaternionFromAxisAngle(WORLD_UP, yaw))`——左乘即在父空间（世界）里转，且不移动原点；随后再把 holder 的 position 设回 `pivot - pivot.applyQuaternion(spin)` 让旋转绕包围盒中心发生。

## D8 卸载边界：复制品与母本共享资源

复制用 `clone()`，几何与材质是共享引用。卸载时只 `root.clear()`，不在副本上 `dispose()`——否则会把母本也一起废掉。真正的释放发生在整间房 dispose 时。

## D9 冒烟必须钉死基线

`e2e/smoke.mjs` 测的是"产品默认路径能不能跑"，而默认路径会随本机是否放着房间 GLB 而变。脚本因此显式 `define` `VITE_ROOM_GLB="none"` 与 `VITE_ROOM=""`，保证在开发机与 CI 上冒烟的是同一套几何。

## Risks / Trade-offs

- **许可风险**：房间素材是他人游戏资产，不能分发、不能商用；仓库里只放脚本与代码，这条必须写进 README。
- **座位标定绑死在素材上**：`IMPORTED_ROOM_LAYOUT` 是给这一间房量的坐标；换房间必须重新量，没有自动求解（座位要靠眼睛与截图定，家具是 Ripper 的散件，没有语义锚点）。
- **网格名无语义**：`Mesh_0025out` 之类的名字不表达内容，任何"复制某件家具"的改动都必须先隔离渲染确认；单测只能钉住"某个组合里必须同时出现哪些网格"。
- **斜顶与受限视角**：屋顶是双坡斜顶，极端机位（拉远抬高）会在屋檐内侧露一条本色；不做遮挡件。
- **镜面无 envMap**：立式镜只能是深色玻璃。
- **不自动降级掉家具**：`furnishImportedRoom` 复制的是整组网格，在低质量档位上不会减少 draw call；房间总共 36 网格 / 29,537 三角面，仍在预算内。
