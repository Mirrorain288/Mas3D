# Design

## Context

`apps/unity/Assets/_Local/room/mita-room.glb`（GUID `5f50f9168985d6744a555e9c8644a77e`）已替换为 `.blend` 导出的新卧室（107 个 `mesh_###` 节点，按 `CenterBottom` 平移 `[-0.062, +0.033, +0.102]` 后，地面 `mesh_397.004` 范围约为 `x ∈ [-1.981, 1.978]`、`z ∈ [-1.895, 2.250]`、`y ∈ [0.027, 0.032]`），而 `CliAnchors.cs`、`RoomColliders.cs`、`EdgeTable.cs`、`Anchors/anchors.json`、`StreamingAssets/mas3d/anchors.json` 及 `Tests/PlayMode/` 中的 6 条用例仍沿用旧资产的 `Mesh_0###out` 命名与约 `7.0 m × 6.7 m` 空间坐标。

**既有不可变约束**：
- 不改动、不重导 `mita-room.glb` 与全屋平层 `mita-flat.glb`。
- 不改动 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的刻意 `Assert.Ignore`（HANDOFF 已知缺口）。
- 不改动多房间拼装脚本 `RoomLayout.cs`（`CliHouse` 用途）。
- 不放宽离墙 `>= 0.35 m`、离静态碰撞体 `>= 0.45 m`、扫掠半径 `0.30 m` 与 `Cancel()` 回位 `< 0.05 m` 判据。

## Goals / Non-Goals

**Goals:**
- 用真实 10 帧 PlayMode 实测数据拆清 `smoke` 中 `Cancel()` 回位偏差（`0.1146 m` / `0.1800 m`）的来源占比。
- 按 `mita-room.glb` 新几何完成 `RoomColliders.IsShell`、`CliAnchors.cs`、`EdgeTable.cs` 与 `Tests/PlayMode/` 适配。
- 按 `tools/unity.ps1 anchors` → `tools/unity.ps1 player` 产线重建 `Main.unity` 与两个 `anchors.json` 文件，使 `unity-play` 6 条红用例与 `smoke` 全绿且不新增失败。

**Non-Goals:**
- 不重新导出任何房间资产或修改贴图/材质/灯光。
- 不解除 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的 `Assert.Ignore`。
- 不实现平层三扇门开/关、二层楼梯/封堵或窗外星空片裁剪（后续任务）。

## Decisions

### D1：动手改代码前先取证 `smoke` 的 10 帧出生状态
`ActionSmokeTests.EveryActionPlaysOnceThroughTheScene` 在 `WaitForBoot` 后于第 37 行先记录 `spawn`，再执行 `player.SetPosture(step.Posture); yield return null;`（第 60–61 行）跑一帧物理，之后才调用 `TryEnterAction` 记录 `_wasSpot`，`Cancel()` 后又 `yield return null`。新几何下旧 `A2 (-0.724, 0.10, -0.028)` 悬空高于地面 `0.068 m` 且距 `mesh_216` 角点约 `0.263 m < 0.30 m`。动手修改前先运行 10 帧诊断探针，逐帧记录 `position`、`Grounded`、以及到 `mesh_216`/`mesh_217`（及地面 `mesh_397.004`）的距离，实测拆分「出生悬空跌落/推离」与「记录时机/帧长抖动」各自的米数后再动手修改。

### D2：`RoomColliders.IsShell` 与 `CliAnchors.cs` 的 `mesh_###` 映射
在 `RoomColliders.cs` 中保留旧 `Mesh_0115out / Mesh_0116out / Mesh_0140out` 与前缀匹配，补充新卧室闭合外向壳体（地板 `mesh_397.004`、外墙 `mesh_204`/`mesh_408.001` 及核实后的其余壳体网格）到 `Shell` 集合，使 `inner-collider` 正确阻挡穿墙与穿地。同步在 `CliAnchors.cs` 将 11 个旧 `Mesh_0###out` 常量与 `RoleTargets` 更新为新卧室 `mesh_###` 角色与相对参照，严格保持 `WallClearance = 0.35f` 与 `FurnitureClearance = 0.45f`。

### D3：产线顺序与 `EdgeTable` / `RoomDress` / 测试适配
严格执行 `tools/unity.ps1 anchors`（写 `apps/unity/Anchors/anchors.json`）→ `tools/unity.ps1 player`（抄写 `apps/unity/Assets/StreamingAssets/mas3d/anchors.json` 并烘焙 `Main.unity`）。依据新 `anchors.json` 更新 `EdgeTable.cs` 的拐点、`Links` 与 `SeatStands`（`S2` 床头位于 `-Z` 靠墙、床尾朝 `+Z`，故保持 `S2.facingY = 0°` 并将 `SeatStands["S2"] = 2.80f` 落在 `+Z` 过道并连向 `W2`/`A5`，同步更新 `EdgeTableTests.SeatNodesStandOnTheFloorOffTheSeat` 的床尾步出方向断言），使 `0.30 m` 胶囊扫掠全图 0 碰撞；微调 `RoomDress.cs` 中 `ceiling-glow` 横向分布比例 `±0.33` → `±0.36` 以在 `4.372 m` 宽的新房间保持 `> 3.0 m` 照明跨度；随后按真实角色网格与边界更新 `RoomAnchorTests.cs`、`PlayerWalkTests.cs`、`AvatarSeatTests.cs`、`AvatarWalkTests.cs`，并按 D1 取证结果修正 `ActionSmokeTests.cs`「记录 `spawn` 之后先落稳一帧再进场」的时序。

### D4：`S1 = mesh_217`（`0.338 m / 0.6°`）坐姿与双人动作重标定
旧卧室 `S1` 为 `topY = 0.604 m`、`facingY = 270°` 的独立长凳；新卧室中仅床尾脚凳 `mesh_217` 可作 `S1`（`topY = 0.338 m`、`facingY = 0.6°`、紧邻床体 `mesh_216`）。保留 `S1 = mesh_217` 并重标定：
1. `CliAnchors.cs` 将 `S1.z` 适当向脚凳前缘微移，使 `SeatsRestOnTheirSeatWithoutPenetration` 中 `SeatedRadius = 0.22 m` 胶囊脱离床尾 `mesh_216`；
2. `AvatarPose.Table` 调整大小腿与脚踝弯折角度，适应 `0.338 m` 低座面（脚部不穿出地板且身体不沉入座面）；
3. 重标定 `PlayerReach.Raised`、`ActionPoses`（`PatHer`/`PatMe`/`Kiss`）、`ActionDirector`（`PatMeOffset`/`KissOffset` 及 `Kiss` 机位与头高俯身适配），并将 `ActionDirectorTests`/`ActionKissTests` 中写死的 `90°`/`180°` 世界朝向改为按 `seat.facingY` 推算，不放松接触精度（`pat-her`/`pat-me <= 0.03 m`，`kiss <= 0.06 m` 且呼吸保持 `<= 0.02 m` / `> 0.01 m`）。

## Risks / Trade-offs

- **房间尺度缩小带来的通道与分离度紧张**：新卧室仅约 `4.0 m × 4.15 m`（原约 `7.0 m × 6.7 m`），`MinAnchorSeparation = 0.60 m`、`WallClearance = 0.35 m`、`FurnitureClearance = 0.45 m` 下部分默认角色偏移可能超出 `SnapRadius = 1.20 m` 或切过中央家具，需通过调整 `CliAnchors.RoleTargets` 偏移与 `EdgeTable` 绕行拐点解决，而非放宽任何净空判据。
- **双面壳体误判风险**：只对经法线/Raycast 确认外向绕向的墙、地与屋顶裙边启用 `IsShell`，避免为双面家具增加冗余内向碰撞体。
