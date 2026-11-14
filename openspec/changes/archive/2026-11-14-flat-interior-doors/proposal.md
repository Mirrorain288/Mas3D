# Proposal

## Why

当前整套平层场景 `apps/unity/Assets/_Local/room/Flat.prefab`（源资产 `mita-flat.glb`）按偏矮比例导出（客厅/厨房/浴室天花板 `y = 1.77 m`，三扇室内门洞净尺寸仅宽 `0.61 m` × 高 `1.30 m`），同时三扇室内门扇仍是绑定在房间原点的静态碰撞网格，与 `PlayerMotor`（`Ø 0.64 m` × 高 `1.70 m`）碰撞冲突，导致玩家被堵在卧室与门框外、无法正常开关门穿梭平层各房间。

## What Changes

- **保持源资产只读、平层按 `1.5×` 尺度重烘焙**：不修改 `mita-flat.glb`（与 `mita-room.glb`），在 `CliFlat.Bake` 中将 `Flat.prefab` 均匀放大 `1.5×`（门洞变为 `0.915 m` 宽 × `1.95 m` 高，天花板落到 `2.655 m`，适配 `1.70 m` 站立角色与第三人称防穿墙相机，并为后续二层留出合理层高）；同步按 `1.5×` 缩放平层五盏顶灯高度与射程、`RoomBootstrap.FlatSpawn`（`-4.725, 0.10, 0.30`）及 `CliFlat.Shot` 取景坐标，并让 `CliPlayer.BuildScene` 在 `Flat.prefab` 存在时自动把未激活的 `FlatRoom` 接进 `Main.unity`。
- **三扇室内门合页枢轴与双面随动碰撞**：在 `CliFlat.Bake` 中对三扇室内门（浴室↔卧室 `mesh_341`+把手 `mesh_340`、卧室↔客厅 `mesh_238`+把手 `mesh_237`、客厅↔餐厅/开放厨房 `mesh_438`+把手 `mesh_297`）按 `+Z` 合页边创建局部旋转枢轴节点并挂载 `InteractiveDoor` 组件；门框（`mesh_538`、`mesh_419`、`mesh_437`）固定不动，门扇与把手绕合页平滑旋转 `90°`，保证双面碰撞随门旋转、闭合可靠阻挡、敞开净宽（`~0.825 m` × `1.95 m`）可供 `1.70 m` 玩家自由穿过。客厅外墙通向室外黑区的防盗门 `mesh_269.001` 维持封闭不列入交互门。
- **统一门交互控制与底部 HUD 提示**：新增 `DoorInteractor` 并由 `RoomBootstrap.Boot` 在存在可交互门时自动挂载绑定。非打字聚焦（`!Panel.IsInputFocused`）且非演出忙碌（`Director.Current == null`）时，玩家靠近且面向门（射程内）会在屏幕底部中央看到 `按 F 开门` / `按 F 关门` 提示；按 `F` 键或用鼠标左键点击非对话面板区域内的目标门可切换开/关状态，摆动过渡期间不卡滞或弹飞 `CharacterController`。

## Capabilities

### New Capabilities

- `scene/unity-flat`: 定义 Unity 整套平层场景（`Flat.prefab` / `-flat 1`）的 `1.5×` 人类尺度烘焙基线、三扇室内门开/关状态切换、合页碰撞随动与交互提示契约。

### Modified Capabilities

- 无（既有 `scene/unity-bedroom` 与 `ui/dialogue-console` 需求保持不变；`F` 键与左键交互受输入框聚焦隔离门禁约束，不侵入既有输入与动作按键契约）。

## Impact

- 编辑器工具：`apps/unity/Assets/Editor/CliFlat.cs`、`apps/unity/Assets/Editor/CliPlayer.cs`
- 运行时脚本：`apps/unity/Assets/Scripts/Room/InteractiveDoor.cs`、`apps/unity/Assets/Scripts/Room/DoorInteractor.cs`、`apps/unity/Assets/Scripts/RoomBootstrap.cs`、`apps/unity/Assets/Scripts/UI/DialoguePanel.cs`
- UI 样式与布局：`apps/unity/Assets/UI/Dialogue.uxml`、`apps/unity/Assets/UI/Dialogue.uss`
- 烘焙产物：`apps/unity/Assets/_Local/room/Flat.prefab`、`apps/unity/Assets/Scenes/Main.unity`
- 自动化测试：`apps/unity/Assets/Tests/EditMode/DoorTests.cs`、`apps/unity/Assets/Tests/PlayMode/FlatDoorTests.cs`
