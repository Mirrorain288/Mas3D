# 观感与操作对齐（MiSide 基准）— 设计

- 日期：2026-11-06
- 触发：YG 实机试玩后的 6 条反馈（m16057），验收口径由 YG 定为「拿参考图并排对比，看不出差距才算过」（m16075）
- 参考图：HAST-SEC webui 的 Monika 立绘（白底、正面、全身、VTuber 风格、有描边、皮肤暖）
- 现状：`apps/unity` 主线，HEAD `4b647ce`，六门全绿

## 1. 意图

六条反馈不是六个 bug，是三摊事：

1. **操作**（反馈 2/3/5/6）：输入框常开吃 WASD、移动时镜头左右晃、伪第一人称、打字光标退一格。
2. **穿帮**（反馈 4）：她脚下有来源不明的光，转头/走近会变。
3. **观感**（反馈 1）：颜色失真、面部发灰；目标是《米塔 MiSide》那种干净、高饱和、暖粉、卡通但有体积感的画面。

第 3 条与「往 MiSide 靠」是同一件事，且必须在管线层解决——现状是房间靠 shader 内手写 ACES 自救、角色完全没过 tonemap，在这种结构上调参永远调不出参考图。

## 2. 目标 / 非目标 / 反目标

**目标**

- G1 管线归位：Linear 色彩空间、真 Volume + ACES 后处理、MToon 描边、主光阴影、MToon 两段色阶可用。
- G2 操作门：输入框获得焦点即夺走移动键；Esc／点画面失焦；IME 组合中 Enter 不发送；写值后光标置末尾。
- G3 相机结构：Pivot 不再受根节点旋转污染；输入用意图 yaw 而非渲染相机 yaw；跨 1.2 m 真切换第一/第三人称。
- G4 灯：删掉 `floor-bounce`，两盏 ceiling-glow 抬回板下并拆四盏，床头灯开软阴影。
- G5 比较工具：固定机位出图 + 与参考图并排 + 数值采样，产出可复跑的对照图。

**非目标**：换模型、重做房间资产、重做 UI 视觉、走路动画、四人动作系统改动。

**反目标**

- 用「加一层暖色滤镜」冒充对齐：脸依旧无描边、无明暗层次——技术上更亮，并排图上一眼看出不是同一套画风。
- 只让截图好看：在对照图里关灯、换背景、换机位，而实机不成立。
- 为了过判据调判据（把阈值放宽到已经通过）。

## 3. 已实测的约束（不要再猜）

| 事实 | 位置 |
|---|---|
| 色彩空间是 **Gamma**（`m_ActiveColorSpace: 0`） | `apps/unity/ProjectSettings/ProjectSettings.asset:49` |
| 相机后处理关（`m_RenderPostProcessing: 0`），场景里没有 Volume | `apps/unity/Assets/Scenes/Main.unity:4860` |
| 房间自带一套调色：`color *= _RoomExposure` → ACES → 饱和度 lerp | `apps/unity/Assets/Shaders/RoomSurface.shader:103-107` |
| `PostExposure = -1.2`、`Saturation = 60`、`LightScale = 0.5` | `apps/unity/Assets/Scripts/RoomLook.cs:14-25` |
| `_RoomSaturation = Clamp01(1 + s/100)`，60 → 夹死成 1.0（只能降不能升） | `apps/unity/Assets/Scripts/RoomLook.cs:39` |
| MToon 不吃房间那套曝光；同光照下亮约 2.3×（代码注释自己写的） | `apps/unity/Assets/Scripts/RoomLook.cs:19` |
| URP：附加光阴影关、软阴影关、每物体附加光上限 4 | `apps/unity/Assets/Settings/Mas3D-URP.asset:48-49`、`:66` |
| Renderer 没有任何 RendererFeature（描边 feature 未挂） | `apps/unity/Assets/Settings/Mas3D-URP-Renderer.asset:27` |
| MToon 材质 `_ShadingToonyFactor = 1` + `_ShadingShiftFactor = 1` → 线性阶跃除零 → 恒走 litColor | VRM 材质（专家读源码确认，实现时以实际材质为准） |
| 两盏 ceiling-glow 落在 `eaveY - 1.75`（≈1.25 m），range 13，无阴影 | `apps/unity/Assets/Editor/RoomDress.cs:550-556` |
| 还有一盏 `floor-bounce` 在 `y = 0.32`，range 8.5，无阴影 | `apps/unity/Assets/Editor/RoomDress.cs:563-571` |
| Pivot 挂在 head 下，却写世界旋转 `Pivot.rotation` | `apps/unity/Assets/Scripts/PlayerCameraRig.cs:49-50`、`:125` |
| 根节点以 `TurnRate = 720`°/s 转；Cinemachine 在 LateUpdate 采样 | `apps/unity/Assets/Scripts/PlayerMotor.cs:150` |
| 全仓没有任何 `Focus()`/`Blur()` 调用；`IsInputFocused` 已存在且语音层在用 | `apps/unity/Assets/Scripts/UI/DialoguePanel.cs:92-104`、`Voice\VoiceInput.cs:109` |

## 4. 方案比较（结论：A1 + B1 + C1 + D）

**美术**

- **A1 管线归位**（选中）：Linear、相机开 post + 一个全局 Look Volume、挂 `MToonOutlineRenderFeature`、修 MToon shade 参数、补主光阴影、删 shader 内手写 ACES。现有资产按作者意图渲染，之后调 MiSide 手感只是拧 Volume 数字。代价：切 Linear 会让所有已调数值失效，需要一轮全量重调（灯、ambient、材质 amb 系数）。风险：中，但可回滚（一次提交）。
- A2 调色直给：不动管线，改灯色温 + 暖补光 + 叠 post。一周内见效，但两套调色长期打架，且描边仍为 0 px——并排图过不了。
- A3 换自写 NPR：自由度最大，要重接 VRM 表情/口型，不是这一轮的量级。

**输入**

- **B1 模式门**（选中）：`Motor.KeyboardInput = !Panel.IsInputFocused`；E 聚焦／Esc 失焦／点画面失焦；IME 组合中 Enter 不发送；写值统一置光标末尾。判据现成。
- B2 只在面板拦字符：止住灌字但角色仍被 WASD 推着走。

**相机**

- **C1 拆 Pivot**（选中）：不旋转的 rig 根在 LateUpdate 跟随 head，Pivot 只吃 Yaw/Pitch，`Apply()` 移入 LateUpdate 并设执行顺序，`ReadWish` 用意图 yaw，`TurnRate` 720→270，跨 1.2 m 真切第一人称。
- C2 最小止血：三行改动，残留阻尼滞后，第一人称仍是假的。

**灯（无分叉）**：D 必须与 A1 同轮，因为曝光口径变了。

## 5. 已定的实现决定（我拍板，可推翻）

### 5.1 色彩空间

切 Linear。**顺序**：先切、再重调，不要两件事混着调。切换后必须重跑 RoomDress 生成器并重新配平，不保留 Gamma 下的数值。

### 5.2 后处理

- 相机开 `renderPostProcessing`（运行时和 `CliCapture` 都要开）。
- 新建一个全局 Volume profile：Tonemapping=ACES，`postExposure = -0.2`、`contrast = 18`、`saturation = 15`、`colorFilter = (1.0, 0.96, 0.93)`；Bloom `0.12 @ threshold 1.1`；Vignette `0.15`。
- **删掉** `RoomSurface.shader` 里的 ACES 与 `_RoomExposure`，只留 `_AmbientStrength`；`RoomLook.Exposure()` 退化为 1（或整个删掉，由编译器报错找全调用点）。
- `RoomLook.Apply` 的饱和度上限改为 `[0.5, 1.8]`（当前 `Clamp01` 是 bug），并只作为兼容层保留，最终饱和由 Volume 管。

### 5.3 描边与 MToon

- `Mas3D-URP-Renderer.asset` 加 `MToonOutlineRenderFeature`；`_OutlineWidth` 提到 `0.0022`（世界单位），眼/眉/嘴保持 0。
- `_ShadingShiftFactor` 1 → −0.15、`_ShadingToonyFactor` 1 → 0.9（以实际材质为准微调）。
- 关掉 `_MTOON_PARAMETERMAP`（shift 贴图为空时开着会把 shift 再加 1）。
- rim：`pow(saturate(1 - NdotV + lift), 100)` 在 `NdotV ≤ lift` 处直接为 1，是二值白边；改为 `smoothstep` 保底或把 rim 强度降到 0.02 以下。

### 5.4 阴影

- `RoomSurface.shader` 编译 `_MAIN_LIGHT_SHADOWS` 并采样 `TransformWorldToShadowCoord`（现在 `GetMainLight()` 恒无衰减，注释里的「URP shadow path corrupts texture sampling」是误诊）。
- URP 打开附加光阴影（至少给 `bedside-lamp`）与软阴影。

### 5.5 灯

- **删** `floor-bounce`（y=0.32、range 8.5、无阴影，就是「脚下怪光」的主因）。
- 两盏 glow 抬到 `eaveY - 0.35`，拆成 4 盏、强度减半、range 降到 8，仍不投阴影（靠抬高与拆分压掉天花板热斑）。
- `bedside-lamp` 开软阴影（她坐在床沿时脚下有接触阴影，不再「浮」）。
- moon 色温 `0xbcd6ff` → `0xffe2d2`；fill `0x8fa6d8` → `0xb7a4c8` 且强度 0.45 → 0.18。
- 8 个角度的脚下地面亮度极差必须 < 5%（现在 > 20%）。

### 5.6 输入与光标

- `PlayerMotor.KeyboardInput` 由面板焦点驱动：`!Panel.IsInputFocused`。
- E 聚焦输入框（面板收起时先展开），Esc 失焦，左键点画面失焦，面板收起即失焦。
- IME：订阅 `IMECompositionEvent`，组合中忽略 Return（发送仍可点按钮或退出组合后回车）。
- 发送期间**不再** `SetEnabled(false)`，只用已有 `_interactable` 挡重复提交——保焦点、保光标。
- 抽出唯一写值入口：写文本后 `cursorIndex = selectIndex = value.Length`，语音回填也走它。

### 5.7 相机

- 新增不旋转的 `CameraRigRoot`（跟 head，只跟位置），`Pivot` 移出 head 层级，只由 `Yaw/Pitch` 驱动。
- `Apply()` 移入 `LateUpdate`，`[DefaultExecutionOrder(100)]` 保证晚于 `PlayerMotor`；`MouseInput = false` 只锁输入，不再跳过姿态。
- `PlayerMotor.ReadWish` 用 `Rig.IntentYaw`，不用渲染相机 `eulerAngles.y`。
- `TurnRate` 720 → 270。
- 距离 ≤ 1.2 m 切真第一人称（相机贴 head、`ShoulderOffset`/`VerticalArmLength` 归零、pitch 写相机本地 X），> 1.2 m 回第三人称，0.15 s 过渡；头部隐藏逻辑与切换点对齐到同一阈值。

### 5.8 对照工具

- `tools/look-shoot.ps1`：四个固定机位出图——白底全身（正面，2.5 m 平视）、白底面部近景（1.0 m）、房内全身（A1 锚点）、房内侧面（A3 锚点）。白底档只换背景色与清屏方式，**不另加灯**，避免出现「两套光照」。
- `tools/look-compare.mjs`：把参考图与当期出图按同高并排，另输出数值（脸颊 B/R、脸峰值 sRGB、描边像素宽、shade 区色相、房间最暗/最亮差、脚下亮度极差）。
- 产物落 `.superpowers/look/<日期>/`，不入库；入库的只有工具与判据。

## 6. 验收判据（可测，不靠肉眼）

| 项 | 判据 | 现在 |
|---|---|---|
| 脸颊色温 | 同机位脸颊采样 `B/R ≤ 0.90` | 0.98 |
| 脸部峰值 | 脸颊 sRGB 峰值 ≤ 0.92（不再顶白） | ≈0.97 |
| 描边 | 1080p / 2.5 m 机位，脸轮廓深线 ≥ 1.5 px | 0 px |
| 明暗层次 | 鼻侧与下颌出现两段色阶，shade 区 `B/R > 1`（偏暖） | 无（恒 litColor） |
| 房间层次 | 床底（最暗）与床单（最亮）sRGB 差 ≥ 0.35 | ≈0.15 |
| 脚下 | 绕她 8 个角度，脚下地面亮度极差 < 5% | > 20% |
| 输入门 | 聚焦输入框后按住 W 两秒：`value.Length` 不变**且**位移为 0；Esc 后 W 只走路 | 长度增长且人后退 |
| 相机稳定 | 移动中每帧 `Pivot.eulerAngles.y - Yaw` < 0.5°，相机 yaw 一阶差分无符号翻转 | 数度级抖动 |
| 第一人称 | 最近距离鼠标上移 100 px：画面中心射线仰角同向变化 ≈2.2°/单位，无横向位移 | 方向近乎反相 |
| 输入顺序 | 微软拼音连续输入 20 个词（含候选确认与改选），提交前 `value.Length == 20`、`cursorIndex == 20`、字符顺序一致 | 末字错位 |
| 六门 | `tools/check.ps1` `summary: 6/6 ok`，一个 SKIP 都不算过 | 绿 |

## 7. 阶段与顺序

1. **S1 输入门 + 光标**（B1，含 IME）：当天可验证，不碰资产。
2. **S2 灯**（D）：与 S1 独立，可在同一轮；先删 `floor-bounce` 立刻止血。
3. **S3 相机**（C1）：结构改完再谈第一人称；回归 `ActionKiss`/`SitTogether`。
4. **S4 管线**（A1）：切 Linear → 删 shader ACES → Volume → 描边 → MToon → 阴影 → 重跑生成器 → 全量重调。
5. **S5 对照定稿**：出四机位并排图，逐条对 §6，未达标回 S4。

每阶段结束跑六门；S4 单独一个提交，便于整段回滚。

## 8. 留给实现者

- §5 里的数值是起点不是圣旨，可按 §6 判据微调；但判据本身不许改。
- MToon 参数以 VRM 实际材质为准，专家读到的 1/1 需在实现时二次确认。
- 参考图的裁切口径（全身 vs 半身）由实现者按机位定，但要固定下来供后续对比复用。
- 白底档是否需要三点光：默认不加（见 §5.8），若不加灯导致并排图不可比，先记录再决定，不要偷偷加灯。

## 9. 实测修正（2026-11-06 收口）

计划里的判据与假设，落地时有五处必须按实测改写。记在这里以免下一个人重新踩。

1. **postProcessData 为 null，整套调色从未生效**。`apps/unity/Assets/Settings/Mas3D-URP-Renderer.asset` 是早期用 `CreateInstance<UniversalRendererData>()` 生成的，`postProcessData` 一直是 `{fileID: 0}`；URP 遇到 null 会**静默跳过整条后处理**。表现是「`RoomLook.PostExposure` 从 −0.45 改到 −3.0，出图逐像素不变」，而 Volume 与相机开关全是对的（`[play] camera post=True mask=1 stackActive=True stackPost=-3.00`）。修好引用后 ACES/白平衡/Bloom/暗角才第一次真正生效，回归用例 `RoomLookTests.TheRendererIsWiredToPostProcessData` 钉死它。**此前所有观感扫参都是在没有 tonemap 的画面上做的，一律作废。**
2. **皮肤判据重新标定**。ACES 生效后整幅压暗一档，旧筛选（`pr > 195 && pg > 160 && pb > 140 …`）一个像素都选不出来。改成共享谓词 `pr >= 150 && pr-pg >= 18 && pg > pb && pr-pb 20..110 && pb < 235`，参考立绘与出图都落在里面。
3. **脚下光斑改代码级判据**。八机位地面亮度比（最亮/最暗 ≤1.30）替代原来的绝对极差 —— 台灯侧本来就比窗侧亮约 18%，绝对极差会把正常的方向性亮度判红。
4. **阴影用下半幅做分母**。影子只落在能照到直射光的地板与床，全画面分母（含吊顶与墙）会把 5% 的差异稀释成 2%。
5. **MToon 描边不需要自己写 Render Graph Pass**。VRM 包自带 `VRM10.MToon10.MToonOutlineRenderFeature`（6000+ 分支走 URP 内置 `RenderObjectsPass` + `MToonOutline` lightmode），直接挂进渲染器资产即可；自己写 `RecordRenderGraph` 那条警告只对手写 Pass 成立。
6. **禁用的 render feature 会让打包版起不来**。观感调参把 SSAO 关掉时只写了 `m_Active: 0`，feature 仍留在 `m_RendererFeatures` 里。玩家构建里 URP 仍会构造它，而它的 shader 因"未启用"被剥离 → `ScreenSpaceAmbientOcclusion.TryPrepareResources()` 抛 `NullReferenceException` → `CreatePipeline()` 每帧失败，连带 `Blitter is already initialized` 刷屏，窗口黑着像"打不开"（2026-11-06 23:0x `build/play.log` 实测；编辑器里完全正常，六门也抓不到——player 门只跑 `-selftest`）。修法是把 feature 从资产里删掉，`OutlineTests` 改成断言"SSAO 必须不存在"。教训：**任何"先关掉"的 render feature，都要么删除、要么在 Player 里保住它的 shader 并实机验一次打包件。**

另外两条语义变更（都来自 YG 实机反馈 m17495）：

- **俯仰方向**：`Quaternion.Euler(Pitch,…)` 里 X 为正 = 低头，而鼠标上推给的是正值，所以 `SetOrbit` 必须 `Pitch - deltaPitch * Sensitivity`。旧用例断言「Pitch 变大」把数值当语义，因此测试绿而手感反 —— 新用例改为断言最终视线 `forward.y > 0`。
- **键盘不再有第二态**：Esc 曾经把走路键盘「锁」上、等焦点回输入框才解，界面上毫无提示，实机表现就是「方向键控制一会儿又没反应」。现在门只认一件事：焦点在不在输入框。E 只负责进入输入框（打字时 E 就是一个字母），退出交 Esc 与点面板外。

## 10. 未完成（下一轮）

- **眼睛**：虹膜偏荧光黄绿、眼周硬黑圈，与参考的柔绿虹膜 + 白色高光仍有差距。属于贴图级改造（虹膜渐变 + 高光），不是材质参数能补。
- **真机输入法连打**：`EnterWhileComposingDoesNotSend` 只能钉到面板层；微软拼音连打 20 词的整链验证要在编辑器/打包版里手工跑（见 §6 判据表）。
