# Design

## Context

详见 `proposal.md - Why`。实测 `帽子米塔的房间.blend` 确认客厅（`客厅` 集合）在 `1.5×` 缩放前仅有外圈吊顶 `mesh_334.001`（`y = 1.77..1.78 m`）、跌级梁 `mesh_328.001` 与中央天花板 `mesh_193`（`y = 1.83 m`，单面向下）；`CliFlat.Map()` 此前按轴对齐包围盒（AABB `size.x * size.z`）统计面积，从而将分散在客厅吊顶四周的 `0.171 m²` 筒灯框 `mesh_303.002` 误记为 `15.0 m²` 二层楼板。同时，`CliPlayer.BuildScene()` 在 `Main.unity` 中把默认单间卧室 `mita-room.glb` 放在世界原点并把 `room-lights` 与 `night-sky-card` 建在场景根部，而 `RoomBootstrap.Boot()` 切入 `FlatRoom` 时未将其停用。

## Goals / Non-Goals

**Goals:**
- 在 `RoomBootstrap.Boot()` 建立默认卧室与备选房间（`FlatRoom`/`HouseRoom`）的严格互斥切换与幂等恢复机制。
- 采用方案 A：保持客厅高空俯视 cutaway 透明性，通过局部纯物理封顶碰撞盒 + 重拍无重叠 `flat-living` / `flat-wide` 确认单层客厅上下封闭、内部仰视无破洞。
- 让 `CliFlat.Map()` 基于真实网格朝上三角形面积判定 `[floor]`，彻底消除 `mesh_303.002` / `mesh_193` / `mesh_188.001` 误报。

**Non-Goals:**
- 不修改或重新导出 `mita-flat.glb` 与 `mita-room.glb` 源资产。
- 不在客厅上方新增实体屋顶上表面渲染盖板（避免 `flat-wide.png` 俯视被白板遮死），也不新建二层楼板或楼梯。
- 不处理第 3 项（默认卧室场景外部取景时裁掉窗外星空卡）或第 4 项（平层角色锚点表）。

## Decisions

### D1：`RoomBootstrap` 缓存默认房间并按场景隔离根级附属对象
- **选择**：在 `RoomBootstrap` 运行期缓存初始默认 `Room`（当当前 `Room` 既不是 `FlatRoom` 也不是 `HouseRoom` 时记录到 `_defaultRoom`），并在切换场景根对象时仅遍历 `gameObject.scene.GetRootGameObjects()` 查找 `"room-lights"` 与 `"night-sky-card"`（记录于 `_defaultLights` / `_defaultSkyCard`）。当 `flatMode || houseMode` 启用时将三者统一 `SetActive(false)`；当回到默认模式时恢复激活并停用 `FlatRoom`/`HouseRoom`。
- **备选与权衡**：只停用 `boot.Room` 会遗留 `room-lights`（导致默认卧室 Directional Light 与点光叠在平层 `FlatLights` 上）及 `night-sky-card`（斜插在平层客厅坐标内）。使用当前所属 `Scene.GetRootGameObjects()` 而非全局 `GameObject.Find`，可避免多场景叠加测试时误关测试夹具对象，同时支持停用后再次唤醒恢复。

### D2：方案 A —— 客厅纯物理封顶 `BoxCollider` 与室内视角核验
- **选择**：在 `CliFlat.Bake()` 完成缩放后为客厅及前部玄关门厅上方（基于实际客厅地板/外墙边界测算 XZ 范围）添加子节点 `LivingCeilingSeal`（仅含薄层 `BoxCollider`，不挂 `MeshRenderer`），使 `y ≈ 2.65..2.75 m` 处存在连续零缝隙物理顶板；待修复 `RoomBootstrap` 重叠并重拍真实 `flat-living.png`（及仰视视角）后，如确实存在内向材质边缘缝隙，再于该位置补内向封边而不增加朝上外遮罩。
- **备选与权衡**：相比方案 B（额外盖上双面渲染白板顶），方案 A 保留了 `flat-wide.png` 从上方审视客厅家具、沙发与门位动线的 cutaway 能力，且物理上同样绝对封死。

### D3：`CliFlat.Map()` 按向上三角形投影面积判 `[floor]`
- **选择**：`CliFlat.Map()` 仍输出全量包围盒到 `flat-world.csv`，但 `[floor]` 筛选不再信任 `b.size.x * b.size.z`，而是累计网格世界中面法向 `normal.y > 0.7f` 的三角形在 XZ 平面投影面积 `upArea`，仅当 `upArea > 1.5f` 且网格非窄边框灯饰时标为 `[floor]`（如 `mesh_307.002`、`mesh_397.004`、`mesh_185.001`、`mesh_429.002`）。
- **备选与权衡**：仅按名称硬编码黑名单 `mesh_303.002` 在未来重导出节点改名后会失效；按法向朝上真实面积过滤从几何本质解决了所有筒灯圈与单面向下吊顶误报问题。

## Risks / Trade-offs

- **重烘 `Flat.prefab` 须保持既有三扇门与子资产完整** → `LivingCeilingSeal` 挂在 `Flat` 根节点下、位于门枢轴外侧，且复用 `SavePrefabWithFlippedSubAssets()` 的重载校验并重新运行全套 `FlatDoorTests`。
- **关闭默认卧室 `room-lights` 可能让既有平层曝光略变** → 修复重叠后重新运行 `CliFlat.Shot` 目视核对 `flat-living.png`、`flat-bed.png` 与 `flat-wide.png`，必要时再在后续独立评估灯光亮度。
