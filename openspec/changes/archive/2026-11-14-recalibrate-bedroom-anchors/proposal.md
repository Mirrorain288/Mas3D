# Proposal

## Why

Unity 主场景的默认房间资产 `apps/unity/Assets/_Local/room/mita-room.glb`（单间卧室，GUID `5f50f9168985d6744a555e9c8644a77e`）已由 `.blend` 重新导出为新版卧室（107 个 `mesh_###` 节点，按 `CenterBottom` 归位后地面 `mesh_397.004` 约 `4.0 m × 4.15 m`）。该资产与全屋平层 `mita-flat.glb` 均属于既定资产方案，红灯完全来自代码与测试仍绑定旧房间资产（`Mesh_0###out` 命名、约 `7.0 m × 6.7 m` 空间范围与旧家具坐标）。

当前基线（`tools\check.ps1`）表现为：`web`、`unity-edit`、`player` 通过；`unity-play` 6 条失败（`AvatarSeatTests.NothingSinksIntoTheBench`、`AvatarWalkTests.AThinPartThatFloatsIsNotExemptFromTheSweep`、`AvatarWalkTests.EveryEdgeClearsTheRealRoom`、`PlayerWalkTests.EveryAnchorSpawnsOnTheFloor`、`PlayerWalkTests.PlayerCannotWalkThroughTheWall`、`RoomAnchorTests.StandingAnchorsClearTheCapsule`）；`smoke` 1 条失败（`ActionSmokeTests.EveryActionPlaysOnceThroughTheScene` 在 `hands` `Cancel()` 后偏离进场前位置 `0.1146 m`，`HANDOFF.md` 记录为 `0.180 m`，超过 `< 0.05 m` 判据）。此外 `RoomColliders.IsShell` 仍只列举旧壳体网格名，未包含新卧室的墙/地壳体网格。

## What Changes

- **动手前先取证 `smoke` 回位偏差**：在修改实现前补跑一次 10 帧诊断观测，在 `Boot()` 之后逐帧打印 `player.transform.position`、`player.Grounded`（`CharacterController.isGrounded`）以及与 `mesh_216`/`mesh_217` 碰撞体的距离，量化拆分 `0.11~0.18 m` 中出生悬空（`SpawnLift`/推离）与 `spawn` 记录时机（`SetPosture + yield return null` / 帧长抖动）的占比。
- **标定新卧室 `mesh_###` 角色并重算锚点**：更新 `apps/unity/Assets/Editor/CliAnchors.cs` 的房间网格映射与角色/座位求解参数，通过 `tools/unity.ps1 anchors` 输出到 `apps/unity/Anchors/anchors.json`（`Application.dataPath/../Anchors`），再经 `tools/unity.ps1 player` 同步副本至 `apps/unity/Assets/StreamingAssets/mas3d/anchors.json` 并重建 `Main.unity`。所有站立锚点严格满足离墙 `>= 0.35 m`、离静态碰撞体 `>= 0.45 m`。
- **更新壳体白名单与角色行走边表**：
  - 在 `apps/unity/Assets/Scripts/RoomColliders.cs` 的 `IsShell` 白名单加入新卧室墙/地等壳体 `mesh_###`（保留旧名兼容），确保生成 `inner-collider`。
  - 更新 `apps/unity/Assets/Scripts/Room/EdgeTable.cs` 的绕行拐点、`Links` 与 `SeatStands`（`S2` 按新卧室床尾 `+Z` 朝向离开床区并衔接 `W2`/`A5`），同步更新 `apps/unity/Assets/Tests/EditMode/EdgeTableTests.cs` 对 `S2` 床尾步出方向的断言，保证每条边 `0.30 m` 胶囊扫掠 0 碰撞。
  - 微调 `apps/unity/Assets/Editor/RoomDress.cs` 中 `ceiling-glow` 四灯横向分布比例（`±0.33` → `±0.36`，按新房间宽 `4.372 m` 跨度 `3.148 m > 3.0 m` 且保持距墙 `0.41 m`），满足 `RoomLightTests.TheGlowIsFourLightsJustUnderTheCeiling`。
- **更新 PlayMode 测试命名/坐标与 `smoke` 落稳时序**：
  - 替换 `apps/unity/Assets/Tests/PlayMode/` 中 `RoomAnchorTests.cs`、`PlayerWalkTests.cs`、`AvatarSeatTests.cs`、`AvatarWalkTests.cs` 的旧网格名与旧空间假设。
  - 按 10 帧取证结论修正 `ActionSmokeTests.cs`（记录 `spawn` 后先落稳再进场的时序与新出生点配合），**不放宽 `0.05 m` 回位判据**。
- **保留 `S1 = mesh_217`（`topY = 0.338 m`、`facingY = 0.6°`）并重标定坐姿与双人动作接触**：
  - 调整 `CliAnchors.cs` 中 `S1` 纵向落座位置（使其 `SeatedRadius = 0.22 m` 胶囊脱离床尾 `mesh_216`），使 `RoomAnchorTests.SeatsRestOnTheirSeatWithoutPenetration` 真实通过。
  - 调整 `AvatarPose.Table` 腿/脚旋转表（适配 `0.338 m` 低脚凳避免小腿与脚掌穿入地面），并同步更新 `AvatarSeatTests`。
  - 调整 `ActionPoses`、`ActionDirector`（站位距离/仰俯或视角兜底）、`PlayerReach`（如 `Raised`）及 `ActionDirectorTests` / `ActionKissTests` / `ActionPatTests` 等围绕旧 `0.604 m / 270°` 长凳硬编码的角度与接触标定，保证五个动作在保持既有姿态门禁与接触容差内全绿。
- **明确不做**：
  - 不修改、不重新导出 `mita-room.glb`、`mita-flat.glb` 等任何房间资产。
  - 不修改 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的刻意 `Assert.Ignore`（HANDOFF 已知缺口）。
  - 不修改多房间拼装脚本 `RoomLayout.cs`（`CliHouse` 用途）。
  - 不在本变更处理平层三扇门交互、二层楼板楼梯/封堵或窗外星空片裁剪（后续独立优先级任务）。

## Capabilities

### New Capabilities

- `scene/unity-bedroom`：定义 Unity 默认单间卧室（`mita-room.glb`）的壳体碰撞体反绕、锚点与座位净空产线（`CliAnchors` -> `Anchors/anchors.json` -> `CliPlayer` -> `StreamingAssets/mas3d/anchors.json`）、行走边表扫掠与 `ActionSmokeTests` 回位时序契约。

### Modified Capabilities

- 无（既有 `scene/space-classroom` 仅约束浏览器端教室场景）。

## Impact

- 编辑器工具：`apps/unity/Assets/Editor/CliAnchors.cs`、`apps/unity/Assets/Editor/RoomDress.cs`
- 运行时脚本：`apps/unity/Assets/Scripts/RoomColliders.cs`、`apps/unity/Assets/Scripts/Room/EdgeTable.cs`、`apps/unity/Assets/Scripts/Avatar/AvatarPose.cs`、`apps/unity/Assets/Scripts/PlayerReach.cs`、`apps/unity/Assets/Scripts/Actions/ActionPose.cs`、`apps/unity/Assets/Scripts/Actions/ActionDirector.cs`
- 锚点数据与场景：`apps/unity/Anchors/anchors.json`、`apps/unity/Assets/StreamingAssets/mas3d/anchors.json`、`apps/unity/Assets/Scenes/Main.unity`
- EditMode / PlayMode 测试：`apps/unity/Assets/Tests/EditMode/EdgeTableTests.cs`、`apps/unity/Assets/Tests/PlayMode/RoomAnchorTests.cs`、`PlayerWalkTests.cs`、`AvatarSeatTests.cs`、`AvatarWalkTests.cs`、`ActionSmokeTests.cs`、`ActionDirectorTests.cs`、`ActionKissTests.cs`、`PlayerLegsTests.cs`、`ActionPatTests.cs`
