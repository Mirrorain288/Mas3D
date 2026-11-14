# Tasks

> 约束清单：
> - 不修改、不重导 `apps/unity/Assets/_Local/room/mita-flat.glb` 与 `mita-room.glb` 源文件。
> - `F` 键与左键门交互在聊天框聚焦（`Panel.IsInputFocused`）或双人动作演出中 MUST 被屏蔽，不与 `InputModes`（`E`/`Esc`）或 `ActionHotkeys`（`1–5`/`C`/`Z`/`X`）冲突。
> - 不破坏默认单间卧室 `Main.unity` 及既有 `RoomLightTests`、`DialoguePanelTests`、`ActionSmokeTests` 等全门测试。
> - 每组代码均先写失败测试（TDD RED），确认因缺失实现而失败后再编写最小实现转绿。

## 1. 室内门合页状态机与交互控制器（TDD）

- [x] 1.1 先创建 EditMode 测试 `apps/unity/Assets/Tests/EditMode/DoorTests.cs` 覆盖 `InteractiveDoor`（开/关目标角 `0° / -90°`、`Tick(dt)` 平滑插值、摆动期关碰撞、若玩家包围盒仍重叠门叶则延迟恢复碰撞、仅允许显式门体不误含外墙大门）并运行 `tools\unity.ps1 test -Mode EditMode` 确认 RED；随后实现 `apps/unity/Assets/Scripts/Room/InteractiveDoor.cs` 并重跑 `DoorTests` 确认全绿。
- [x] 1.2 在 `apps/unity/Assets/Tests/EditMode/DoorTests.cs` 追加 `DoorInteractor` 测试（`InteractKey == KeyCode.F`、与 `InputModes`/`ActionHotkeys` 按键不冲突、交互半径 `2.6 m` 与视角朝向筛选最优门、闭合显示 `按 F 开门`、开启显示 `按 F 关门`、远离清空提示、`Typing`/`Busy`/`!KeyboardInput` 门禁下 `PressInteract()` 返回 `false`）确认 RED；随后实现 `apps/unity/Assets/Scripts/Room/DoorInteractor.cs` 并重跑 EditMode 确认全绿。

## 2. 底部交互提示 UI 与启动接线

- [x] 2.1 在 `apps/unity/Assets/UI/Dialogue.uxml` 与 `apps/unity/Assets/UI/Dialogue.uss` 新增屏幕底部中央的提示条 `#door-prompt`（`picking-mode="Ignore"`，默认 `display: none`），并在 `apps/unity/Assets/Scripts/UI/DialoguePanel.cs` 新增 `ShowDoorPrompt(string)` 与 `DoorPromptText` 只读属性（沿用 `ApplyCjkFallback` 字体，不修改 `LogText`）；运行 PlayMode `DialoguePanelTests` 验证既有测试与新 `#door-prompt` 结构断言全部通过。
- [x] 2.2 在 `apps/unity/Assets/Scripts/RoomBootstrap.cs` 调整平层默认出生点 `FlatSpawn = (-4.725f, 0.10f, 0.30f)`、`FlatEye = 2.13f` 并在 `Boot()` 为激活场景中存在的 `InteractiveDoor` 自动挂载并绑定 `DoorInteractor`（包含左键点门射线与 `Panel.ContainsScreenPoint` UI 防误触）；验证 `Boot` 在无门默认卧室不新建多余提示且在有门场景正常接线。

## 3. 平层 1.5 倍烘焙、合页重构与自动场景集成

- [x] 3.1 更新 `apps/unity/Assets/Editor/CliFlat.cs`：加入 `FlatScale = 1.5f` 均匀缩放（围绕地面 `y = 0` 与中心原点）、在挂载碰撞前将 `mesh_341+mesh_340`、`mesh_238+mesh_237`、`mesh_438+mesh_297` 按各自缩放后 `+Z` 合页边重组为 `Door_BathBedroom` / `Door_BedroomLiving` / `Door_LivingKitchen` 并挂 `InteractiveDoor`、确保 `(inner)` 翻转网格作为子资产持久化进 `Flat.prefab`、同步按 `1.5×` 调整 `Light()` 与 `Shot()` 机位及 `WireIntoScene()`；更新 `apps/unity/Assets/Editor/CliPlayer.cs` 在 `BuildScene()` 末尾存在 `Flat.prefab` 时自动调用 `CliFlat.WireIntoScene(scene)`。
- [x] 3.2 依次运行 `tools\_bake.ps1 -Method Mas3D.Editor.CliFlat.Bake -Log flat-bake`、`tools\unity.ps1 player`、`tools\_shot.ps1 -Method Mas3D.Editor.CliFlat.Shot -Log flat-shot`；验证 `Flat.prefab` 含 3 个 `InteractiveDoor`（外墙大门 `mesh_269.001` 未挂交互）、所有门体双面碰撞体均非空、门洞净尺寸约为 `0.915 m × 1.95 m`，并查看 `flat-player.png` / `flat-wide.png` 确认光照与尺度正常。

## 4. 平层真机门穿行 PlayMode 验收与全量回归

- [x] 4.1 编写 PlayMode 测试 `apps/unity/Assets/Tests/PlayMode/FlatDoorTests.cs`：先断言现状失败再针对加载的 `Flat.prefab` 真实几何核验（1）三扇门闭合时站立 `PlayerMotor` 从 `-X` 与 `+X` 两侧撞击均被可靠阻挡；（2）走近按 `F` 与模拟非 UI 区域点击分别触发开关并切换 `#door-prompt` 提示文案；（3）依次开启浴室↔卧室、卧室↔客厅、客厅↔开放厨房三扇门后，站立 `1.70 m`（`R = 0.30 m`）玩家可连续走通全部三个房间；（4）输入框聚焦或演出期间按 `F` 与点击面板内左键不触发开关门。运行 `FlatDoorTests` 确认全绿。
- [x] 4.2 按最终顺序运行 `openspec validate flat-interior-doors --strict`、`git status --short apps/unity/Assets/_Local/room/mita-flat.glb apps/unity/Assets/_Local/room/mita-room.glb` 与全门验收 `powershell -NoProfile -ExecutionPolicy Bypass -File tools\check.ps1`；核查 `web`、`unity-edit`、`unity-play`、`smoke`、`player` 全绿（仅保留既有的 `bridge` 与 `RoomLightTests` 预期跳过），将真实输出与截图路径提交给用户验收。

## Workflow follow-up

- 待用户确认第 1 项验收结果后，再按指令决定是否提交或通过 `/openspec-archive-change` 归档，随后继续第 2 项（客厅上方二层楼板补楼梯或封掉）。
