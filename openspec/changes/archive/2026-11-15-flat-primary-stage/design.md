# Design

## Context

参见 [proposal.md](./proposal.md) 的背景说明。技术现状与上游对齐点如下：
- 工程当前未安装 `com.unity.ai.navigation`（`apps/unity/Packages/manifest.json` 仅含基础模块），全部卧室走位由 [EdgeTable.cs](../../../apps/unity/Assets/Scripts/Room/EdgeTable.cs) 配合 [AnchorTable.cs](../../../apps/unity/Assets/Scripts/AnchorTable.cs) 及 `AvatarWalker` 的直连边胶囊扫掠验证支撑。
- 平层 `Flat.prefab` 已按 `1.5×` 尺度烘焙完成，具备三扇可绕合页旋转 `0.35 s` 打开 `-90°` 的 `InteractiveDoor`（含门把手 `HandleMesh`），但尚无专属平层锚点与座位配置，`RoomBootstrap` 仅通过 `-flat 1` 挪动玩家到旧卧室端朝向，未把角色放入平层。
- 全局演进顺序确定为：`① 平层主舞台化（本次） → ② 光影与渲染打磨 → ③ 拟人化动作底座（Kimodo 离线动作 + MotionPlan 语义序列） → ④ 跨房间扩展玩法`，另有“语义控制表情（LumenAct：<https://github.com/AaBb-coder/LumenAct>）”因需先在 Blender 中手动新增 **118 个形态键**而作为独立 `[Blocked]` 待办项保持解耦。

详细技术规格原型见 [2026-11-15-flat-primary-stage-design.md](../../../docs/superpowers/specs/2026-11-15-flat-primary-stage-design.md)，对应 TDD 执行细则见 [2026-11-15-flat-primary-stage.md](../../../docs/superpowers/plans/2026-11-15-flat-primary-stage.md)。

## Goals / Non-Goals

**Goals:**
- 以最小破坏将 `FlatRoom` 升格为默认运行主场，客厅三人位大沙发作为开局迎面焦点与五项交互动作主座。
- 将 `AnchorTable` 与 `EdgeTable` 通用化为“兼容旧 `anchors.json`、支持平层 `anchors-flat.json` 多房间与门洞属性”的配置化图结构。
- 让 `AvatarWalker` 在跨室经过闭合的 `Door_BedroomLiving` / `Door_LivingKitchen` 时，依托现有 `TwoBoneIk` 单臂伸向真实门把手中心配合 `InteractiveDoor.SetOpen(true)` 平滑开通行。
- 新增克制的 `AvatarRoutine`，在纯空闲时低频轮换三大非卫浴生活区，可被对话和动作抢占，且可在测试里确定性步进或一键禁用。

**Non-Goals:**
- 不引入 `NavMesh` / `com.unity.ai.navigation`。
- 不重做平层分房间灯光、闭合壳体阴影与窗外夜色风景（留给步骤 ②）。
- 不接入 `Kimodo` `.bvh` 骨骼动画资产或带脚步联动的全身推门动作，不消费 `ConversationController` 中的 `MotionPlan`（留给步骤 ③）。
- 不实现喝咖啡、递书/翻书道具及长段共读邀约玩法（留给步骤 ④）。
- 不接入 `LumenAct` 118 形态键，不改变 `mita-room.glb` / `mita-flat.glb` 或 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的跳过边界。

## Decisions

### 1. 跨房间导航：配置化锚点路网（扩展 `AnchorTable` + `EdgeTable`）而非 `NavMesh`
- **选择**：保持零新包依赖，给 `AnchorTable` 增加可选字段（`room`、`waypoints`、`links`（含 `doorId`）、`defaultSpawn`、`SeatEntry.pose/standGap/intimate`），离线运行 `CliFlatAnchors.Solve` 从已烘焙 `1.5×` 的 `Flat.prefab` 真实碰撞体求解并执行 `0.30 m` 胶囊全图零碰撞扫掠后产出 `anchors-flat.json`。当活跃表未填写 `links` 时 `EdgeTable` 自动维持旧单卧硬编码构图。
- **备选与权衡**：引入 `com.unity.ai.navigation` 对 400+ 细分网格（餐椅脚、吧台凳、茶几底、`0.91 m` 窄门框）烘焙 `NavMeshSurface` 易产生卡边或门区抖动，还会使现有 `EdgeTable` 扫掠证明契约失效；缺点是未来增改平层家具后需重跑一遍 `CliFlatAnchors.Solve` 刷新配置。

### 2. 默认入口切换与老测试兼容：默认平层 + `-bedroom 1` / `ShowBedroom()` 回退 + 活跃表生命周期隔离
- **选择**：`RoomBootstrap.Boot()` 默认激活 `FlatRoom` 并切换到 `mas3d/anchors-flat.json`（保留 `ShowFlat()` / `-flat 1` 为兼容无害入口）；新增 `-bedroom 1` 与 `ShowBedroom()` 供既有明确校准在旧单卧几何上的回归夹具调用；`AvatarActor.Start()` 在场景存在未完成引导的 `RoomBootstrap` 时礼让延后落座；`RoomBootstrap.OnDestroy()` 重置活跃表与 `EdgeTable` 缓存，杜绝跨测试状态污染。
- **备选与权衡**：直接在第一步废除旧卧室模式会把大量依赖 `mesh_217`/`mesh_216` 精确姿态容差的既有测试强行拉进第一步重写，混淆里程碑边界。

### 3. 闭合室内门通行与单臂 IK 分层
- **选择**：由 `InteractiveDoor` 提供 `HandleWorldPosition()` 返回 `HandleMesh` 真实渲染/碰撞中心；当 `AvatarWalker` 行至带 `doorId` 的边段且门尚未全开时暂停前进、平滑面向门、`SetOpen(true, false)`，并在 `LateUpdate` 与步进末尾使用 `TwoBoneIk.Solve` 驱动单臂朝向门把手中心至门开稳后释放继续前行。
- **备选与权衡**：全身迈步联动推门依赖完整骨骼动画剪辑与全身根节点配合，延后到步骤 ③ `Kimodo` 动作管线落地；本步以单臂 `TwoBoneIk` 即可达成自然抓手开门观感与零穿模通行。

### 4. 非兼容外围点位上的 1–5 互动策略
- **选择**：客厅大沙发主座（`intimate == true`）直接承载 `hands` / `pat-her` / `pat-me` / `kiss` / `sit-together`；当她正在行走或停留在卧室阅读椅、餐厨吧台等通道受限点位时，`ActionDirector.ResolvePlan` 直接安全拒绝并返回明确中文提示，不做穿家具强行下跪或瞬移。

### 5. 静谧空闲流转与抢占控制
- **选择**：新增 `AvatarRoutine`（支持 `Enabled`、`IdleSeconds`、`Tick(dt)`），仅在完全无对话、无输入聚焦、无动作演出、无行走时累计计时并在非卫浴池内顺序/轮换前往下一点位；通过对话状态检查与 `AvatarWalker` 既有“说话优先于走路”逻辑实现瞬间礼让，可在无头单测和静态冒烟场景中显式关闭或时间步进。

## Risks / Trade-offs

- **客厅大沙发与茶几净空不足以容纳下跪/蹲起偏移** → 由 `CliFlatAnchors.Solve` 在构建配置阶段实测茶几碰撞体与大沙发之间走廊宽度，`HandsOffset` / `PatMeOffset` / `KissOffset` 立足点必须在碰撞检查阶段硬性验绿（若需要微调 `defaultSpawn` 与沙发前通道，必须在求解脚本层面早暴露）。
- **`Main.unity` 加载后立即卸载导致的 `EdgeTable` 缓存陈旧** → `RoomBootstrap.OnDestroy()` 与 `SceneFlow.UnloadMain` 双向清理 `AnchorTable.ResetActive()` 及 `EdgeTable.Rebuild()`，并由专项测试回归看护。
- **既有依赖 `SceneFlow.LoadMain()` 的单卧室测试受默认切换影响** → 清晰区分“面向默认平层的用例”（如 `ActionSmokeTests`、`FlatCeilingTests`、`FlatDoorTests`、`FlatActionTests`）与“面向旧卧室几何标定的用例”（通过 `SceneFlow.LoadBedroom()` / `ShowBedroom()` 运行 `BedroomSkyCardTests` 等）。

## Migration Plan

1. 扩展 `AnchorTable`、`BootArgs` 与 `EdgeTable` 兼容旧单卧无参行为；
2. 用 `CliFlatAnchors.Solve` 生成并通过全平层零碰撞扫掠校验的 `anchors-flat.json`；
3. 实现 `InteractiveDoor.HandleWorldPosition()` 与 `AvatarWalker` 单臂推门，PlayMode 扫掠与推门用例验绿；
4. 将 `RoomBootstrap` 默认模式切至平层，完成 `AvatarActor` 时序隔离、`ActionDirector` 客厅沙发适配、`ActionSmokeTests` 与旧单卧夹具迁移；
5. 加入 `AvatarRoutine` 空闲流转与抢占测试，最后重烘 `Main.unity` 并跑通 `tools/check.ps1` 与首帧视觉截图确认。
