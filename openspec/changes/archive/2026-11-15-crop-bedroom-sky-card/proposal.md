# Proposal

## Why

默认单间卧室的窗外星空卡 `night-sky-card` 目前仍按双面（`CullMode.Off`）与整墙尺寸（`Main.unity` 中 `5.18 × 2.95 m`）生成在墙外 `0.55 m` 处，且 `CliPlayer.BuildScene()` 与 `CliCapture` 非 `-lightsOnly` 分支仍把它摆在错误的 `+X` 侧而非 `mita-room.glb` 实际的 `-Z` 窗洞（`mesh_219`）后方。这导致在室外视角或外部环绕截图（如 `CliCapture -orbit 4` 的 `-Z` 机位）中出现一块遮挡房间或斜伸出墙体的巨型双面星空面片；而备用 `House` 与平层 `Flat` 不属于本次范围（平层下默认 `night-sky-card` 已由 `RoomBootstrap` 停用）。

## What Changes

- 将默认卧室的 `night-sky-card` 对齐到实际 `-Z` 窗洞（`mesh_219`）正后方，尺寸由整墙级裁小为紧贴窗洞且包含室内斜视视差余量、但不超出 `-Z` 外墙与檐口边界的窗口卡片。
- 将 `night-sky-card` 材质的剔除模式从双面（`CullMode.Off`）改为单面背面剔除（`CullMode.Back`），仅令正面朝向室内（`+Z`），使室外 `-Z` 侧回望时自动裁除、不遮挡外部取景，同时保持室内透过窗洞看星空的视角与比例自然。
- 统一 `RoomDress`、`CliPlayer.BuildScene()` 与 `CliCapture.Capture()`（含 `-lightsOnly` 与 `-dress` 路径）对默认卧室星空卡的创建入口与语义，并重烘 `apps/unity/Assets/Scenes/Main.unity`；保留根级 GameObject 名称 `"night-sky-card"` 不变，确保 `RoomBootstrap` 与 `FlatCeilingTests` 的模式隔离兼容。
- 备用 `HouseLook` 窗卡与平层 `Flat.prefab` 保持现状不变。

## Capabilities

### New Capabilities

### Modified Capabilities
- `scene/unity-bedroom`: 新增对默认卧室窗外星空卡 `night-sky-card` 的位置对齐、贴窗裁剪尺寸、单面背面剔除（外部取景不遮挡）及 `CliPlayer` / `CliCapture` 一致构建约束。

## Impact

- Affected code:
  - `apps/unity/Assets/Editor/RoomDress.cs`
  - `apps/unity/Assets/Editor/CliPlayer.cs`
  - `apps/unity/Assets/Editor/CliCapture.cs`
  - `apps/unity/Assets/Scenes/Main.unity`（由 `CliPlayer.BuildScene` 重烘生成）
  - 新增/更新 Editor 或 PlayMode 自动化校验测试（`apps/unity/Assets/Tests/`）
- Affected specs:
  - `openspec/specs/scene/unity-bedroom/spec.md`（通过本变更 `specs/scene/unity-bedroom/spec.md` delta 定义）
- Out of scope:
  - `HouseLook.BuildSkies()` 4 张备用多房间窗卡、平层 `Flat.prefab`、第 4 项平层锚点重算及第 6 项 `RoomLightTests` 投影分档（`Assert.Ignore` 保持不变）
