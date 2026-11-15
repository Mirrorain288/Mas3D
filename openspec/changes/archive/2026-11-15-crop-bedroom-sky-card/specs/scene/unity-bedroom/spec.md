# Spec Delta

## ADDED Requirements

### Requirement: 默认卧室窗外星空卡贴窗裁剪并仅朝室内单面可见

默认卧室（`mita-room.glb`）在 `Main.unity` 烘焙（`CliPlayer.BuildScene`）与截图（`CliCapture`）中生成的根级 `night-sky-card` MUST 紧贴 `-Z` 窗洞（`mesh_219`）外背、按窗洞加适度斜视余量裁小并严格落在 `-Z` 外墙与檐口边界以内，材质 MUST 启用单面背面剔除且仅朝室内（`+Z`）渲染；同时保留根级 `"night-sky-card"` 身份兼容 `RoomBootstrap` 模式隔离，不改变 `HouseLook` 窗卡或 `Flat.prefab`。

#### Scenario: 默认场景烘焙对齐真窗洞且贴窗单面裁剪
- **WHEN** 通过 `CliPlayer.BuildScene` 重烘 `Main.unity` 并以默认模式加载
- **THEN** 场景根级存在 `"night-sky-card"`，其位于 `-Z` 窗洞（`mesh_219`）正后方紧贴外窗面、宽高覆盖窗洞且不越出 `-Z` 外墙与檐口轮廓，正面朝向室内 `+Z` 且材质启用背面剔除（`CullMode.Back`）

#### Scenario: 室内望窗正常且外部环绕取景无星空片遮挡
- **WHEN** 从默认卧室室内望向 `-Z` 窗洞，以及通过 `CliCapture`（含 `-lightsOnly` 与默认穿衣路径的 `-orbit` 外部环视视角）从室外回望
- **THEN** 室内透窗正常显示星空无漏底，而 `-Z` 室外与外部环绕视角下 `night-sky-card` 被背面剔除且不溢出墙外遮挡房间
