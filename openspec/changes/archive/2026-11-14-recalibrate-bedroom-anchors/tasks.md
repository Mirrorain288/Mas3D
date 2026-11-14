# Tasks

> 约束清单：
> - 不修改、不重新导 `apps/unity/Assets/_Local/room/mita-room.glb`（GUID `5f50f9168985d6744a555e9c8644a77e`）与 `mita-flat.glb`。
> - 不修改 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的刻意 `Assert.Ignore`。
> - 不放宽离墙 `>= 0.35 m`、离静态碰撞体 `>= 0.45 m`、扫掠半径 `0.30 m` 与 `Cancel()` 回位 `< 0.05 m` 任何判据。
> - 产线顺序固定：`unity.ps1 anchors`（写入 `apps/unity/Anchors/anchors.json`）→ `unity.ps1 player`（复制副本至 `Assets/StreamingAssets/mas3d/anchors.json` 并重建 `Main.unity`）。

## 1. 动手前取证：`smoke` 的 10 帧出生状态与几何清查

- [x] 1.1 在修改任何生产代码或既有测试前，对当前 `Main.unity` `Boot()` 后的连续 10 帧运行临时诊断探针（取证后撤回探针代码），逐帧记录并输出 `frame`、`Time.deltaTime`、`player.transform.position`、`player.Grounded`（`CharacterController.isGrounded`）、以及胶囊到 `mesh_216` / `mesh_217`（与地面 `mesh_397.004`）的最近表面距离；验证产出为 10 帧完整实测日志及定量误差拆分结论（多少米来自出生悬空跌落/几何推离，多少米来自 `spawn` 记录时机与帧长抖动）。
- [x] 1.2 在只读模式下检查 `mita-room.glb` 全 107 个 `mesh_###` 节点的包围盒、面法线朝向与材质，完成角色与壳体映射表（地板 `mesh_397.004`、闭合壳体墙/裙边、床 `S2`、座面 `S1`、书桌/柜/门/窗，以及 `[0.30, 1.00]` 高度带内 `size.y < 0.07 m && min.y > 0.07 m` 的薄悬空件）；验证产出为完整已核对的 `mesh_###` 映射清单，确认不含猜测字段。

## 2. 修复壳体白名单、重算锚点与边表

- [x] 2.1 更新 `apps/unity/Assets/Scripts/RoomColliders.cs` 中的 `IsShell` 白名单：保留旧 `Mesh_0###out` 与前缀兼容，纳入 1.2 核实的新卧室闭合墙/地/裙边 `mesh_###`；验证通过 `PlayerWalkTests`（及 EditMode 编译器检查）确认壳体节点均附加 `inner-collider`。
- [x] 2.2 更新 `apps/unity/Assets/Editor/CliAnchors.cs` 中的角色 `mesh_###` 常量与 `RoleTargets` 相对参照，然后运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity.ps1 anchors` 重新生成 `apps/unity/Anchors/anchors.json`；验证 CLI 退出码为 `0`，且 JSON 中全部站立锚点满足 `wallClear >= 0.35`、`nearestClear >= 0.45`、全部 `A1`–`A5` 与 `S1`–`S2` 落在室内地板区域内。
- [x] 2.3 运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity.ps1 player` 将 `apps/unity/Anchors/anchors.json` 复制到 `apps/unity/Assets/StreamingAssets/mas3d/anchors.json` 并重建 `apps/unity/Assets/Scenes/Main.unity`；验证 CLI 退出码为 `0` 且两个 `anchors.json` 完全一致。
- [x] 2.4 更新 `apps/unity/Assets/Scripts/Room/EdgeTable.cs` 的绕行拐点、`Links` 与 `SeatStands`（及 `EdgeTableTests.cs` 的 `S2` 床尾 `+Z` 方向断言、`RoomDress.cs` 的 `ceiling-glow` `±0.36` 分布），然后运行 `AvatarWalkTests` 与 EditMode 测试；验证 `EveryEdgeClearsTheRealRoom` 按 `0.30 m` 胶囊扫掠全边 `0` 碰撞且 `EdgeTableTests` / `RoomLightTests`（除刻意忽略项）全通过。

## 3. 更新 PlayMode 测试与 `smoke` 落稳时序

- [x] 3.1 更新 `apps/unity/Assets/Editor/CliAnchors.cs` 中 `S1` 纵向座位落点（脱离床尾 `mesh_216`）及 `apps/unity/Assets/Tests/PlayMode/RoomAnchorTests.cs` 的 `Walkable` 集合为新卧室地板 `mesh_###`，重建锚点后运行该夹具；验证 `StandingAnchorsClearTheCapsule` 与 `SeatsRestOnTheirSeatWithoutPenetration` 均通过。
- [x] 3.2 更新 `apps/unity/Assets/Tests/PlayMode/PlayerWalkTests.cs` 的 `Walkable` 及新房间走廊/边界撞墙路线（`PlayerWalksFromTheBedFootToTheDeskFront` 与 `PlayerCannotWalkThroughTheWall`）并运行该夹具；验证 `EveryAnchorSpawnsOnTheFloor` 与 `PlayerCannotWalkThroughTheWall` 通过（墙 `inner-collider` 可靠阻挡且未跌破地板）。
- [x] 3.3 更新 `apps/unity/Assets/Scripts/Avatar/AvatarPose.cs` 中 `Table` 坐姿腿/脚弯折角度以适配 `0.338 m` 低座高，并更新 `apps/unity/Assets/Tests/PlayMode/AvatarSeatTests.cs` 的 `BenchName` 为 `mesh_217`（及坐姿脚踝参照判据）运行该夹具；验证不穿地板且全夹具通过。
- [x] 3.4 更新 `apps/unity/Assets/Tests/PlayMode/AvatarWalkTests.cs` 的 `Walkable` 与 `AThinPartThatFloatsIsNotExemptFromTheSweep` 中的薄悬空 `mesh_###` 并运行该夹具；验证薄悬空件不被豁免且全夹具通过。
- [x] 3.5 重标定 `ActionPoses`、`PlayerReach` 与 `ActionDirector` 适配 `S1 = mesh_217`（`topY = 0.338 m`、`facingY = 0.6°`），并更新 `ActionDirectorTests` / `ActionKissTests` 中硬编码的旧朝向断言（改为基于 `seat.facingY`）；验证 `ActionDirectorTests`、`ActionHotkeysTests`、`ActionPatTests`、`ActionKissTests`、`PerformanceMotionTests`、`PlayerLegsTests`、`PlayerPostureTests` 全部通过。
- [x] 3.6 根据 1.1 的 10 帧实测结论更新 `apps/unity/Assets/Tests/PlayMode/ActionSmokeTests.cs` 中「记录 `spawn` 之后先落稳一帧再进场」的时序（保持 `< 0.05 m` 阈值不放宽），并运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity.ps1 smoke`；验证 `Smoke.xml`、`SmokeActions.xml` 与 `Logs/smoke-actions-marker.txt` 全部通过（5 动作均写出 ok 标记）。

## 4. 全量回归验收

- [x] 4.1 按最终顺序核验全链路：依次运行 `openspec validate recalibrate-bedroom-anchors`、`tools\unity.ps1 anchors`、`tools\unity.ps1 player`、`tools\check.ps1`，核查资产文件零改动、`TestResults/EditMode.xml` 中 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 保持 1 skipped 其余全绿、`TestResults/PlayMode.xml` `failed="0"`、`TestResults/SmokeActions.xml` 全通过，并将真实输出贴给用户。

## Workflow follow-up

- 待用户验收第 0 项结果后，再按指令决定是否提交或通过 `/openspec-archive-change` 归档，再继续第 1 项（平层三扇门开/关交互）。
