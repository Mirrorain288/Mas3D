# Unity Bridge Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unity 客户端能一连上 bridge 就完整走一轮对话——打字面板输入中文、发出 `say`、按 `ack → thinking → reply[/intent]` 收齐、回复落在面板上，并且这条链路有可重复的无头冒烟。

**Architecture:** 纯增量。bridge / packages/protocol 的语义一个字不改，Unity 作为第二个客户端接 `ws://127.0.0.1:8790/mas3d/ws`（`?token=` 鉴权）。C# 侧手写帧镜像，用**共享 JSON 语料**把 TS 解析器与 C# 解析器的判定钉在一起；网络层是 `ClientWebSocket` 的收发循环，外加可注入的 URL/令牌与退避重连；UI 是 UI Toolkit 的运行时面板，会话状态机与网络解耦，所以 PlayMode 测试可以只驱动状态机。

**Tech Stack:** Unity 6.6.7f1 + URP（已就位）、C#（.NET Standard 2.1 / ClientWebSocket）、NUnit（Unity Test Framework，EditMode + PlayMode）、UI Toolkit（UXML/USS）、Node（`tools/mock-bridge.mjs`，既有）。

**Spec:** `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（§3 端口与令牌、§5.3 网络层、§5.5 UI、§7 的 P1）

## Global Constraints

- **协议不许改**：`apps/bridge/`、`packages/protocol/src/` 本计划内**零改动**（新增测试与语料除外）。Unity 是消费者。
- `MAX_INPUT_CHARS = 300`（`packages/protocol/src/frames.ts:30`）；超长由面板拦下，不发出去。
- 服务端帧：`ack` / `thinking` / `reply` / `intent` / `error`；`reply.cue` 与 `reply.voice` 可缺省。
- `ExpressionCue`：`emotion ∈ {neutral,joy,fun,sorrow,angry,surprised}`、`intensity ∈ [0,1]`、`gaze ∈ {player,away,down,up,left,right}`、`gesture ∈ {none,nod,shake,tilt,wave,lean_in,look_away}`。
- `MotionPlan`：`base{change:bool,name:1..40,motion:0..1,rest:bool}`、`operation ∈ {chat,command,release,unsupported}`、`timing ∈ {同时,依次}`、`intents ≤ 4`，每个意图 `{key:非空,speed:0.4..2,strength:0..1,hold:0..20,delay:0..30}`；`command` 需要至少一个 intent 或 `base.change`，其余 operation 不许带 intent；`reply` 缺失与 null 等价、上限 600。
- WS 默认 `ws://127.0.0.1:8790/mas3d/ws`，鉴权走 query `?token=<64 hex>`（也可用 `Authorization: Bearer`）；坏令牌服务端 close code **1008**。
- 客户端帧只有 `{"type":"say","text":"…","turnId":"可选"}`。
- `tools/unity.ps1` **必须保持纯 ASCII**（PS 5.1 读无 BOM UTF-8 会解析失败）；`.cs` 里不许用 `GetInstanceID()`（Unity 6.6 是 error CS0619），需要键就用名字。
- **不许执行 `git commit` / `git push`**：本仓库约定由 Lead 统一提交。每个任务结束时 `git add -A`（只入暂存区）并报告，评审用 `git diff HEAD` 取包。
- 无头批处理默认带 `-nographics`；只有截图任务（`capture`）例外。PlayMode 测试不加 `-nographics` 也能跑，统一用 `tools/unity.ps1 test -Mode PlayMode`。
- 新增 C# 必须落进既有 asmdef（`Mas3D.Runtime` 运行时代码、`Mas3D.Editor` 编辑器、`Mas3D.Tests.PlayMode`/新建的 EditMode 测试），不许偷偷靠默认程序集。
- 中文文案按现有 web 端口吻（"她坐在书桌前等你先开口。"这类整句），不许出现"AI 助手"自称。

## Review Focus

1. **坏令牌 / 服务端宕机**：连接被 1008 拒绝或端口没人听时，面板必须显示可读错误并保留输入框内容，而不是静默卡在"待机"。
2. **半轮对话**：只说 `ack`+`thinking` 之后服务端消失（`timeout` 场景），状态机必须退出"思考中"并允许再次发送，不许永久 disable 输入。
3. **超长与空白输入**：301 字的粘贴、只有空格的输入、含换行的输入——面板要按 300 字截断/拦下、空白不发、换行不炸 JSON。
4. **重连风暴**：bridge 先挂后起，客户端退避重连不能打出每秒几十次的连接尝试，也不能在已经连上后重复建连。
5. **帧的宽容度**：`reply` 带未知字段、`cue` 缺失、`intent` 的 `reply:null`——必须按帧解析通过而不是整条丢弃或抛异常打断连接。

---

### Task 1: 协议镜像与共享契约语料

**Files:**
- Create: `packages/protocol/test/fixtures/frames-cases.json`
- Create: `packages/protocol/test/frames-cases.test.ts`
- Create: `apps/unity/Assets/Scripts/Net/MotionPlan.cs`
- Create: `apps/unity/Assets/Scripts/Net/ServerFrame.cs`
- Create: `apps/unity/Assets/Scripts/Net/ClientFrame.cs`
- Create: `apps/unity/Assets/Tests/EditMode/Mas3D.Tests.EditMode.asmdef`
- Create: `apps/unity/Assets/Tests/EditMode/FrameProtocolTests.cs`

**Interfaces:**
- Produces（后续任务直接用）:
  - `namespace Mas3D.Net`：`ServerFrame`（`public string Type; public string TurnId; public string Text; public ExpressionCue Cue; public VoiceRef Voice; public MotionPlan Plan; public string Code; public string Message;`）、`ExpressionCue`、`VoiceRef`、`MotionPlan`/`MotionBase`/`MotionIntent`（字段名同 JSON，`public` 字段即可）
  - `public static class FrameParser { public static ServerFrame Parse(string json); }` 解析失败抛 `ProtocolException`（`namespace Mas3D.Net`，`public sealed class ProtocolException : Exception`）
  - `public static class ClientFrame { public const int MaxInputChars = 300; public static string Say(string text, string turnId = null); public static string Say(ServerFrame previous)? }`——**只保留 `Say(string text, string turnId = null)` 与 `MaxInputChars`**
- Consumes: 无（第一个任务）

- [ ] **Step 1: 写共享语料**

`packages/protocol/test/fixtures/frames-cases.json`，形状与既有 `plan-cases.json` 一致：`{"cases":[{"name":"…","ok":true,"frame":{…}},{"name":"…","ok":false,"error":"…"}]}`。至少覆盖 14 条，`ok:true` 的必须让 TS 侧解析通过：

1. ack 最小帧
2. thinking 最小帧
3. reply 只有 type/turnId/text（cue、voice 都缺省）
4. reply 带完整 cue（emotion 取 `joy`、intensity 0.6、gaze `player`、gesture `wave`）
5. reply 带完整 voice（`audioUrl:"/mas3d/api/voice/v1/audio"`、durationMs 1800、sentenceStartsMs [0,720]、englishText "Good evening."）
6. intent 带 command plan（一个 intent，速度 1、强度 0.8、hold 2、delay 0）
7. intent 的 `plan.reply` 为 `null`（等价缺失，必须解析通过）
8. error 带 turnId 与 code `timeout`
9. error 不带 turnId、code `bridge_down`
10. `ok:false`：未知 type（`"type":"pause"`）
11. `ok:false`：ack 缺 turnId
12. `ok:false`：cue.intensity = 1.5（越界）
13. `ok:false`：plan.timing = `"并行"`
14. `ok:false`：command plan 没有 intent 且 `base.change` 为 false

- [ ] **Step 2: 写 TS 侧契约测试**

`packages/protocol/test/frames-cases.test.ts`：

```ts
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { parseServerFrame } from '../src';

const corpus = JSON.parse(readFileSync(fileURLToPath(new URL('./fixtures/frames-cases.json', import.meta.url)), 'utf8')) as {
  cases: { name: string; ok: boolean; frame?: unknown }[];
};

describe('frames corpus', () => {
  for (const c of corpus.cases) {
    it(c.name, () => {
      const run = () => parseServerFrame(c.frame);
      if (c.ok) expect(run).not.toThrow();
      else expect(run).toThrow();
    });
  }
});
```

- [ ] **Step 3: 跑 TS 测试，确认语料自身站得住**

Run: `cd packages/protocol && pnpm exec vitest run`
Expected: 现有 `frames.test.ts`/`plan.test.ts` 仍全绿，新增 14 条 `frames corpus` 用例全绿。若有 `ok` 判反的，**改语料不改解析器**——TS 解析器是权威。

- [ ] **Step 4: 写 C# 解析器的失败测试**

`apps/unity/Assets/Tests/EditMode/Mas3D.Tests.EditMode.asmdef`（照抄 PlayMode 那份，改 name 为 `Mas3D.Tests.EditMode`、`includePlatforms: ["Editor"]`、`references` 含 `Mas3D.Runtime`、保留 `UnityEngine.TestRunner`/`UnityEditor.TestRunner`、`precompiledReferences ["nunit.framework.dll"]`、`overrideReferences: true`、`defineConstraints ["UNITY_INCLUDE_TESTS"]`）。

`apps/unity/Assets/Tests/EditMode/FrameProtocolTests.cs`：从仓库根读**同一份**语料（`Application.dataPath` 连上 `../../..` 就是仓库根，再拼 `packages/protocol/test/fixtures/frames-cases.json`），逐条断言 C# 判定与 `ok` 一致：`ok:true` 的必须 `FrameParser.Parse` 不抛异常，`ok:false` 的必须抛 `ProtocolException`；用例名带上语料里的 `name` 以便定位。断言语料条数 ≥ 14。

另外补三条独立用例：`Say` 生成的 JSON 恰好是 `{"type":"say","text":"…"}`（`turnId` 为空时**不出现**该键）、`MaxInputChars == 300`、`reply` 带未知字段 `{"type":"reply","turnId":"t","text":"嗯","extra":1}` 解析成功。

- [ ] **Step 5: 实现解析器**

`apps/unity/Assets/Scripts/Net/ServerFrame.cs`：**不要用 `JsonUtility`**（它无法表达"字段缺失 vs null"、也不支持 `Dictionary`/联合类型）。用 `Newtonsoft.Json` 读 `JObject`。**依赖来源已定**：官方包以本地 tarball 形式钉在 `apps/unity/Packages/com.unity.nuget.newtonsoft-json-3.2.1.tgz`，`Packages/manifest.json` 写 `"com.unity.nuget.newtonsoft-json": "file:com.unity.nuget.newtonsoft-json-3.2.1.tgz"`（registry 版本会撞 UPM 的 "You are not signed in" 门禁；不要把 DLL 拷进 `Assets/Plugins` 放第二份，出处与 SHA256 见 `apps/unity/Packages/README.md`），逐字段按 `packages/protocol/src/frames.ts` 的规则校验，任何不合规抛 `ProtocolException` 并把路径写进消息（`reply.cue.intensity must be within 0..1` 这种口径）。

`MotionPlan.cs` 按 `packages/protocol/src/plan.ts` 的 12 条规则逐条实现（含 `command` 至少一个 intent 或 base.change 的互斥、`reply` null 等价缺失、`MAX_INTENTS=4`、`MAX_BASE_NAME=40`、`MAX_REPLY=600`、五段数值区间）。

`ClientFrame.cs`：`Say(text, turnId)` 用 `JObject` 组帧；`turnId` 为 null/空时不写这个键。

- [ ] **Step 6: 跑 Unity EditMode 测试**

Run: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\unity.ps1 test -Mode EditMode`
Expected: 新增用例全绿；`TestResults/EditMode.xml` 里 `failed="0"`。

- [ ] **Step 7: 暂存并报告**

Run: `git add -A`（**不要 commit**）。报告里给出两个测试命令的实际输出行数与失败数。

---

### Task 2: WebSocket 客户端与重连

**Files:**
- Create: `apps/unity/Assets/Scripts/Net/BridgeSettings.cs`
- Create: `apps/unity/Assets/Scripts/Net/BridgeClient.cs`
- Create: `apps/unity/Assets/Tests/PlayMode/MockBridgeProcess.cs`
- Create: `apps/unity/Assets/Tests/PlayMode/BridgeClientTests.cs`
- Modify: `apps/unity/Assets/Tests/PlayMode/Mas3D.Tests.PlayMode.asmdef`（测试代码用到 `Mas3D.Net` 已在 `Mas3D.Runtime` 里，通常无需改；若报 CS0246 才加）

**Interfaces:**
- Consumes: Task 1 的 `FrameParser.Parse`、`ClientFrame.Say`、`ServerFrame`
- Produces:
  - `namespace Mas3D.Net`：`public sealed class BridgeSettings { public string Url = "ws://127.0.0.1:8790/mas3d/ws"; public string Token = ""; public float[] BackoffSeconds = { 0.5f, 1f, 2f, 4f, 8f }; public static string TokenFilePath { get; } }`
  - `public sealed class BridgeClient : IDisposable`：
    - `public BridgeState State { get; }`（`enum BridgeState { Idle, Connecting, Connected, Waiting, Closed }`）
    - `public event Action<BridgeState> StateChanged;`
    - `public event Action<ServerFrame> FrameReceived;`
    - `public event Action<string> Failed;`（人话错误，直接可显示）
    - `public void Configure(BridgeSettings settings);`
    - `public void Start(); public void Stop();`
    - `public bool SendSay(string text);`（未连接返回 false，不抛）
    - `public void Pump();`（主线程泵：把后台收到的帧派发出去；PlayMode 测试与无头冒烟都调它）
- Consumes 测试侧：`MockBridgeProcess`（`public static MockBridgeProcess Start(string scenario, int port, string token = null)`、`public string Url { get; }`、`public void Dispose()`）——用 `System.Diagnostics.Process` 起 `node tools/mock-bridge.mjs --port … --scenario … [--token …]`，`Dispose` 里 `Kill(entireProcessTree: true)`。

- [ ] **Step 1: 写失败测试（对真 mock-bridge）**

`BridgeClientTests.cs` 四条 PlayMode 用例：

1. `ConnectsAndReceivesAWholeTurn`：起 `happy` 场景 + 固定令牌 → `Configure` + `Start` → 等 `State == Connected`（≤ 10 s，每帧 `Pump()`）→ `SendSay("晚上好")` → 收集帧直到出现 `reply`，断言顺序里先有 `ack` 再有 `reply`、`reply.Text` 非空、`TurnId` 一致。
2. `RejectsABadToken`：服务端起带令牌 `secret`，客户端用 `wrong` → 断言 `Failed` 被触发、`State` 不再回到 `Connected`、错误文案里含 `令牌`。
3. `ReportsAnUnreachableBridge`：端口用 6553（无人听）→ `Failed` 在 ≤ 10 s 内触发，文案含 `连接`。
4. `DoesNotSendWhenDisconnected`：不 `Start` 直接 `SendSay` → 返回 false，无异常。

- [ ] **Step 2: 跑测试确认失败**

Run: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\unity.ps1 test -Mode PlayMode`
Expected: 编译失败（`BridgeClient` 不存在）或四条全红。

- [ ] **Step 3: 实现 `BridgeSettings` 与 `BridgeClient`**

要点：
- `Start()` 起一个后台 `Task` 跑连接循环：`ClientWebSocket` → `ConnectAsync(new Uri(Url + "?token=" + Uri.EscapeDataString(Token)))` → 收循环 `ReceiveAsync` 到 `ConcurrentQueue<string>`；`Pump()` 在主线程取出 JSON → `FrameParser.Parse` → `FrameReceived`。
- 收到非 `NormalClosure` 的关闭码（1008 令牌坏）**不重连**，只 `Failed("令牌不正确…")`；网络异常按 `BackoffSeconds` 退避重连，且退避期间 `State == Connecting` 只切一次，不许打出重连风暴。
- 解析失败的单帧只 `Debug.LogWarning`，不打断连接（Review Focus 第 5 条）。
- `Stop()/Dispose()` 必须幂等并取消后台任务，测试之间不许残留连接。
- 所有回调都在 `Pump()` 所在的线程触发；后台线程只入队，不碰 Unity API。

- [ ] **Step 4: 跑测试确认通过**

Run: 同上。Expected: 四条全绿，且 `TestResults/PlayMode.xml` 里之前 6 条 P2 用例仍然通过。

- [ ] **Step 5: 暂存并报告**

Run: `git add -A`（不 commit）。报告里写清 mock-bridge 的启动参数与四条用例的实际耗时。

---

### Task 3: 会话状态机与打字面板

**Files:**
- Create: `apps/unity/Assets/UI/Dialogue.uxml`
- Create: `apps/unity/Assets/UI/Dialogue.uss`
- Create: `apps/unity/Assets/UI/DialoguePanelSettings.asset`（`PanelSettings`，`ScaleWithScreenSize`，参考分辨率 1280×720）
- Create: `apps/unity/Assets/Scripts/UI/DialoguePanel.cs`
- Create: `apps/unity/Assets/Scripts/Conversation/ConversationController.cs`
- Create: `apps/unity/Assets/Tests/PlayMode/DialoguePanelTests.cs`
- Create: `apps/unity/Assets/Tests/PlayMode/ConversationTests.cs`

**Interfaces:**
- Consumes: Task 1 `FrameParser`/`ClientFrame`，Task 2 `BridgeClient`（`SendSay`、`FrameReceived`、`StateChanged`、`Failed`、`Pump`）
- Produces:
  - `public sealed class DialoguePanel : MonoBehaviour`（`namespace Mas3D.UI`）：`public UIDocument Document;` `public event Action<string> Submitted;` `public void SetStatus(string text);` `public void AppendPlayer(string text);` `public void AppendReply(string text);` `public void SetThinking(bool on);` `public void SetError(string text);` `public string InputText { get; set; }` `public bool Interactable { get; set; }` `public string LogText { get; }`（全部已显示行的纯文本拼接，测试用）；内部把字符计数显示成 `n/300`。
  - `public sealed class ConversationController : MonoBehaviour`（`namespace Mas3D.Conversation`）：`public BridgeClient Client; public DialoguePanel Panel;` `public string PendingTurnId { get; }` `public TurnPhase Phase { get; }`（`enum TurnPhase { Idle, Sending, Thinking, Answered, Failed }`）、`public bool Submit(string text);`
- 文案（照抄，别改写）：待机 `她坐在书桌前等你先开口。`；思考中 `她正在想…`；超时 `她这次没有回应。`；桥接断开 `桥接服务没有响应，先由本地兜底回话。`

- [ ] **Step 1: 写失败测试**

`DialoguePanelTests.cs`（三条）：
1. `PanelBuildsFromUxml`：实例化 UIDocument + Dialogue.uxml，`Panel.Document` 挂上后断言输入框、发送按钮、计数标签都存在（按 `name` 查询：`input`、`send`、`counter`、`log`）。
2. `CounterFollowsTheThreeHundredLimit`：`InputText` 设 301 字 → `InputText` 长度 300、`counter` 文本为 `300/300`。
3. `EnterSubmitsAndBlankDoesNot`：`Submitted` 事件计数；`InputText = "  "` 触发提交 → 0 次且 `InputText` 保留；`InputText = "晚上好"` 回车 → 1 次、载荷是 `晚上好`。

`ConversationTests.cs`（三条，全部用假 BridgeClient 替身或直接喂帧）：
1. `ASayMovesThePhaseToThinking`：`Submit("晚上好")` → `Phase == Thinking`、面板 `Interactable == false`。
2. `TheReplyEndsTheTurn`：喂 `ack`→`thinking`→`reply` 三帧 → `Phase == Answered`、面板出现她的话、`Interactable == true`。
3. `AnErrorFrameEndsTheTurnWithAText`：喂 `error{code:"timeout"}` → `Phase == Failed`、面板错误文案是 `她这次没有回应。` 且**输入框内容保留**（Review Focus 第 2 条）。

- [ ] **Step 2: 跑测试确认失败**

Run: `… unity.ps1 test -Mode PlayMode`

- [ ] **Step 3: 实现 UXML/USS 与两个脚本**

面板布局：右上角一块半透明圆角面板（宽 420、高 260，圆角 12，背景 `rgba(18,16,22,0.82)`），自上而下：状态行（状态文本 + `设置`/`导出`/`清空` 三个按钮，本任务只做 `清空`）、滚动区 `log`（玩家行右对齐、她的话左对齐）、输入框 `input`（占位 `和她说点什么（Enter 发送，Shift+Enter 换行）`）、底部 `counter` + `send`。样式与既有 web 端截图对齐（深色、小圆角、0.5 px 描边），不要引外部字体。

`DialoguePanel` 里 `UIDocument` 由 Inspector 赋值；`Awake` 里 `rootVisualElement.Q<TextField>("input")`，注册 `RegisterCallback<KeyDownEvent>` 处理 Enter/Shift+Enter。**中文 IME**：用 `KeyDownEvent` 判断 `evt.keyCode == KeyCode.Return` 且 `evt.shiftKey == false` 才提交，不要用 `TextField` 的 `isDelayed`。

`ConversationController` 订阅 `Client.FrameReceived`：`ack` 记 turnId → `thinking` 保持 Thinking → `reply` 落面板并 `Answered` → `error` 落错误文案并 `Failed` → `intent` 本阶段只 `Debug.Log`（P4 才消费）。`Update()` 里调 `Client.Pump()`。

- [ ] **Step 4: 跑测试确认通过**

Run: 同上。Expected: 六条新用例全绿，旧的 6 条 PlayMode + 4 条 BridgeClientTests 仍绿。

- [ ] **Step 5: 暂存并报告**

Run: `git add -A`（不 commit）。报告附一张运行后的面板截图（`apps/unity/Shots/ui-01.png`，用 `CliPlayer.Capture` 的写法或 PlayMode 里 `ScreenCapture` 皆可）。

---

### Task 4: 场景接线与无头冒烟

**Files:**
- Modify: `apps/unity/Assets/Editor/CliPlayer.cs`
- Create: `apps/unity/Assets/Tests/PlayMode/BridgeSmokeTests.cs`
- Modify: `tools/unity.ps1`

**Interfaces:**
- Consumes: Task 2 `BridgeClient`/`BridgeSettings`，Task 3 `DialoguePanel`/`ConversationController`，测试侧 `MockBridgeProcess`
- Produces: `tools/unity.ps1 smoke`（跑 `BridgeSmoke` 这一组 PlayMode 用例并打印 `[smoke]` 标记）

- [ ] **Step 1: 写失败测试**

`BridgeSmokeTests.cs` 一条端到端用例 `OneTurnAgainstTheRealMockBridge`：起 `happy` mock-bridge → 建场景里的 `ConversationController` + `DialoguePanel`（`Resources` 或直接在测试里 `new GameObject` 拼）→ `BridgeClient` 指向它 → `Submit("晚上好")` → 每帧 `Pump`，≤ 15 s 内断言 `Phase == Answered` 且面板里有非空回复；测试结束断言进程被杀干净（`MockBridgeProcess` 的 `Dispose`）。

- [ ] **Step 2: 跑测试确认失败**

Run: `… unity.ps1 test -Mode PlayMode`

- [ ] **Step 3: 接线场景 + 冒烟子命令**

`CliPlayer.BuildScene` 里加：`Dialogue` 预制体（UIDocument + `DialoguePanel`，PanelSettings 用 `Assets/UI/DialoguePanelSettings.asset`，缩放 `ScaleWithScreenSize` 参考 1280×720）、`Session` 上加 `ConversationController` 并引用 `BridgeClient` 与面板；`BridgeClient` 挂在 `Session` 同物体，令牌从 `BridgeSettings.TokenFilePath` 读（文件不存在就空串并让面板显示"桥接未配置令牌"）。场景仍要能**离线**打开：没连上时面板显示待机文案，不许报错弹窗。

`tools/unity.ps1` 加 `'smoke'`：跑 `test -Mode PlayMode --filter Mas3D.Tests.PlayMode.BridgeSmokeTests`（`unity test` 支持 `--filter`；若该 CLI 版本不支持，就退化为跑全量 PlayMode 并用 `Select-String '\[smoke\]'` 过滤输出），并 tail 出 `[smoke]` 行。

- [ ] **Step 4: 跑冒烟确认通过**

Run: `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & D:\Mas3D\tools\unity.ps1 smoke`
Expected: `[smoke] turn ok turnId=…` 一行、退出码 0。

- [ ] **Step 5: 暂存并报告**

Run: `git add -A`（不 commit）。报告写清 `smoke` 的实际输出与「断网时打开场景」的行为（截图或日志行）。

---

## 不在本计划内（各自单独开计划）

- Monkia.vrm / Me.vrm 的导入与待机动画（asset pipeline 子系统，需要先定 VRM 依赖）。
- P3 语音（`/mas3d/api/asr` 与按住说话）。
- P4 四件动作的演出态与 IK。