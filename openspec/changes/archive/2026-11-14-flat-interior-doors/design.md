# Design

## Context

`mita-flat.glb` 在 `.superpowers/export-flat2.py` 导出时将各节点的世界变换直接烘焙进了顶点并将局部原点归零，且整体尺偏矮（天花板 `y = 1.77 m`，三扇室内门 `mesh_341`、`mesh_238`、`mesh_438` 各自仅有 `0.61 m` 宽 × `1.30 m` 高）。同时，`CliFlat.Bake` 当前为全部网格挂静态碰撞，`CliPlayer.BuildScene` 重建 `Main.unity` 时会清空场景且不会自动重新挂载 `Flat.prefab`（仅靠单独运行 `CliFlat.Wire` 挂接）。详细动机与需求范围见 `proposal.md` 与 `specs/scene/unity-flat/spec.md`。

## Goals / Non-Goals

**Goals:**
- 通过 `CliFlat.Bake` 均匀 `1.5×` 缩放 `Flat.prefab`，使玩家无需缩小胶囊或穿墙即可在平层三扇门间正常穿梭，并与既有 `1.70 m` 角色身高与第 2 项二层楼板比例对齐。
- 在不改动 `mita-flat.glb` / `Main.unity` 默认卧室路径的前提下，为三扇室内门建立可复用、可无头单元测试的合页开关与提示系统。
- 让 `CliPlayer.BuildScene` 在 `Flat.prefab` 存在时自动保留/接回未激活的 `FlatRoom` 与 `FlatSpawn`，使 `tools/unity.ps1 player` 不会意外抹掉平层接线。

**Non-Goals:**
- 不重新导出或修改 `mita-flat.glb` 与 `mita-room.glb` 源文件。
- 不给室外黑区外墙防盗门 `mesh_269.001`、卫生间淋浴房门或柜门增加交互。
- 不在本变更处理客厅二层楼板楼梯/封堵（第 2 项）、窗外星空卡裁剪（第 3 项）或平层角色锚点重算（第 4 项）。

## Decisions

### D1：烘焙期 `FlatScale = 1.5f` 与 `CliPlayer` 自动接线
- **选择**：在 `CliFlat.Bake()` 中先将根节点下所有直接子物体按 `(pos + shift) * 1.5f`、`localScale * 1.5f` 放缩，使 `y = 0` 地面齐平、中心对齐原点且所有网格边界放大 `1.5×`（门洞 `0.915 m × 1.95 m`、常规层高 `2.655 m`）。同步在 `CliFlat.Light()` 按 `1.5×` 缩放灯高与射程，在 `RoomBootstrap` 与 `CliFlat.Wire()` 将 `FlatSpawn` 设为 `(-4.725f, 0.10f, 0.30f)`、`FlatEye = 2.13f`，并抽出 `CliFlat.WireIntoScene(Scene)` 供 `CliPlayer.BuildScene()` 末尾调用（若 `Flat.prefab` 存在则接入未激活的 `FlatRoom`）。
- **备选与权衡**：方案 B（维持 `1.0×` 并在 `-flat` 下缩小胶囊体）虽不改 `FlatSpawn`，但 `1.70 m` 站立角色头肩模型仍会穿透 `1.30 m` 门楣、第三人称相机易顶撞 `1.77 m` 吊顶；缩放烘焙不改任何资产且兼容第 2 项楼板高度，代价仅是同步更新平层相机截图坐标。

### D2：三扇室内门的合页枢轴重构与固定开启方向
- **选择**：在 `CliFlat.Bake()` 完成 `1.5×` 缩放后、挂碰撞体之前，根据网格名配对三扇室内门：
  1. `Door_BathBedroom`：门扇 `mesh_341` + 把手 `mesh_340`（`x ≈ -7.83`）
  2. `Door_BedroomLiving`：门扇 `mesh_238` + 把手 `mesh_237`（`x ≈ -1.64`）
  3. `Door_LivingKitchen`：门扇 `mesh_438` + 把手 `mesh_297`（`x ≈ +6.62`）
  每扇门以缩放后门扇包围盒的 `+Z` 合页边 `(leafBounds.center.x, leafBounds.min.y, leafBounds.max.z)` 建立枢轴父物体，再用 `SetParent(..., true)` 将门扇和把手挂到枢轴下并添加 `InteractiveDoor`。实测 `flat-world.csv` 显示浴室 `-X` 侧紧邻弯角淋浴隔断 `mesh_181.001`，而三扇门的 `+X` 侧通道（卧室、客厅过道、餐厅）均空旷无家具遮挡；因此三扇门统一绕枢轴 Y 轴向 `+X` 方向开启 `-90°`（在局部净宽约 `0.825 m > 0.64 m` 胶囊直径下完全让开通路），门框与外墙防盗门 `mesh_269.001` 留在原层级保持静态。
- **备选与权衡**：按玩家所在侧双向开启在浴室门会顶进淋浴房；统一朝无遮挡的 `+X` 侧开启更稳定且便于断言。

### D3：摆动期间的 `CharacterController` 防卡滞碰撞处理
- **选择**：每扇门挂上 `InteractiveDoor` 并绑定门枢轴内所有 `Collider`（包括 `CliFlat.AttachColliders` 生成的外向 `MeshCollider` 与 `inner-collider`）。闭合（`OpenFraction == 0`）与完全开启（`OpenFraction == 1`）时门体碰撞开启；当门处于开关动画过渡期（`IsMoving`），临时禁用门体碰撞并在到达目标角时检测玩家 `CharacterController.bounds` 是否与门体包围盒重叠——若重叠则待玩家离开门叶扫掠范围后再恢复实心碰撞。
- **备选与权衡**：始终保持非凸网格碰撞旋转会在 Unity `CharacterController` 中卡住或把站在 `+X` 摆动区的玩家弹飞；短暂避让 + 离开后恢复既保证闭门阻挡、开门扇叶实心，又零卡死。
- **Prefab 碰撞序列化注意**：`CliFlat.AttachColliders` 会创建运行时实例化的翻转网格 `(inner)`；保存 Prefab 前需将新建的翻转网格作为子资产存入 `Flat.prefab`（或由 `RoomColliders` 兜底），避免重载 Prefab 后 `inner-collider.sharedMesh` 丢失导致单向穿门。

### D4：`DoorInteractor` 输入门禁、点击射线与底部 HUD 提示
- **选择**：
  - 在 `RoomBootstrap.Boot()` 检测场景中已激活节点里的 `InteractiveDoor`（仅当 `FlatRoom` 启用或测试/场景存在门时挂接 `DoorInteractor`，挂载在随场景销毁的 `Input` 子物体上）。
  - `DoorInteractor` 与 `ActionHotkeys` 使用同一门禁契约：`KeyboardInput && (Player == null || Player.KeyboardInput)`、非打字（`Panel == null || !Panel.IsInputFocused`）、非演出（`Director == null || Director.Current == null`）。
  - 按 `F`（`InteractKey = KeyCode.F`）从射程内（默认 `2.6 m`）面向度与距离最优的一扇门切换开关；左键点击时先确认未点中 `DialoguePanel` 区域且 `Camera` 射线命中某扇 `InteractiveDoor` 的子节点碰撞体并在射程内再切换。暴露公开接缝 `TargetDoor`、`CurrentPromptText` 与 `PressInteract()`，测试无需真实键鼠即可验证完整行为。
  - 在 `Dialogue.uxml` / `Dialogue.uss` 添加屏幕底部中央（`picking-mode="Ignore"`，默认 `display: none`）的提示条 `#door-prompt`，由 `DialoguePanel.ShowDoorPrompt(string)` 控制显隐（沿用 `ApplyCjkFallback` 字体，不污染 `LogText` 与 `DialoguePanelTests`）。

## Risks / Trade-offs

- **旧截图坐标失效** → 同步更新 `CliFlat.Shot` 的预设观察坐标与灯光高度参数，并重新拍 `flat-player` / `flat-wide` 目视核对。
- **`Flat.prefab` 子资产翻转网格缺失风险** → 在 `CliFlat.Bake` 显式检查或将 `(inner)` `Mesh` 作为 sub-asset 写进 `Flat.prefab`（`AssetDatabase.AddObjectToAsset` 或随门运行期 `RoomColliders` 补建），并在 `FlatDoorTests` 实测从 `-X` 和 `+X` 两个方向撞向闭合门均被挡住。
- **后续第 2/3/4 项坐标基线变化** → 已在 `design.md` 明确记录 `FlatScale = 1.5f` 使二层楼板变为 `y ≈ 2.655 m`，后续变更直接基于新尺寸推进。
