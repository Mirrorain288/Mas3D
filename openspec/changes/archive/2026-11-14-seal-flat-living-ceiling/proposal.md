# Proposal

## Why

经核查 [帽子米塔的房间.blend](file:///d:/Mas3D/素材/Mita_room/blender少女米塔的房间卡通室内卧室客厅厨房3D模型游戏场景素材/帽子米塔的房间.blend) 与运行时接线，`CliFlat.Map()` 此前将客厅顶部的 `mesh_303.002` 误报为 `y = 1.77 m`（`1.5×` 后 `2.655 m`）的 `15.0 m²` 二层楼板，实际该网格总面积仅 `0.171 m²`（8 圈筒灯框），上方只有单层客厅吊顶（`mesh_334.001` 与 `mesh_193`）而无二层墙体与屋顶；同时 `RoomBootstrap.Boot()` 切入 `-flat 1`（或 `-house 1`）时未隐藏默认卧室 `Room`（`mita-room.glb`）及默认卧室根级灯光/天空卡（`room-lights`、`night-sky-card`），导致整间旧卧室直接重叠在平层客厅正上方和正中央。需要在保持源资产只读的前提下彻底消除模式间重叠，并把客厅上方按单层吊顶物理封实、修正楼层扫描误报。

## What Changes

- **模式互斥隔离默认卧室及其根级附属物**：更新 `RoomBootstrap.Boot()`，在进入 `flatMode` 或 `houseMode` 时显式缓存并将原默认卧室 `Room`（`mita-room.glb`）以及同场景根级 `room-lights`、`night-sky-card` 设为未激活；在默认卧室模式时保留激活并保证 `FlatRoom`/`HouseRoom` 未激活，避免几何与光照互相穿透。
- **客厅单层物理封顶（方案 A，保留俯视 Cutaway）**：在 `CliFlat.Bake()` 中根据客厅实际平面范围于吊顶高度生成纯碰撞封顶组件（`BoxCollider` 封住客厅与门厅上空，不新增遮挡高空俯视的朝上渲染顶板），若重拍真实客厅仰视发现接缝或穿光则于内侧补齐，确保玩家与相机无法越出单层空间，同时维持 `flat-wide.png` 俯视查看客厅家具与动线的清晰度。
- **修正 `CliFlat.Map()` `[floor]` 扫描并补充真·客厅校验**：更新 `CliFlat.Map()` 按向上三角形有效面积筛选水平地面，不再把灯框 `mesh_303.002` 与下向天花板 `mesh_193`/`mesh_188.001` 误列为可行走楼层；重烘 `Flat.prefab`、重刷 `Main.unity`，并补充 EditMode / PlayMode 测试与真实截图验收。

## Capabilities

### New Capabilities

- 无

### Modified Capabilities

- `scene/unity-flat`: 补充 `-flat 1` 模式下默认卧室及默认灯光/天空卡必须停用的双向隔离约束，以及客厅按单层吊顶物理封顶且不误报二层楼板的验收要求。

## Impact

- 运行时脚本：`apps/unity/Assets/Scripts/RoomBootstrap.cs`
- 编辑器烘焙与巡检：`apps/unity/Assets/Editor/CliFlat.cs`
- 烘焙产物：`apps/unity/Assets/_Local/room/Flat.prefab`、`apps/unity/Assets/Scenes/Main.unity`
- 测试与截图：`apps/unity/Assets/Tests/EditMode/FlatMapTests.cs`（或 `CliFlatTests.cs`）、`apps/unity/Assets/Tests/PlayMode/FlatCeilingTests.cs`（或扩展现有平层 PlayMode 测试）、`apps/unity/Shots/flat-living.png` / `flat-wide.png`
