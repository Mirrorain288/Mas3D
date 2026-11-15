# Proposal

## Why

当前 Unity 桌面端默认打开仍停留在狭小的单间卧室（`mita-room.glb`），`Flat.prefab`（卧室/客厅/餐厅/厨房/卫浴全平层）仅作为 `-flat 1` 调试开关存在：平层没有专属的锚点与座位配置，`AvatarActor` 未进入平层空间，角色行走图也仅覆盖旧单间卧室。为了将整套平层正式确立为日常陪伴的主舞台（整体四步规划的第一步），需要打通“默认进入平层客厅、跨三大生活区活动与推门通行、兼容既有单卧回归测试”的完整基础闭环。

## What Changes

- **默认平层开局与兼容通道（BREAKING on default startup mode）**：将 `Main.unity` / `RoomBootstrap` 的无参默认启动从旧单间卧室切换为平层主舞台（激活 `FlatRoom`，停用原默认卧室与场景根级灯光/天空卡），同时提供显式 `-bedroom 1` 与 `ShowBedroom()` 切换回原单间卧室，使既有单卧标定与回归测试继续受控运行。
- **客厅大沙发迎面开局与 1–5 互动适配**：将玩家与她的默认初始落位设于平层客厅（玩家立于茶几前面向三人位大沙发，她初始安静坐于客厅主座 `S1`）；扩展 `AnchorTable` 活跃表解析，使 `ActionDirector`、`ActionSeats` 与 `AvatarActor` 跟随活跃房间表读取客厅大沙发及伴侣位，并在她处于外围非兼容座或行走中时安全拒绝 1–5 进场。
- **配置化多房间路网与单手推门 IK**：扩展 `AnchorTable`（`waypoints`、`links`、`defaultSpawn` 等可选字段）与 `CliFlatAnchors` 离线几何求解器生成经过 `0.30 m` 胶囊零穿模扫掠验证的 `anchors-flat.json`；覆盖卧室（阅读椅/书桌/床边/窗边）、客厅（大沙发/双人沙发/窗边）与餐厨区（餐桌/吧台/料理窗旁），卫浴间仅保留玩家手动开关门交互而不安排她的自主点位；`EdgeTable` 与 `AvatarWalker` 支持跨过关闭的 `Door_BedroomLiving` / `Door_LivingKitchen` 时停步并用单臂 `TwoBoneIk` 抓向真实门把手配合 `0.35 s` 推门通过。
- **克制的静谧空闲流转**：新增 `AvatarRoutine`，在无对话、无 1–5 动作且安静空闲一段时间后，按确定性/低频节律让她在非卫浴生活区之间缓慢换区停驻，遵循“说话优先于走路”的抢占原则并提供测试禁用开关。
- **独立阻塞项追踪（超出本次变更实施范围）**：全局计划中的“语义控制表情（LumenAct：<https://github.com/AaBb-coder/LumenAct>）”因需先在 Blender 中手动添加 **118 个形态键（shape keys）**，继续作为独立 `[Blocked]` 待办项跟踪，不在本次变更内改造面部形态键；后续步骤（分室布灯与窗外夜色景观、Kimodo 骨骼动画与全身推门、咖啡/共读道具玩法）亦保持解耦延后。

## Capabilities

### New Capabilities
- （无：本变更直接在既有 Unity 平层场景能力下扩展平层主舞台、多房间通行与空闲流转契约。）

### Modified Capabilities
- `scene/unity-flat`: 将默认启动契约从“仅 `-flat 1` 激活平层”调整为“默认激活平层、`-bedroom 1` / `ShowBedroom()` 保留旧单卧兼容”；新增客厅迎面开局与大沙发 1–5 互动适配、非卫浴多房间锚点与全图零碰撞路网、跨门单臂 `TwoBoneIk` 推门通行，以及克制的空闲换区流转需求。

## Impact

- **Unity 运行时与编辑器代码**：`apps/unity/Assets/Scripts/AnchorTable.cs`、`Boot/BootArgs.cs`、`RoomBootstrap.cs`、`Room/EdgeTable.cs`、`Room/InteractiveDoor.cs`、`Avatar/AvatarWalker.cs`、`Avatar/AvatarActor.cs`、`Avatar/AvatarRoutine.cs`（新增）、`Actions/ActionDirector.cs`、`Actions/ActionSeats.cs`、`Editor/CliFlatAnchors.cs`（新增）、`Editor/CliPlayer.cs`。
- **数据与脚本**：`apps/unity/Anchors/anchors-flat.json`（新增）、`apps/unity/Assets/StreamingAssets/mas3d/anchors-flat.json`（新增）、`tools/unity.ps1`。
- **测试夹具**：新增平层 EditMode/PlayMode 套件（`FlatAnchorTableTests`、`FlatAnchorTests`、`FlatNavigationTests`、`FlatActionTests`、`RoutineTests`）；更新 `SceneFlow.cs` 与既有用例（`FlatCeilingTests`、`FlatDoorTests`、`BedroomSkyCardTests` 及明确测试旧单卧几何的场景用例），同时确保 `ActionSmokeTests` 在默认平层场景通过且 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的跳过边界不动。
