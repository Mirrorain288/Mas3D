# 平层主舞台化与全局演进路线 · 设计

- 日期：2026-11-15
- 状态：待用户审阅批准（批准前不拆写执行计划与 OpenSpec 变更，不修改产品代码）
- 规模：阶段一（整体四步演进计划的第一步 + 独立阻塞项追踪）

---

## 1. 全局四步演进路线与独立阻塞项

为把后续开发按"空间底座 → 光影质感 → 动作表现 → 玩法扩展"稳步推进，先明确全局顺序与边界：

1. **步骤一（本阶段）：平层主舞台化**
   将 5 区域平层 `Flat.prefab`（卧室、客厅/玄关、餐厅、开放式厨房、卫浴）升格为游戏默认主场；构建配置化多房间锚点与路网、客厅大沙发迎面开局、双门跨室单手 IK 推门通行、以及极克制的空闲换区流转；保留 `-bedroom 1` / `ShowBedroom()` 供既有单卧回归测试继续运行。
2. **步骤二：光影与渲染打磨**
   用按房间语义布置的灯具替代当前平层调试用单排点光源；理顺 `mesh_###` 闭合壳体阴影与既有 `RoomLightTests` 的边界；采用安全可靠的接触阴影（避开 URP SSAO 在角色脸部的条纹/异常问题）；补齐平层窗外夜色景观与透窗光效（承接步骤一设好的望窗点位）。
3. **步骤三：拟人化动作底座**
   采用"离线生产 + 运行时轻量驱动"组合：用 `Kimodo`（`D:\kimodo`）离线生成 `.bvh` 动作并重定向为 Unity Humanoid 动作资产，配合现有 `TwoBoneIk` 锁住门把手、牵手、摸头等精确接触点，接通 `MotionPlan` 协议实现动作序列与对话生成解耦（不把 `Kimodo` 重容器和 `HAST-SEC` 本地 Qwen-0.8B 重叠对话能力强绑进运行时）；在此步骤将单手推门升级为带脚步协同的全身推门动作。
4. **步骤四：跨房间扩展玩法与日常陪伴**
   丰富恬静淡雅的日常陪伴玩法：跨房间牵手散步、喝咖啡、共读诗书（含递物/道具与主动起身邀约等长段表现），串联卧室阅读角、客厅沙发与餐厨咖啡区的多区生活循环。
5. **独立阻塞待办项 — 语义控制表情（`LumenAct`） `[Blocked]`**
   - 项目链接：<https://github.com/AaBb-coder/LumenAct>
   - 当前状态：阻塞中（独立跟踪，不阻塞步骤 ①–④ 主线推进）
   - 阻塞原因：须先在 Blender 中为角色模型手动添加 **118 个形态键（shape keys）**，模型侧就绪前无法启用测试与语义表情驱动。
   - 过渡方案：阻塞解除前，角色面部继续沿用现有 VRM `ExpressionKey`（`Blink`、`Happy`、`Sad`、`Relaxed`）及 TTS 口型同步。

---

## 2. 本阶段意图、目标、非目标与约束

### 2.1 意图与为什么

当前 `Main.unity` 默认启动单间卧室（`mita-room.glb`），`FlatRoom`（`Flat.prefab` / `mita-flat.glb`）仅在命令行带 `-flat 1` 或测试调用 `ShowFlat()` 时临时切入：不仅没有专属的平层锚点与座位表（[RoomBootstrap.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/RoomBootstrap.cs) 仍加载单卧 `anchors.json`），`Actor` 也没落进平层坐标，[EdgeTable.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Room/EdgeTable.cs) 的行走图更是写死在旧单卧的 5 个锚点与 2 个座位上。

要让整个平层真正"活成她和你的日常空间"，第一步需要先把"能住、能逛、能坐、能推门穿室、空闲时会安静换换角落"的主舞台地基立稳，而刻意不把光影重调和动作资产改造掺进这一步里。

### 2.2 本阶段目标

1. **平层默认开局与兼容兜底**：无额外参数启动 `Main.unity` / `CliPlayer` 即直接进入平层；显式传入 `-bedroom 1` 或调用 `ShowBedroom()` 时切回默认单间卧室以保住既有单卧回归测试（同时维持原有 `ShowFlat()` 无害兼容）。
2. **客厅大沙发迎面开局**：玩家出生在客厅茶几前正对三人位大沙发（`mesh_211.002` / `mesh_218.002`），她初始安静坐在大沙发主座上；左手边室内门通往卧室、右手边室内门通往餐厨，三扇室内门开局初始均处于关闭状态。
3. **生活区点位与卫浴边界**：为三大日常区配置锚点与座位，所有坐标均通过平层几何求解校验无穿模与悬空：
   - **卧室**：粉色阅读椅（独处阅读座）、电脑桌前、床边/床位、`-Z` 侧望窗点；
   - **客厅/玄关**：三人位大沙发（开局主座与"并排同坐"共读座）、角落绿色双人沙发、两扇临窗望景点；
   - **餐厅/开放式厨房**：四人餐桌（`mesh_407.001` / `mesh_623`）旁或岛台高脚吧台凳（喝咖啡/翻书座）、料理台与靠窗绿植旁；
   - **卫浴间**：`Door_BathBedroom`（`mesh_341`）保留供玩家 `F` 键或点击自由开关查看，第一阶段**不在卫浴内部安排她的自主点位**。
4. **配置化多房间路网与单手推门 IK**：扩展路网从 JSON 读入平层各区节点、拐点与门洞边段；当 `AvatarWalker` 经过 `Door_BedroomLiving`（`mesh_238` / `mesh_237`）或 `Door_LivingKitchen`（`mesh_438` / `mesh_297`）遇到关门状态时，先停步面向门，单臂 `TwoBoneIk` 伸向门把手 `HandleMesh` 并触发 `InteractiveDoor.SetOpen(true)`（`0.35 s` 旋转 `-90°`），待门让开后走过再松手。
5. **1–5 互动适配客厅主座**：`ActionDirector`、`ActionSeats` 与 `AvatarActor` 改读当前活跃锚点/座位表；在客厅三人位大沙发主座下，`hands` / `pat-her` / `pat-me` / `kiss` / `sit-together` 全部可用、无穿模，且按 ESC `Cancel()` 复位准确。
6. **极克制的空闲自主换区流转**：无对话、无 1–5 互动、且保持足够长时间静止空闲后，她会在上述三大生活区的有效点位间慢速切换（遇到关着的门也会推门过去）；对话开始或互动进入时按现有"说话优先于走路"原则立即让步停下；测试环境下提供显式开关确保可确定性关闭或步进。

### 2.3 非目标（明确切给后续阶段）

- 不接入 `com.unity.ai.navigation` / `NavMesh`（[Packages/manifest.json](file:///d:/Mas3D/apps/unity/Packages/manifest.json) 保持现状）；
- 不重构平层各房间灯光、壳体阴影与窗外夜色景观（留步骤 ②）；
- 不引入 `Kimodo` `.bvh` 骨骼动画文件、也不制作带全身迈步的推门动作（留步骤 ③）；
- 不实现端咖啡、翻书道具、"叫我一起读诗"的长段主动邀约剧情，或消费对话 `MotionPlan` 意图帧（留步骤 ③/④）；
- 不接入 `LumenAct` 的 118 形态键（保持 `[Blocked]`）；
- 不修改源导出模型 `mita-room.glb` 与 `mita-flat.glb`，也不移除或改写 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的 `Assert.Ignore`。

### 2.4 不可协商约束

- **真碰撞体校验优先于拍脑袋坐标**：平层所有站立点、座位及跨室边段必须经过 `FlatRoom` 碰撞体的 `0.30 m` 胶囊体扫掠与地表贴地校验，零重叠零穿模；
- **确定性初始化**：`RoomBootstrap.Boot()` 必须在任何读取 `AnchorTable` 的组件落座之前确定当前房间表，避免 `AvatarActor.Start()` 先按旧卧室 `anchors.json` 坐歪；
- **不破坏既有单卧回归面**：原单卧室行为在 `-bedroom 1` / `ShowBedroom()` 下保持绿灯；原 `FlatCeilingTests` 中"默认启动不得激活平层"类与新默认方向直接冲突的断言将在规格确认后按新契约同步更新；
- **仓库规则**：不自行 `git commit` / `git push`；正式开发前通过 `openspec-propose` 沉淀到 `openspec/` 规格。

---

## 3. 架构方案与取舍

围绕**跨室导航**与**默认入口兼容**，比较了三条路线并选定**方案一**：

| 维度 | 选定方案（方案一） | 备选 A（`NavMesh`） | 备选 B（一步切死去单卧） |
|---|---|---|---|
| **寻路核心** | 配置化 `anchors-flat.json` + 扩展 `EdgeTable` / `AvatarWalker` 门洞边段 | 引入 `com.unity.ai.navigation` 运行时烘焙 + `NavMeshAgent` | 同方案一寻路，仅移除单卧切换 |
| **推门触发** | 门前/门后成对节点明确绑定 `InteractiveDoor` 名 + 单臂 IK 抓 `HandleMesh` | 需另搭 `OffMeshLink` + 动态 `NavMeshObstacle` 裁切 | 同左 |
| **单卧回归** | 默认平层，`-bedroom 1` / `ShowBedroom()` 显式切回原单卧 | 需重写旧单卧 `EdgeTable` 测试基准 | 强行废除几十个旧单卧标定或立刻重调全套动作 |
| **决定与理由** | **零新包依赖、每段跨室路径均可胶囊扫掠证明 0 穿模，且不把步骤 ③ 的动作重标定拖进步骤 ①** | 400+ 精细家具与约 `0.91 m` 窄门框易致 NavMesh 卡边抖动 | 过渡干净但会把第三步工作强塞入第一步，回归风险过高 |

代价明确：方案一中她只沿求解验证过的节点与连通边行走，未来若在平层新增或挪动家具，需要重跑一次平层锚点图求解命令刷新配置。

---

## 4. 模块与数据设计

### 4.1 活跃表与配置扩展（`AnchorTable` + `anchors-flat.json`）

在保留现有 `Assets/StreamingAssets/mas3d/anchors.json` 字段结构 100% 向后兼容的前提下：

- 为 [AnchorTable.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/AnchorTable.cs) 增设可选扩展字段：
  - `room`（节点/座位所属生活区：`bedroom` / `living` / `kitchen` 等，便于空闲换区与诊断定位）；
  - `waypoints`（仅通路用的无停留拐点数组：`id`、`x`、`z`、`room`）；
  - `links`（无向边描述数组：`from`、`to`、可选 `doorId` 与推门方向hint，当边跨过室内门时注明对应 `InteractiveDoor` 对象名，如 `Door_BedroomLiving`、`Door_LivingKitchen`）；
  - `defaultSpawn`（可选房间默认出生点与朝向，供平层客厅开局读取）。
- 运行时增加"当前活跃 `AnchorTable`"解析上下文（例如 `AnchorTable.Active` / `ActiveRelativePath`，缺省指向平层 `mas3d/anchors-flat.json`，`-bedroom 1` 模式指向 `mas3d/anchors.json`），让 [RoomBootstrap.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/RoomBootstrap.cs)、[AvatarActor.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Avatar/AvatarActor.cs)、[AvatarWalker.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Avatar/AvatarWalker.cs) 与 [ActionDirector.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Actions/ActionDirector.cs) 始终读取同一份活跃表。
- 平层 `anchors-flat.json` 由编辑器离线求解器基于已烘焙 `1.5×` 缩放后的 `FlatRoom` 真实物理碰撞体计算落点与门两侧门洞节点（确保站在门前 `HandleMesh` 臂长范围内、且通过门洞的连线不刮擦门框与门扇），写盘前做整图胶囊扫掠验收。

### 4.2 启动切换与客厅开局时序（`RoomBootstrap` + `AvatarActor`）

1. `RoomBootstrap.Boot()` 优先解析启动模式：
   - 存在 `-bedroom 1`（或测试调用 `ShowBedroom()`）且存在旧卧室对象时进入**兼容单卧模式**（激活原 `Room` / `room-lights` / `night-sky-card`，停用 `FlatRoom`，设活跃表为 `mas3d/anchors.json`）；
   - 默认情况（含原有 `-flat 1` / `ShowFlat()`）进入**平层主舞台模式**（停用原单卧及其根灯/星空卡，激活 `FlatRoom` 并附着 `RoomColliders`，设活跃表为 `mas3d/anchors-flat.json`）。
2. **客厅迎面开局**：
   - 玩家出生位置改设在平层客厅茶几旁正对三人位大沙发主座（由 `anchors-flat.json` 经碰撞检验给出精确安全坐标，避开茶几碰撞体 `Player` 与大沙发无重叠）；
   - 若 `Actor` 在场景默认激活顺序中先触发过 `Awake`/`Start`，`RoomBootstrap.Boot()` 在切换至平层模式后**显式刷新她的状态**：清除旧位置残余，将她坐定在平层 `S1`（客厅三人位大沙发主座），重建 `Idle` 基线、`FitBody()` 胶囊体、绑定 `Walker`/`Actions`/`Dialogue`/`GazeTarget`。
   - 为避免 `AvatarActor.Start()` 在 `RoomBootstrap` 之前抢先用默认 `anchors.json` 落座，让 `AvatarActor.Start()` 在存在未初始化的 `RoomBootstrap` 时礼让，统一由 `RoomBootstrap.Boot()` 完成首次入座（无 `RoomBootstrap` 的独立单测场景照旧自行入座，不伤单测）。

### 4.3 通用多房间路网与单手推门通道（`EdgeTable` + `AvatarWalker` + `InteractiveDoor`）

- **路网多房间化**：
  [EdgeTable.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Room/EdgeTable.cs) 当活跃表提供 `links`（含 `waypoints`）时按该表动态建图，保留原有 `Node` / `Path`（Dijkstra）/ `Neighbours` 接口与旧卧室静态表回退逻辑（旧卧室无 `links` 时行为一字不差）。每条跨越 `doorId` 的边在边对象上保留门引用名。
- **跨室遇门与单臂 IK 推门状态**：
  当 `AvatarWalker` 即将踏入一条标注了 `doorId` 的边段时：
  1. 若在当前 `FlatRoom` 中查得对应 `InteractiveDoor` 且 `door.IsOpen == false`（或正在关门中）：
     - 暂停沿着路径位移；
     - 平滑转向面向门（使门把手处于她同侧肩膀伸手可及的方向）；
     - 触发 `door.SetOpen(true, immediate: false)` 启动 `0.35 s` 平滑开门旋转（开门期间 `InteractiveDoor` 会按既有逻辑暂关门扇碰撞体以顺畅旋转）；
     - 在开门过渡窗口内（与步进和呼吸 `AvatarIdle` 配合，利用现有 [TwoBoneIk.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Avatar/TwoBoneIk.cs) 对右臂或近门侧手臂的 `UpperArm` / `LowerArm` 施加腕部目标追踪至 `door.HandleMesh` 世界坐标中心），摆腿恢复直立站姿；
     - 等待 `!door.IsMoving && door.IsOpen` 成立，平滑收回手臂权重并继续走过门洞边段。
  2. 若门原本就开着（例如玩家提前按 `F` 推开过），她直接穿过门洞走过，不画蛇添足做假推门。
  3. 无论开着还是刚推开的门洞，前方 `0.30 m` 胶囊防撞检测继续生效：若玩家正好堵在门框中间，她会礼让停步而不是强行挤穿玩家。

### 4.4 1–5 互动动作与客厅大沙发适配（`ActionDirector` + `ActionSeats`）

- **座位映射**：
  在 `anchors-flat.json` 中将客厅三人位大沙发中心主座定为默认主座 `S1`，其右侧/同排的宽大并排同坐区定为 `S2`（或者由 [ActionSeats.cs](file:///d:/Mas3D/apps/unity/Assets/Scripts/Actions/ActionSeats.cs) 按平层大沙发朝向与安全间距生成 `BedPair` 同排伴侣位），确保：
  - `sit-together`（`5` 键）：她与玩家并排坐在客厅三人位大沙发上一起待着，两具身体间距满足原有防穿模要求；
  - `hands` / `pat-her` / `pat-me` / `kiss`（`1`–`4` 键）：当她处于客厅主座 `S1` 时，玩家立足点 `HandsOffset` / `PatMeOffset` / `KissOffset` 在大沙发与茶几之间的通道净空足够完成站/蹲/跪与接触（离线求解阶段硬性校验该通道无茶几穿模）。
- **当她在卧室阅读椅、餐厨吧台等非主座时触发 1–5 互动的处理**：
  为不在步骤 ① 硬凑不安全动作，约定清晰闭环：
  - **若她此刻正在走路**：沿用现有原则拒绝进场并给出明确提示（或先等她停稳）；
  - **若她当前处于其他辅助座位（如卧室粉色椅、吧台凳）或站在窗边**：对于依赖客厅主座通道的动作（`sit-together` 明确迁去客厅大沙发；`1–4` 若当前所处位置前面家具净空不达标），第一步先通过清晰的拒绝说明提示（如 `"需要先回到客厅大沙发"`）或只在其位于兼容客厅座位时进入动作，不强行穿家具下跪/蹲起——把"先自动走回主座再表演"的高级编排留给步骤 ③/④。

### 4.5 克制的静谧空闲自主流转（`AvatarRoutine`）

为让第一步本身就呈现出"安静陪伴、偶尔自己去客厅看窗、回卧室看书、去吧台歇息"的平层氛围，在 `Actor` 上增设一个独立轻组件（暂名 `AvatarRoutine`）：

- **开启与测试隔离**：默认开启（正式平层运行中生效）；提供 `Enabled` 开关与 `DriveInUpdate` 控制，测试夹具或 `-bedroom 1` 模式可一键关闭以保零漂移，同时逻辑按 `Tick(float dt)` 驱动以便单元测试覆盖。
- **静默计时与触发门限**：每次到点停稳或对话结束后重置一个缓慢计时器（如 `45 s – 75 s` 区间，支持确定性配置值方便测试）。仅当下列条件**全部满足**时才自动发起一次 `Walker.GoTo(nextNode)`：
  1. 她当前没有在走动（`!Walker.Walking`）；
  2. 对话不在思考/回复中（`TurnPhase` 处于空闲），且玩家对话框未聚焦输入；
  3. 玩家没有处于任何 `ActionDirector` 动作阶段（`Actions.Current` 为空）；
  4. 玩家不在持续紧贴阻挡路径。
- **选点风格（恬静不进卫浴）**：只在平层生活区预设好的惬意点位间切换（例如：`客厅大沙发 -> 客厅窗边 -> 餐厨吧台/餐桌 -> 卧室阅读椅 -> 卧室窗边 -> 客厅大沙发`），优先轮转去向不反复在同一两平米内来回晃，**严禁排入卫浴间点位**。
- **抢占与让步**：沿用 `AvatarWalker` 现有"说话优先于走路（mouth wins over legs）"策略——玩家发消息开始思考/回答时她立刻停下脚步进入对话姿态（若需触发 1–5 互动则优先保证停步安全）。

---

## 5. 验证与回归收口标准

设计批准并转入实施计划前，预先定死以下必须可跑通的自动化验收标准（正式进入 `openspec-propose` 时写入 specs/tasks）：

1. **平层几何与全图零穿模**：平层求解器生成 `anchors-flat.json`，对三大生活区所有节点与连通边在真实 `FlatRoom` 碰撞体上按 `0.30 m` 胶囊半径扫掠 **0 碰撞**；卫浴无任何她的生活/通路节点。
2. **默认平层开局**：不带 `-bedroom` 启动 `Main.unity` 时，`FlatRoom` 激活、旧单卧及根灯/星空卡停用、玩家在客厅正对大沙发生成、她稳定坐于客厅 `S1` 且无碰撞穿模。
3. **跨室与关门推门 IK**：从客厅 `S1` 调用 `Walker.GoTo` 前往卧室节点与餐厨节点（跨越初始关闭的 `Door_BedroomLiving` 与 `Door_LivingKitchen`），验证到达门前触发门扇平滑打开、IK 手臂朝向把手位置伸出并在开门后顺利穿过抵达目标。
4. **空闲换区与对话抢占**：推进 `AvatarRoutine.Tick(dt)` 超过空闲阈值能自主走去其他生活区点位；处于禁用态（测试开关关掉）或对话进入 `Thinking` 时绝不自主起行或立刻停步。
5. **1–5 动作与旧单卧兼容**：客厅大沙发主座上 `1–5` 动作全部完成进场、`1.5 s` 保持与 `Cancel()` 精确回位；传入 `-bedroom 1` / `ShowBedroom()` 时既有单卧套件全部保持通过（`RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的跳过保持不动）。
