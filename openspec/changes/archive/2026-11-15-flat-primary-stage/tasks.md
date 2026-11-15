# Tasks

## 1. 活跃锚点契约与启动参数扩展（对应执行计划 Task 1）

- [x] 1.1 在 `apps/unity/Assets/Scripts/AnchorTable.cs` 新增 `FlatRelativePath`、`ActiveRelativePath`、`SetActiveRelativePath`、`ResetActive` 及可选配置类型（`WaypointEntry`、`LinkEntry`、`SpawnEntry`、`SeatEntry` 扩展字段），保证旧 `anchors.json` 向后兼容；在 `apps/unity/Assets/Scripts/Boot/BootArgs.cs` 新增 `-bedroom [0|1]` 解析，并通过 EditMode 过滤执行 `BootArgsTests|FlatAnchorTableTests` 先红后绿。

## 2. 平层几何求解器与配置化多房间路网（对应执行计划 Task 2）

- [x] 2.1 在 `apps/unity/Assets/Scripts/Room/EdgeTable.cs` 增设 `WalkNode.Room`、`WalkEdge.DoorId`、`FindEdge` 与 `Rebuild()`，当活跃表含 `links` 时动态构建多房间图，否则保留原单卧静态构图回退。
- [x] 2.2 新增 `apps/unity/Assets/Editor/CliFlatAnchors.cs` 并在 `tools/unity.ps1` 注册 `flatanchors` 子命令；基于烘焙后 `Flat.prefab` 真实碰撞体生成 `Anchors/anchors-flat.json` 与 `StreamingAssets/mas3d/anchors-flat.json`（含客厅沙发主座/副座、茶几前迎面 `defaultSpawn`、卧室/客厅/餐厨生活区节点、`Door_BedroomLiving` 与 `Door_LivingKitchen` 两侧门洞节点，卫浴零节点），完成 `0.30 m` 胶囊全边扫掠 `0` 碰撞并通过 EditMode `FlatAnchorTests|EdgeTableTests`。

## 3. 跨房间穿门通行与单臂 IK 推门（对应执行计划 Task 3）

- [x] 3.1 在 `apps/unity/Assets/Scripts/Room/InteractiveDoor.cs` 新增 `HandleWorldPosition()` 返回 `HandleMesh` 实时世界中心；扩展 `apps/unity/Assets/Scripts/Avatar/AvatarWalker.cs` 修复床座判定语义（按 `seat.pose == "bed"`），并在踏入带 `DoorId` 的边且门未全开时暂停位移、转向、调用 `SetOpen(true, false)` 并由 `TwoBoneIk.Solve` 驱动单臂末端朝向 `HandleWorldPosition()` 直至开门完成穿过。
- [x] 3.2 新建并执行 PlayMode `FlatNavigationTests|AvatarWalkTests`，验证全平层真碰撞体 `0.30 m` 扫掠 zero-hit、闭合卧室门单臂推门抵达、已敞开餐厨门直接穿行，以及门后玩家阻挡时开门后安全停步礼让。

## 4. 默认平层开局、客厅大沙发动作适配与回归迁移（对应执行计划 Task 4）

- [x] 4.1 改造 `RoomBootstrap.cs`（默认进入平层并载入 `anchors-flat.json`，提供 `-bedroom 1` / `ShowBedroom()` 回退，卸载时清理活跃表）、`AvatarActor.cs`（`Start()` 在存在未引导 `RoomBootstrap` 时礼让落座）、`CliPlayer.cs`（同步拷贝 `anchors-flat.json` 到 StreamingAssets）与 `ActionDirector.cs` / `ActionSeats.cs`（适配客厅大沙发 `S1`/`S2` 1–5 互动并拒绝行进中或非兼容外围座请求）。
- [x] 4.2 迁移 `SceneFlow.cs`（新增 `LoadBedroom()`）与既有用例（更新 `FlatCeilingTests`、`FlatDoorTests`、`BedroomSkyCardTests` 及明确校验旧单卧几何的场景夹具），新建 `FlatActionTests` 并在重建 `Main.unity` 后验证 PlayMode `FlatActionTests|FlatCeilingTests|FlatDoorTests|ActionSmokeTests|BedroomSkyCardTests|AvatarSceneTests|ActionSceneTests` 全绿。

## 5. 克制的静谧空闲自主换区流转（对应执行计划 Task 5）

- [x] 5.1 新建 `apps/unity/Assets/Scripts/Avatar/AvatarRoutine.cs` 并在 `RoomBootstrap.cs` 接线：支持 `Enabled` 测试禁用开关、`IdleSeconds`、`Tick(dt)`、非卫浴生活区低频轮换及“说话优先于走路”抢占（对话/输入聚焦/动作演出时暂停并重设计时）；通过 EditMode `RoutineTests` 与 PlayMode `FlatNavigationTests`（`Thinking` 阶段打断检验）。

## 6. 整套重烘与全门禁联合验收（对应执行计划 Task 6）

- [x] 6.1 依次执行 `tools/unity.ps1 flatanchors`、`tools/unity.ps1 player`、`tools/check.ps1 -SkipWeb -SkipBridge -SkipPlayer` 与 `tools/unity.ps1 capture`，确认 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 保持跳过边界而其余 EditMode / PlayMode / Smoke（含默认平层 `ActionSmokeTests`）全部通过、首帧客厅面对面画面正常，且 `openspec validate --all --strict` 通过；提交前征询用户确认（不自行 `git commit`）。

## Workflow follow-up

- **独立阻塞待办项 — 语义控制表情（`LumenAct`：<https://github.com/AaBb-coder/LumenAct>） `[Blocked]`**：因需先在 Blender 中为角色模型手动添加 **118 个形态键（shape keys）**，目前处于阻塞状态；阻塞解除前继续沿用现有 VRM `ExpressionKey` 与 TTS 口型同步，后续解除阻塞后单独建项推进。
- **后续阶段路线延展（步骤 ②–④）**：待本阶段确认验收并使用 `/openspec-archive-change` 归档后，依次开展步骤 ②（分室布灯、壳体阴影策略、接触阴影、窗外夜色景观）、步骤 ③（Kimodo 离线动作资产、`TwoBoneIk` 联合、`MotionPlan` 协议与全身推门）与步骤 ④（咖啡/读诗跨区陪伴玩法扩展）。
