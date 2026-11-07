# 2026-11-07 交互键与焦点修复 · 设计

## 1. 意图与为什么

YG 实机反馈三条，都是「键盘说不清自己在干什么」：

1. **Esc 退不出输入框**；左键点面板外之后能走了，但「过一会又进了输入框」，走不动。
2. **蹲 / 坐 / 跪没有实机键**，只有代码入口，玩家无法自救姿态。
3. **牵手 / 摸头 / 亲吻 / 并肩坐没有实机键**，动作只有测试与编辑器工具能触发。

目标不是「多加几个按键」，是让键盘在**走路、打字、姿态、动作**四个状态之间有一条玩家能预期的规则，并且每一次失败都要有回声——现在按不动的东西一律静默，玩家分不清是没绑键还是够不着。

## 2. 目标 / 非目标 / 反目标

**目标**
- Esc 在任何焦点层级下都能退出输入框，退出后当帧就恢复走路。
- 焦点变化可观察：一条脱敏日志 + 一条用例，能把「焦点什么时候、被什么抢回去」钉住。
- 1–5 触发五件动作，够不着时状态行给出原因；C / Z / X 切换蹲 / 跪 / 坐。
- 全部按键都能在批处理里用接缝驱动，不需要合成真实按键。

**非目标**
- 不做「准星对准 + 提示条 + E 进演出态」（spec §8.1 的原方案）。它需要准星、交互体、提示 UI 三件东西，与固定键是两条路；本轮选固定键，§8.1 保留为将来。
- 不改动作本身的几何、判据与演出时长。
- 不换 Input System（工程仍是 activeInputHandler: 0）。

**反目标**
- 演出中按键把姿态改掉、把玩家从演出机位里拽出来。
- 任何「按了没反应也不说为什么」的静默拒绝。
- 再引入一条「隐形键盘锁」——m17495 第 4 条（走一会儿突然失控）就是那么来的。

## 3. 现状与约束（读过的代码）

- 键盘门只有一条：PlayerMotor.WantsKeyboard => KeyboardInput && (Panel == null || !Panel.IsInputFocused)（PlayerMotor.cs:70-72）。KeyboardInput 是总闸，演出期间由 ActionDirector 置 false（ActionDirector.cs:194 附近）。
- InputModes 现在只管 E（进框）与 Esc（出框），Esc 走 PressEscape() -> DialoguePanel.BlurInput()（InputModes.cs:82-85、DialoguePanel.cs:199-206）。
- IsInputFocused 承认焦点可能在 TextField 的**内层** TextElement（DialoguePanel.cs:171-183，用 _input.Contains(focused)）。
- 动作入口 ActionDirector.TryEnterAction(id) 是「问题不是命令」：busy、未知 id、够不着都返回 false 并留 LastFaults（:157、:179-188）。id 常量在 ActionId.cs:10-15：hands / pat-her / pat-me / kiss / sit-together。
- 姿态 PlayerMotor.SetPosture(posture, seatTopY)（:137-141），Sitting 只有拿到座面高度才落得对。
- 状态行 API：SetStatus（DialoguePanel.cs:277）、SetError（:329，作为一条 notice 行进消息流，可回看）。
- 输入是老的 Input Manager，全部读 Input.GetKeyDown。

## 4. 方案（已选 A）

**A. 新增 ActionHotkeys，与 InputModes 并列**
一张表把 Alpha1..Alpha5 映到动作 id，C/Z/X 映到姿态；Update 只做「按键 → 接缝」的翻译，接缝是 PressAction(int slot) 与 PressPosture(PlayerPosture)。门与 InputModes 共用同一条谓词，演出期间整块禁掉。拒绝时把 LastFaults 里的原因写进面板。
*代价*：多一个组件、多一份场景接线；*好处*：InputModes 保持「键盘门」单一职责，动作键可以有自己的一套用例而不去动门的用例。

**B. 全塞进 InputModes**
少一个组件，但那个类的用例全钉在门语义上，塞进去以后每加一件动作都要改它的测试，且「门」与「动作」两种失败模式混在一起。

**C. 上 Input Actions 资产**
键位可视化、可重绑，但要重接 WASD / 鼠标 / UI 焦点，工程当前是老输入，迁移成本与收益不成比例。

选 A。

## 5. Esc 与焦点（bug，两个假设）

**H1（主）**：_input.Blur() 关的是外层 TextField，而真实焦点在内层 TextElement，所以 IsInputFocused 仍为真 → WantsKeyboard 假 → Esc「什么都没发生」。批处理用例只覆盖了 FocusInput() 直接聚焦外层的情况，所以一直是绿的。
**H2**：组词中 Esc 被输入法吃掉，Unity 压根收不到这个键；表现与 H1 一模一样。

两条都防：
1. BlurInput() 先取 focusController.focusedElement，只要在 _input 子树内就对它 Blur()，再 _input.Blur() 兜底，最后**复查** IsInputFocused，仍为真就 focusController.Blur() 强清。
2. 面板根节点监听 KeyDownEvent（TrickleDown）的 Escape，不依赖焦点在哪一层；与 InputModes 同键值，两条路都指向 BlurInput()（幂等）。
3. 焦点变化日志：[focus] <from> -> <to> reason=<focus|escape|outside-click|panel-reenable>，只在变化时打，元素名脱敏成类型名。
4. 「过一会又自己回来」的真因还没定——日志就是为它准备的。若确认是 UI Toolkit 在元素 re-enable 时恢复上次焦点，则 ApplyInteractable 永久不许对输入框 SetEnabled，并且焦点回来时日志里要能看出是 panel-reenable。

## 6. 键位（YG 已确认）

| 键 | 作用 |
|---|---|
| 1 | 牵手 hands |
| 2 | 摸她头 pat-her |
| 3 | 她摸我头 pat-me |
| 4 | 亲吻 kiss |
| 5 | 并肩坐 sit-together |
| C | 蹲（再按回站） |
| Z | 跪（再按回站） |
| X | 坐（再按回站；1.0 m 内没有座面就拒绝） |

细则：
- 只在 KeyboardInput && !Panel.IsInputFocused 时读键；打字时 1–5 就是数字。演出期间（ActionDirector 已把总闸关掉）动作键与姿态键都不响应。
- 动作键：TryEnterAction 返回 false → SetError(原因)（消息流留痕）+ SetStatus(「够不着」)，让玩家知道是「够不着」而不是「没绑」。
- 姿态键：SetPosture(..., seatTopY)；坐需要座面高度，取最近座节点（S1 凳 0.60 / S2 床 0.91），找不到就拒绝并回声。
- 动作键不回退姿态：动作自己会摆玩家，取消由 Esc 走 ActionDirector.Cancel()（已有）。

## 7. 判定（可测）

- EditMode：键表唯一（无重复键、无重复 id）、覆盖全部五个 id。
- PlayMode（每条都先红后绿）：
  - 五件动作各一条：她坐在可及位置时按 1–5 → ActionDirector 进入对应动作；站远/站姿够不着时 → 不进、位置与姿态不变、面板里出现原因。
  - 三条姿态各一条：按下切换、再按回站；演出中按键无效；远离座位按 X 被拒。
  - Esc 两条：焦点在内层元素时 Esc 仍能出框且 WantsKeyboard 立刻为真；组词态（ApplyComposition("ni")）下 Esc 同样出框。
- 计数不许倒退：EditMode 95 起、PlayMode 225 起，只增不减。

## 8. 留给实现者

- 内层元素怎么取（textEdition 非公开，可能得 _input.Q<TextElement>()）；取不到时的兜底路径。
- 座面查询的数据源（EdgeTable 的座节点 vs AnchorTable.Seat）与半径。
- 状态文案的具体措辞、日志粒度。
- 键位要不要做成可在 Inspector 改的字段（默认建议：动作键是数组，姿态键是三个 KeyCode 字段）。

## 9. 风险

- 焦点回流的真因可能不在我们代码里。若日志显示它来自 UI Toolkit 内部且兜底也压不住，退路是把输入框改成「按 E 才显示并聚焦」的模态条——那是一次更大的改动，届时再单独设计。
- 数字键将来可能与热键冲突；先做成字段，别写死。
