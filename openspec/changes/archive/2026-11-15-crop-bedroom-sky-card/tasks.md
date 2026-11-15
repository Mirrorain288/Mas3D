# Tasks

## 1. 窗外星空卡自动化校验（TDD）与单面贴窗重构

- [x] 1.1 在 `apps/unity/Assets/Tests/EditMode/BedroomSkyCardTests.cs` 新增校验用例：分别验证 `Main.unity` 中的根级 `"night-sky-card"` 与 `RoomDress` 默认建卡辅助方法满足位于 `-Z` 窗洞（`mesh_219`）正后方（贴近外窗面、`z < window.min.z` 且间距 `<= 0.15 m`）、覆盖窗洞开口且严格落在 `-Z` 墙体横向及檐口高度以内、世界朝向令正面法线指向室内 `+Z` 且材质 `_Cull == (float)UnityEngine.Rendering.CullMode.Back`；运行 EditMode 验证用例按预期报红（RED）
- [x] 1.2 更新 `apps/unity/Assets/Editor/RoomDress.cs`，实现以 `mesh_219` 为锚的 `-Z` 贴窗单面星空卡构建方法（加适度室内斜视视差边距并在外墙及檐口轮廓内钳位、紧贴窗背放置、`CullMode.Back`、无 `mesh_219` 时按 `-Z` 窗比例兜底），并移除或收口原有整墙双面（`CullMode.Off`）建卡路径；同步更新 `apps/unity/Assets/Editor/CliPlayer.cs`（`BuildScene`）与 `apps/unity/Assets/Editor/CliCapture.cs`（`-lightsOnly` 与默认穿衣分支）统一调用该方法，运行对应 EditMode 构建辅助断言验证通过

## 2. 重烘 `Main.unity` 与内外视角实拍验图

- [x] 2.1 核对 `CliPlayer.BuildScene()` 完整保留 `FlatRoom`、`InteractiveDoor` 与 `StreamingAssets/mas3d/anchors.json` 同步逻辑后，执行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity.ps1 run -- -executeMethod Mas3D.Editor.CliPlayer.BuildScene` 重烘 `apps/unity/Assets/Scenes/Main.unity`；随后重跑 `BedroomSkyCardTests` 与 PlayMode `FlatCeilingTests` 确认全绿（`1.1` 由 RED 转 GREEN，且默认/`-flat` 隔离用例不回退）
- [x] 2.2 分别运行 `CliCapture`（含室内正斜望窗机位及 `-orbit 4` 外部环绕视角）与 `CliPlayer.Capture` 生成最新实拍截图并逐张检查：确认 `-Z` 室外与四向环绕视角下不再出现巨幅/斜伸星空片遮挡房间、室内正视角与侧斜角望窗均完整显示星空无露底与明显畸变；如贴图比例或斜视边距需微调，更新参数并重新执行 `2.1` 与截图复核

## 3. 全套门禁回归确认

- [x] 3.1 执行全量回归 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\check.ps1` 与 `openspec validate crop-bedroom-sky-card --strict`，确认 web / Unity EditMode（`RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 保持 `Skipped`）/ PlayMode / smoke / player 全部通过，`mita-room.glb`、`mita-flat.glb`、`Flat.prefab` 与 `HouseLook.cs` 未发生非预期修改

## Workflow follow-up

- 等待用户验收第 3 项结果并明确确认后，再处理工作区变更提交及运行 `/openspec-archive-change` 同步主规格并归档本变更。
