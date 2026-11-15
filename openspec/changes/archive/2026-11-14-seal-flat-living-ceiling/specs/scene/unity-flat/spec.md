# Spec Delta

## ADDED Requirements

### Requirement: 平层或备选房间启用时停用默认卧室及其场景根级附属物

当以 `-flat 1`（或 `ShowFlat()`、`-house 1`）启动场景时，系统 MUST 停用原默认单间卧室节点及其所属场景根级默认灯光与背景天空卡（`room-lights`、`night-sky-card`），确保平层客厅空间与光照不被默认卧室叠加或穿透；当回到默认模式启动时，上述默认卧室及附属对象 MUST 保持激活。

#### Scenario: 启用平层时默认卧室与附属物自动停用
- **WHEN** 在 `Main.unity` 中以 `-flat 1` 或 `ShowFlat()` 启动 `RoomBootstrap.Boot()`
- **THEN** `FlatRoom` 被激活并成为当前 `Room`，原默认卧室节点及同场景根级 `room-lights`、`night-sky-card` 均处于未激活状态；且在不带 `-flat` 的默认启动中默认卧室保持激活而 `FlatRoom` 未激活

### Requirement: 客厅上方按单层吊顶封实且楼层清单不误报二层

系统 SHALL 保留客厅既有内向跌级吊顶视觉（维持高空俯视截图单面透视能力），并在平层客厅与门厅上空（`y ≈ 2.655..2.75 m`）确保完整物理封顶与内部仰视零漏洞，使角色胶囊与相机不会越界穿透；平层地面统计工具 MUST 仅识别真实朝上的大面积可行走水平面，不得将筒灯框 `mesh_303.002` 或下向天花板 `mesh_193`、`mesh_188.001` 误报为二层楼板。

#### Scenario: 客厅上空物理封顶且地面扫描准确
- **WHEN** 在烘焙后的 `Flat.prefab` 客厅与门厅网格采样上行射线/胶囊检测，并运行平层地面扫描 `CliFlat.Map`
- **THEN** 客厅区域全部竖直上行检测均被有效吊顶或封顶碰撞阻挡在单层高度以内（无穿出虚空通道），且 `CliFlat.Map` 的 `[floor]` 列表不包含 `mesh_303.002`、`mesh_193` 与 `mesh_188.001`
