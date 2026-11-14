# scene/unity-bedroom Delta Specification

## ADDED Requirements

### Requirement: 默认单间卧室资产与既有已知忽略用例保持只读边界

系统 MUST 保留已导入的 `.blend` 导出资产 `apps/unity/Assets/_Local/room/mita-room.glb`（GUID `5f50f9168985d6744a555e9c8644a77e`）与平层资产 `mita-flat.glb` 不变，且 MUST NOT 通过重导资产来迎合旧测试；系统 MUST NOT 修改或移除 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 中的 `Assert.Ignore`。

#### Scenario: 资产与灯光忽略门禁不被改写
- **WHEN** 本变更实施完成并执行 `tools\check.ps1`
- **THEN** `mita-room.glb` 与 `mita-flat.glb` 文件未发生任何修改，且 `TestResults/EditMode.xml` 中 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 仍保持 `Skipped`（`Assert.Ignore`）而其余 EditMode 用例全部通过

### Requirement: 新卧室闭合壳体网格启用双面内向碰撞体

`RoomColliders.IsShell`（`apps/unity/Assets/Scripts/RoomColliders.cs`）SHALL 在保留旧 `Mesh_0###out` 与前缀兼容的同时，将新卧室 `mita-room.glb` 中作为室内边界的外向闭合壳体网格（地板、墙体及必要壳体裙边 `mesh_###`）纳入壳体集合，使 `RoomColliders.Attach` 为其生成反转绕向的 `inner-collider`，防止玩家胶囊体在室内穿透墙面或跌出地板。

#### Scenario: 玩家向新卧室墙面直线行走被可靠阻挡
- **WHEN** 玩家在启用 `RoomColliders.Attach` 的新卧室中从可站立锚点向室内边界墙行走
- **THEN** 玩家胶囊体被墙体 `inner-collider` 阻挡在室内边界以内（不穿墙，且不跌破地板）

### Requirement: 卧室锚点表按新几何求解并沿既定产线落盘

`CliAnchors.Solve`（`apps/unity/Assets/Editor/CliAnchors.cs`）SHALL 基于 `mita-room.glb` 的 `mesh_###` 语义映射求解室内矩形 `interior`、站立锚点（`A1`–`A5`）与座位（`S1`–`S2`），输出到 `apps/unity/Anchors/anchors.json`；再由 `CliPlayer.BuildScene` 将其复制至 `apps/unity/Assets/StreamingAssets/mas3d/anchors.json` 并烘焙 `Main.unity`。每个站立锚点 MUST 满足：
1. 距室内墙体边界 `>= 0.35 m`；
2. 距所有非地面覆盖静态障碍物碰撞体 `>= 0.45 m`；
3. 胶囊体（半径 `0.30 m`、高 `1.70 m`）与房间碰撞体零重叠，且正下方存在有效地板碰撞表面。

#### Scenario: 锚点求解与 PlayMode 碰撞核验通过
- **WHEN** 依次执行 `tools/unity.ps1 anchors` 与 `tools/unity.ps1 player` 后运行 `RoomAnchorTests` 与 `PlayerWalkTests`
- **THEN** `Anchors/anchors.json` 与 `StreamingAssets/mas3d/anchors.json` 内容一致，`StandingAnchorsClearTheCapsule`、`SeatsRestOnTheirSeatWithoutPenetration`、`EveryAnchorSpawnsOnTheFloor` 与 `PlayerCannotWalkThroughTheWall` 全部通过且阈值未被放宽

### Requirement: 角色行走边表与座位扫掠零碰撞

`EdgeTable`（`apps/unity/Assets/Scripts/Room/EdgeTable.cs`）SHALL 按新卧室的锚点、家具布局与座位朝向更新绕行拐点、`Links` 与 `SeatStands`。在真实房间碰撞体中对 `EdgeTable.Edges` 按 `0.30 m` 半径逐段扫掠时 MUST 为 `0` 碰撞；`AvatarSeatTests.NothingSinksIntoTheBench` MUST 绑定新卧室的实际 `S1` 凳/座网格校验无穿模；`AvatarWalkTests.AThinPartThatFloatsIsNotExemptFromTheSweep` MUST 绑定新卧室中实际存在的薄悬空网格（`size.y < 0.07 m` 且 `min.y > 0.07 m`）证明薄件豁免不会放过悬空障碍物。

#### Scenario: 新卧室边图扫掠与座椅校验全绿
- **WHEN** 在 PlayMode 中运行 `AvatarWalkTests` 与 `AvatarSeatTests`
- **THEN** `EveryEdgeClearsTheRealRoom`、`AThinPartThatFloatsIsNotExemptFromTheSweep` 与 `NothingSinksIntoTheBench` 全部通过

### Requirement: 场景动作冒烟 Cancel 回位满足 0.05 m 硬判据

`ActionSmokeTests.EveryActionPlaysOnceThroughTheScene`（`apps/unity/Assets/Tests/PlayMode/ActionSmokeTests.cs`）MUST 保持 `< 0.05 m` 回位判据不放宽。实施改动前必须先通过 `Boot()` 后连续 10 帧的实测日志（位置、`Grounded`、与 `mesh_216`/`mesh_217` 距离）量化确认出生悬空与记录时机各自的误差贡献；随后仅修正记录 `spawn` 后先落稳一帧再进场的时序（配合已重算的无碰撞 `A2` 出生点），确保五个动作全部完成进场、`1.5 s` 保持及 `Cancel()` 复位至距进场前 `spawn` `< 0.05 m` 内。

#### Scenario: 冒烟五动作复位偏差均小于 5 cm
- **WHEN** 在重建后的 `Main.unity` 上执行 `tools/unity.ps1 smoke`
- **THEN** `ActionId.All` 五个动作均写入 `Logs/smoke-actions-marker.txt` 合格标记，且每次 `Cancel()` 后玩家与落稳后 `spawn` 位置的距离 `< 0.05 m`

### Requirement: S1 低脚凳坐姿与五个交互动作重标定不穿模不失触
保留 `S1 = mesh_217`（`topY = 0.338 m`、`facingY = 0.6°`）作为默认座位时，`AvatarPose.Table` MUST 适应 `0.338 m` 座高使全身最低点 `>= -0.01 m`（不穿地板）且不沉入 `mesh_217`；`ActionPoses`、`PlayerReach` 与 `ActionDirector` SHALL 按 `mesh_217` 实际座高与朝向完成重标定，同时保持 `PlayerPostureTests` 既有姿态门禁（含蹲下 `1.05 m`、跪姿 `1.28 m`、牵手掌心 `0.62 m`）及各动作接触/对齐容差不变。

#### Scenario: 坐姿与动作夹具在新 S1 脚凳上全绿
- **WHEN** 在 PlayMode 运行 `AvatarSeatTests`、`PlayerPostureTests`、`PlayerLegsTests`、`ActionDirectorTests`、`ActionHotkeysTests`、`ActionPatTests`、`ActionKissTests` 与 `PerformanceMotionTests`
- **THEN** 以上全部夹具通过，`S1` 坐姿无穿模，且 `hands` / `pat-her` / `pat-me` / `kiss` / `sit-together` 均达成原有接触精度、朝向对齐与呼吸保持容差
