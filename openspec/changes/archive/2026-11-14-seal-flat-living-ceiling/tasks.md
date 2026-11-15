# Tasks

## 1. 默认卧室与平层模式互斥隔离

- [x] 1.1 先在 `apps/unity/Assets/Tests/PlayMode/FlatCeilingTests.cs` 编写隔离用例：加载 `Main.unity` 调用 `ShowFlat()` 并 `Boot()`，断言原默认卧室 `Room` 与场景根级 `room-lights`、`night-sky-card` 均被停用且不与平层重叠（运行确认 RED）；随后更新 `apps/unity/Assets/Scripts/RoomBootstrap.cs` 缓存默认房间并按所属 `Scene` 根对象停用/恢复 `room-lights` 与 `night-sky-card`，补充默认模式仍激活默认卧室、停用 `FlatRoom` 的断言，重跑用例确认 GREEN。

## 2. 客厅单层物理封顶与 `CliFlat.Map()` 真实楼层判据

- [x] 2.1 先编写 EditMode 测试 `apps/unity/Assets/Tests/EditMode/FlatMapTests.cs` 校验向上三角形真实面积判定助手（确认空心灯框 `mesh_303.002` 与单面向下天花板 `mesh_193`/`mesh_188.001` 被排除在 `[floor]` 之外而真实地面保留），运行 EditMode 确认 RED；随后在 `apps/unity/Assets/Editor/CliFlat.cs` 抽出该判定助手改进 `Map()`，并在 `Bake()` 中于客厅及门厅上方添加仅物理封顶的 `LivingCeilingSeal` `BoxCollider`（无朝上外遮罩面，保持 `flat-wide` 俯视 cutaway），重跑 EditMode 确认全绿。
- [x] 2.2 依次运行 `tools\_bake.ps1 -Method Mas3D.Editor.CliFlat.Bake -Log flat-bake`、`tools\unity.ps1 player`、`tools\_bake.ps1 -Method Mas3D.Editor.CliFlat.Map -Log flat-map` 与 `tools\_shot.ps1 -Method Mas3D.Editor.CliFlat.Shot -Log flat-shot`；核对 `flat-map.log` 不再把 `mesh_303.002`/`mesh_193`/`mesh_188.001` 打印为 `[floor]`，查看真实客厅视角 `flat-living.png` 与全景 `flat-wide.png` 确认默认卧室重叠消失、客厅吊顶内部无孔洞或漏光（若有微缝则在 `CliFlat.Bake` 补齐内向封边并重烘）；随后在 `FlatCeilingTests.cs` 追加针对重烘 `Flat.prefab` 客厅与门厅网格的上行射线与胶囊封顶测试，运行 `FlatCeilingTests` 确认全绿。

## 3. 全量集成验收与门禁检查

- [x] 3.1 依次运行 `openspec validate seal-flat-living-ceiling --strict`、`git status --short apps/unity/Assets/_Local/room/mita-flat.glb apps/unity/Assets/_Local/room/mita-room.glb`、`FlatDoorTests` 与 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\check.ps1`；核对 `web`、`unity-edit`、`unity-play`、`smoke`、`player` 全绿（仅保留既有的 `bridge` 与 `RoomLightTests` 预期跳过），将真实输出与新截图路径交付用户验收。

## Workflow follow-up

- 待用户确认第 2 项验收结果并决定是否提交/归档后，通过 `/openspec-archive-change` 归档，随后继续第 3 项（窗外星空片在外部取景时裁掉）。
