# 观感收尾：眼睛与房间自身投影 — 实施计划

- 日期：2026-11-06
- 前置：`docs/superpowers/specs/2026-11-06-art-and-controls-alignment-design.md` §10「未完成」的两条；HEAD `b4866e6`
- 背景：用户 m18380 要求继续做 §10 的 ① 眼睛、② 房间自身投影

## Global Constraints

1. 不 commit、不 push：所有改动只 `git add`（AGENTS.md + YG 历次要求）。提交由控制方问过 YG 再做。
2. 只改本任务范围内文件；**不要动** `apps/unity/Assets/Scripts/InputModes.cs`、`PlayerMotor.cs`、`PlayerCameraRig.cs`、`PlayerBody.cs`、`Assets/Scripts/UI/DialoguePanel.cs`（controls 已验收）。
3. 判绿只认 `apps/unity/TestResults/*.xml` 的计数，不认 CLI 退出码（`unity test` 撞项目锁时 exit 6）。
4. 同一工程不能并发开编辑器：调 Unity 前先 `Get-Process Unity`，跑完立刻退出，开始/结束各报一句。
5. 测试不许按帧数等（批处理帧率不稳），要按 `Time.deltaTime` 累积的模拟时间。
6. 渲染数值只走 `apps/unity/Assets/Scripts/RoomLook.cs` 的 Volume 与 `RoomDress.Tuning`；不要在 shader 里再写 tonemap。
7. 出图统一用 `tools/unity.ps1 player` 重建场景 + `tools/unity.ps1 capture`；量图用 `tools/look-compare.mjs`（不要新写一套分析脚本，需要新判据就扩展它）。
8. 每条「修好了」都要有**变异红证**：把关键改动回退一行 → 对应用例必须红 → 还原 → 绿，XML 存 `apps/unity/Logs/`。

## Task 1 眼睛：从荧光黄绿改成参考那种柔绿虹膜

**为什么**：参考立绘的虹膜是低饱和青绿渐变 + 左上白色高光 + 细而不黑的外圈；当前是荧光黄绿、几乎无渐变、外圈过重，和整体干净的脸比就这一处最违和（设计文档 §10 第 1 条）。

**要做的**：
1. 先定位眼睛画在哪：`Assets/_Local/avatars/Monkia.vrm` 的 17 个材质里，找出虹膜/眼白/睫毛各自用哪张贴图、哪个材质属性（`_MainTex` 还是 MToon 的 `_MainTex`/`_ShadeColor`）。把结论写进报告（材质名 + 贴图名 + 尺寸），别猜。
2. 在**贴图级**改造虹膜：压饱和度、把虹膜调成青绿渐变（顶部深、靠瞳孔浅），保留瞳孔，并在左上角加一处柔白高光（不是纯白硬点，要有 1-2 px 过渡）。眼白保持接近纯白但略压（避免和脸一样亮）。**不要改 VRM 源文件**：改出来的贴图放 `apps/unity/Assets/_Local/avatars/eyes/`，运行时由脚本把材质贴图换成它（与 `AvatarShading` 一样写场景副本）。
3. 外圈：现在是硬黑描边，改成更细、偏深棕（不是纯黑），或者干脆靠 MToon 描边承担。判据见下。
4. 出图：`tools/unity.ps1 player - ...` 重建后 `tools/unity.ps1 capture` 出 `face.png`，与 `docs/superpowers/specs/assets/2026-11-06-reference.png` 并排成一张 `.superpowers/look/<日期>/eyes-sheet.png`（参考裁眼部 vs 当前眼部特写，机位沿用 `CliPlayer.Capture` 现有的 face 机位，必要时加一个更近的眼部机位）。

**判据（数值，写进测试或用例）**：
- 虹膜平均饱和度（HSV S）从当前实测值降到 **≤0.62**，且色相落在 **140°–185°**（青绿）。用 `tools/look-compare.mjs` 扩一个 `eyes` 段输出这两项，不通过 exit 1。
- 虹膜内存在至少一处亮度 **≥0.90** 的高光簇（≥8 像素），且该簇面积 **≤虹膜面积的 8%**。
- 外圈暗带厚度（从虹膜外缘向外）**≤2 px @720p**。

**测试**：`apps/unity/Assets/Tests/EditMode/EyeLookTests.cs` 至少 3 条：① 运行时材质确实换成了 `_Local/avatars/eyes/` 里的图；② 虹膜材质的 `_ShadeColor`/`_Color` 没有把高光压掉（断关键属性的值或 `IsKeywordEnabled`）；③ 换图脚本幂等（跑两次材质实例数不变）。

## Task 2 房间自身投影：让家具真的在地上落影

**为什么**：`look-shoot` 的阴影判据（下半幅 5.54%）现在几乎全靠玩家身体与台灯近场满足——`sheet-3-feet.png` 的差值图只有玩家身体亮起来，床脚、书桌、衣柜在地板上没有影子。这是「阴影真的落地」没做完的部分。

**要做的**：
1. 诊断为什么没有影子，逐项确认并记录：① 月光 `Directional` 的 `shadows`、`shadowStrength`、`shadowBias/normalBias`、加色相机的 shadow distance（URP 资产里 30）；② 家具 renderer 的 `shadowCastingMode` 是否为 On（`RoomMaterials.Rebuild` 与 `RoomDress` 里可能有改成 Off 的地方）；③ `RoomSurface.shader` 的阴影采样是否真被编译进管线（关键字、`_MAIN_LIGHT_SHADOWS` 变体）；④ 月光方向是否太低/太斜，导致影子落在镜头外。
2. 修到能看见家具自身投影：优先调光的方向与强度、必要时提高 `shadowStrength`、把该投影的 renderer 打开。**不要**用假平面贴图伪造影子。
3. 新增**只测房间**的对照：`CliPlayer` 加一个开关（如 `-nobody` 已有则复用）出 `room-shadow-01.png` 与 `room-noshadow-01.png` 一对，跑 `tools/shadow-diff.mjs`，让判据不再被玩家身体撑起来。

**判据**：
- 玩家隐藏时，下半幅「被压暗 ≥3%」的像素比例 **≥2.0%**，最深压暗 **≥25%**（现在的数值要先量出来记进报告，再定是否放宽）。
- `look-shoot.ps1` 一条命令仍然 exit 0；原有的脸/描边/脚下/对比度四项不许回退。

**测试**：`apps/unity/Assets/Tests/EditMode/RoomLightTests.cs` 增 2 条：① 主平行光 `shadows != None` 且 `shadowStrength ≥ 0.7`；② 房间主要家具 renderer（床 `Mesh_0121out`、衣柜 `Mesh_0025out`、书桌组）的 `shadowCastingMode` 为 On。

## 收口

- 两任务各自绿跑：`unity.ps1 test -Mode EditMode` 与 `-Mode PlayMode`，计数写进报告。
- 最后跑一次 `tools/look-shoot.ps1`，把六项数值与两张新 sheet 路径写进报告，由控制方复核后再问 YG 提交。
