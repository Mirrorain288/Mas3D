# Design

## Context

详见 `proposal.md - Why`。实测当前资产与构建链路：
- 默认单间卧室 `mita-room.glb` 的开放窗洞为 `mesh_219`（`Anchors/anchors.json` 世界包围盒 `x ∈ [-0.571, 0.572]`、`y ∈ [0.517, 1.538]`、`z ∈ [-2.254, -1.872]`，位于 `-Z` 墙床头后方），墙体节点 `mesh_204` 的 `-Z` 内壁约在 `z ≈ -1.90 m`，窗框突出至 `z = -2.254 m`。
- 现行 `RoomDress.BuildSkyCard` 与 `BuildSkyCardZ` 均将 `night-sky-card` 设为 `_Cull = CullMode.Off (0f)`，按房间整墙尺度（`5.18 × 2.95 m`）放置在墙外 `0.55 m`；且 `CliPlayer.BuildScene()` 与 `CliCapture` 非 `-lightsOnly` 分支仍在调用面向 `+X` 的 `BuildSkyCard(bounds, bounds.max.x)`。
- 已有 `RoomBootstrap` 与 `FlatCeilingTests` 依赖场景根级 `"night-sky-card"` 名称做模式互斥显隐，因此必须保持 `"night-sky-card"` 仍作为根级 GameObject 存在。

## Goals / Non-Goals

**Goals:**
- 让默认卧室 `night-sky-card` 对齐到真实 `-Z` 窗洞（`mesh_219`）正后方并紧贴外窗面，尺寸裁小到仅略大于窗洞、严格小于 `-Z` 外墙轮廓。
- 将 `night-sky-card` 材质切换为单面背面剔除（`CullMode.Back`），仅令正面朝向室内（`+Z`），彻底消除室外 `-Z` 回望与外部环视视角下的面片遮挡。
- 统一 `CliPlayer.BuildScene()` 与 `CliCapture.Capture()`（`-lightsOnly` 与默认穿衣路径）走同一套裁剪建卡入口，并重烘 `Main.unity`。

**Non-Goals:**
- 不修改 `mita-room.glb` / `mita-flat.glb` 源资产、`RoomBootstrap` 模式切换逻辑或 `FlatCeilingTests` 既有用例语义。
- 不修改 `HouseLook.BuildSkies()` 的 4 张备用多房间窗卡或平层 `Flat.prefab`。
- 不触碰 `RoomLightTests.TheFurnitureCastsShadowsAndTheShellDoesNot` 的 `Assert.Ignore`。

## Decisions

### 1. 窗洞度量定位与紧贴裁切尺寸
- **选择**：在 `RoomDress` 中提供面向具体房间的统一建卡入口（例如 `BuildWindowSkyCard(GameObject room, Bounds roomBounds)`）。当房间中存在 `mesh_219`（其带有效包围盒的 `Renderer`）时，以 `mesh_219` 世界包围盒 `w` 为锚，将 `night-sky-card` 放置于 `z = w.min.z - 0.08f`（紧贴窗框外背 `~8 cm`，取代原先悬空 `0.55 m`），中心对齐 `(w.center.x, w.center.y)`，宽高设为窗框尺寸加室内斜视视差余量（例如水平各留 `+0.35 m`、上下各留 `+0.30 m`，约 `1.84 × 1.62 m`），同时钳位到 `roomBounds` 的横向壁面宽度与底部/檐口高度内（如 `x` 不出墙角、`minY >= 0.05`、`maxY <= roomBounds.max.y - 0.35`）。若无 `mesh_219`（例如精简测试夹具），退回以 `roomBounds` 的 `-Z` 中部按窗口比例裁切生成，而非整墙尺度。
- **备选与权衡**：未采用「全动态脚本逐帧按相机是否在房间外开关星空卡」（Option B），因为仅在单面背面剔除 + 贴窗内缩的几何下即可零运行时开销解决 `-Z` 室外遮挡与外缘溢出；`0.30–0.35 m` 边距经窗框厚度（`-1.87..-2.25 m`）与卡面间隙（`~0.08 m`）几何核算足以覆盖室内斜角度看窗不漏底，又严格落在 `4.37 m` 宽 `-Z` 外墙以内。

### 2. 单面背面剔除与 Quad 朝向
- **选择**：材质统一设置 `_Cull = (float)UnityEngine.Rendering.CullMode.Back`（`2f`），不再使用 `CullMode.Off`（`0f`）。Unity 内置 `PrimitiveType.Quad` 局部正面法线朝 `-Z`，施加 `Quaternion.Euler(0f, 180f, 0f)` 后局部 `-Z` 转向世界 **`+Z`（朝向卧室室内）**，从而：
  - 室内摄像机（位于 `z > card.z` 望向 `-Z`）面向正面，完整渲染星空；
  - 室外 `-Z` 侧摄像机（位于 `z < card.z` 回望 `+Z`）面向背面，由 GPU 背面剔除直接裁掉。
- **注意**：需核查并保留 URP `Universal Render Pipeline/Unlit`（及兜底材质）下 `_Cull = 2f` 实际生效，并在测试与双面对比截图中实测验证没有正反面颠倒。

### 3. 缩尺后贴图平铺校验
- **选择**：沿用 `Assets/_Local/sky/night-sky-2k.jpg` 与原有提亮系数（`1.35, 1.35, 1.5`）；由于卡片宽度从 `~5 m` 缩至 `~1.8 m`，实施后通过对比室内透窗截图检查是否因整张 2K 全景天球直接压进小窗而导致星点过密或银河畸变，如有需要可通过 `_BaseMap` / `_MainTex` 的材质 `mainTextureScale` / `mainTextureOffset` 取天球中上星区子区域以保持自然窗景。

### 4. 构建与截图双入口统一及 `Main.unity` 重烘
- **选择**：让 `CliPlayer.BuildScene()` 与 `CliCapture.Capture()`（含 `-lightsOnly` 与默认穿衣分支）均改为调用同一套 `-Z` 贴窗单面建卡方法，移除向 `+X` 墙挂巨幅双面卡的调用分叉；随后运行 `CliPlayer.BuildScene()` 重烘 `Main.unity`（确保 `night-sky-card` 仍居场景根级且名称严格为 `"night-sky-card"`）。
- **验证兼容**：检查 `CliPlayer.BuildScene()` 当前是否完整保留近期归档过的接线（如 `FlatRoom`、`InteractiveDoor` 烘培及 `Anchors` 同步），保证重烘 `Main.unity` 不会丢失既有功能。

## Risks / Trade-offs

- `[Risk] 室内斜角度看窗角落漏出背景底色` → 通过 `mesh_219` 宽高各扩 `0.30–0.35 m` 边距并紧密贴合在 `w.min.z - 0.08f`，再在 `CliPlayer.Capture` 与室内斜视机位实拍验图；若仍漏底仅微调边距并在墙廓范围内钳位即可。
- `[Risk] Quad 绕向与材质 _Cull 实际效果相反导致室内变黑、室外反而可见` → 在新增测试中显式校验 `renderer.sharedMaterial.GetFloat("_Cull") == (float)CullMode.Back` 与卡面世界前向指向（`card.transform.forward` 指向 `-Z` 时即局部 `+Z` 为背、`PrimitiveType.Quad` 正面法线沿 `-forward = +Z` 朝向室内），并结合室内 + `CliCapture -orbit 4` 外部截图肉眼核验。
- `[Risk] 重烘 Main.unity 覆盖场景手工调整` → 执行 `CliPlayer.BuildScene()` 前确认 `CliPlayer.cs` 已包含全部场景构建与 `FlatRoom` 接线（在任务中显式核对后再重烘），并在重烘后运行 EditMode/PlayMode 全套测试验证。
