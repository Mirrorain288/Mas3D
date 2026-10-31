# Proposal

## Why

程序化教室在试玩里暴露了三个具体问题：地板是半径 3.6 m 的圆形而墙是 ±3.62 m 的方形，四角露空；墙/天花板/窗框/灯带全是纯色 `BoxGeometry`，零贴图，画面读起来"分辨率很低"；7.24 m 见方、净高 3.3 m 的空间对一张课桌来说太空。

手里有一套带贴图的粉色阁楼卧室（`素材/mita/无标题.blend`，Blender 3.4，NinjaRipper 抓取的帧内几何：39 个网格、47,446 顶点、地板是正方形、斜屋顶带格线吊顶、全套家具都写了 UV）。用它替掉程序化几何能一次解决三条，代价是素材来自别人的游戏、只能本机使用，并且导出后有三处丢贴图、灯光与右墙需要收尾。

## What Changes

- **场景可以来自导入的房间 GLB**：`buildClassroom()` 先试 `/assets/room/mita-room.glb`（或 `VITE_ROOM_GLB` 指定的 URL），失败静默退回程序化教室；`VITE_ROOM_GLB=none` 强制程序化教室。
- **导入房间由 `roomDressing.ts` 整备**：补材质（屋顶 → Poly Haven 吊顶 PBR、三处白模 → 乳胶漆/玻璃）、补光（吸顶暖光 + 半球补光，同时把全局三盏压到 0.25/0.2/0）、补家具（右墙空墙前整组复制书桌 + 显示器/主机 + 抽屉柜）。
- **座位与朝向由 `IMPORTED_ROOM_LAYOUT` 标定**：座椅锚点不再是 `findSeatAnchor` 量出来的，而是常量；导入房间时把地板对齐 y=0、座位平移到原点。
- **素材不入库**：房间 GLB 与贴图由 Blender 脚本本机生成，落在 `apps/web/public/assets/room/`（被 `.gitignore` 排除）；入库的是导出脚本、整备代码、测试与文档。
- 不做：真镜面（要每帧额外渲一遍 cube camera）、把房间几何在 Blender 里裁短（A++ 路线，本次不做）、动态换房间 UI。

## Capabilities

### Modified Capabilities

- `scene/space-classroom`：新增"室内几何可以来自导入的场景包"与"导入场景缺失时静默退回程序化几何"两条约束；房间不再是太空教室专属形态，星空这一条对导入房间不适用（窗户外是房间自带的贴图）。

## Impact

- 代码：`apps/web/src/scene/classroom.ts`（导入分支、`lightProfile`、`DEFAULT_IMPORTED_ROOM`）、`apps/web/src/scene/roomDressing.ts`（新）、`apps/web/src/scene/lights.ts`（对外暴露 fill/rim）、`apps/web/src/scene/Scene.tsx`（按 `lightProfile` 压全局灯）
- 工具：`tools/blender/export-room.py`、`tools/blender/inspect-parts.py`（新）
- 测试与探针：`apps/web/src/scene/__tests__/roomDressing.test.ts`（新）、`apps/web/e2e/{room-preview,room-angles,room-map,room-turn}.mjs`（新）、`apps/web/e2e/smoke.mjs`（钉死 `VITE_ROOM_GLB=none` 拿基线）
- 文档：`README.md` 新增"换房间（自备卧室场景）"
