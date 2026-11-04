# P3 语音：桥接 ASR + 按住 V 说话（实施计划）

For agentic workers: 本计划用 subagent-driven-development 逐 Task 执行。每个 Task 由全新实现者按 brief 完成，TDD 顺序（先红后绿），Lead 评审后再进下一个。

Goal: 玩家按住 V 开麦、松开提交，本地 Whisper 识别成文本回填到对话框输入框（**不自动发送**），全程不出本机。

Architecture: 识别在桥接进程内完成（`apps/bridge/asr.py`，openai-whisper 常驻，small 默认、tiny 兜底，与 TTS 共用一把串行锁），走 `POST /mas3d/api/asr`；Unity 侧 `VoiceCapture`（麦克风）→ `WavEncoder`（16-bit PCM/WAV）→ `AsrClient`（UnityWebRequest + Bearer）→ `VoiceInput`（按住/松开状态机）→ `DialoguePanel.InputText`。

Tech Stack: Python 3.12（`D:\Astrbot\backend\python\python.exe`，fastapi 0.141 / uvicorn 0.53 / openai-whisper 20250625 / torch 2.11+cu128 / numpy 2.5）、pytest；Unity 6000.6.7f1 + URP + Unity Test Framework（PlayMode）；C# 目标 .NET Standard 2.1。

Spec: `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` 第 104-105 行（ASR 端点 / 常驻模型 / 固定 initial_prompt / 与 TTS 共用串行锁）与第 186 行（P3 验收：`/mas3d/api/asr` + 按住说话 + 文本回填）。

## Global Constraints

- 端点只绑 127.0.0.1，鉴权沿用 `Authorization: Bearer <token>`（`apps/bridge/data/mas3d/token`）；Unity 侧令牌只从 `BridgeSettings.TokenFilePath` 读。
- 音频入参固定 **16 kHz / 单声道 / 16-bit PCM WAV**，时长上限 **30 s**，体积上限 **8 MiB**。
- 识别 prompt 固定为 `以下是一段普通话简体中文字幕。`（spec 实测：tiny CER 22.9% → 0/0/6.25%，small ≈ 2%）；`language="zh"`。
- 模型常驻进程内；small 加载失败才退 tiny；ASR 与 TTS 共用一把 `asyncio.Lock`，避免抢 CUDA。
- 识别结果**只回填输入框**，绝不自动发送；空识别给提示文案，不报错。
- Unity 侧禁止新依赖；JSON 用 `JsonUtility`，HTTP 用 `UnityWebRequest`。
- tools/unity.ps1 必须纯 ASCII；Unity 输出必须 `*>` 落文件再 `Select-String`（否则管道里的 stderr 会变成假 exit 1）。
- 实现者不 `git commit` / `git push`，只 `git add -A apps/unity apps/bridge docs`；提交由 Lead 在交付时统一做。
- 基线：PlayMode 59 total / 58 passed / 1 skipped（跳过的都是 `MissingMonkia` 守卫或既有 `[Explicit] UiSceneShot`）；EditMode 44/44；pytest 全绿。**缺 `Monkia.vrm` 时必须 skip 而不是红**。

## Review Focus

1. WAV 解码必须拒绝非 16-bit、非单声道、超 30 s、空音频，并且拒绝时消息可读（前端要显示）。
2. 串行锁必须真串：两个并发 `transcribe` 不能同时进模型（用慢速假模型断言并发峰值为 1）。
3. `fp16` 只在 CUDA 上开；CPU 上开 fp16 会直接抛错。
4. `VoiceInput` 松手时**先停止录制再编码**，且录音短于 0.3 s 时给 `没听清，再说一次？` 而不是发请求。
5. 识别成功路径必须断言 `Panel.InputText` 被写入且 `Submitted` **没有**触发。

---

## Task 1 — 桥接 ASR 客户端（asr.py）

Files: `apps/bridge/asr.py`（新建）、`apps/bridge/tests/test_asr.py`（新建）

Interfaces:

- `MAX_WAV_BYTES = 8 * 1024 * 1024`、`MAX_SECONDS = 30.0`、`ASR_LANGUAGE = "zh"`、`ASR_PROMPT = "以下是一段普通话简体中文字幕。"`
- `class AsrUnavailable(Exception)`
- `def decode_wav(data: bytes) -> tuple[np.ndarray, int]`：只收 RIFF/WAVE + PCM(1) + 单声道 + 16-bit；返回 `(float32 归一化到 [-1,1] 的样本, 原生采样率)`。异常一律 `ValueError`，消息分别为 `不是 RIFF/WAVE 数据`、`只支持 16-bit PCM`、`只支持单声道`、`音频超过 30 秒`、`采样率超出范围`（<8 kHz 或 >192 kHz）、`空音频`。
- `transcribe` 负责把任意合法采样率线性重采样到 `TARGET_RATE = 16000`（whisper 对 ndarray 输入不做重采样），并在重采样后复检 30 s；`durationMs` 由重采样后的长度算，保证是真实时长。
- `def normalize_text(text: str) -> str`：去首尾空白、把连续空白压成一个空格、去掉两侧引号。
- `class AsrClient`：`__init__(self, model_name="small", fallback="tiny", device=None, lock: asyncio.Lock | None = None, loader: Callable[[str], Any] | None = None)`；`model_name` 属性在首次加载后才变成实际加载到的名字；`async def transcribe(self, wav: bytes) -> dict` 返回 `{"text", "language", "durationMs", "model"}`。
- 加载：`loader` 默认 `lambda name: whisper.load_model(name, device=self.device)`，`device` 默认 `None`（由 whisper 自己选 cuda/cpu，模块不 import torch）；small 抛任何异常就记日志并试 fallback，两者都失败抛 `AsrUnavailable`。
- 推理：`fp16 = str(model.device).startswith("cuda")`（不 import torch）；`model.transcribe(samples, language=ASR_LANGUAGE, initial_prompt=ASR_PROMPT, fp16=fp16, verbose=False)`，跑在 `asyncio.to_thread` 里；整段包在 `async with self._lock`（未传锁则不加）。
- `durationMs` 用样本数 / 采样率算（不依赖 whisper 返回）。

TDD 步骤：

- [ ] 先写 `tests/test_asr.py`：5 条解码拒绝 + 1 条解码成功（用 `wave` 造真实 WAV，断言样本值与长度）+ 1 条 `normalize_text`；断言失败（模块不存在）。
- [ ] 写 `decode_wav` / `normalize_text` 让它们转绿。
- [ ] 再写 transcribe 用例：假 loader 返回记录 kwargs 的 stub；断言 `language`、`initial_prompt`、`fp16` 在 CUDA 上为 True、在 CPU 上为 False、`durationMs` 精确到毫秒、空文本返回 `text: ""`。
- [ ] 写 fallback 用例：small 抛异常 → tiny 加载成功且 `model_name == "tiny"`；两个都抛 → `AsrUnavailable`。
- [ ] 写锁用例：慢模型（`time.sleep(0.15)`）+ `asyncio.gather` 两次 transcribe，断言并发峰值 == 1。
- [ ] `& D:\Astrbot\backend\python\python.exe -m pytest apps/bridge/tests/test_asr.py -q` 全绿；再跑整套 `apps/bridge/tests` 确认没碰坏既有用例。

## Task 2 — `POST /mas3d/api/asr` 与插件接线

Files: `apps/bridge/web_api.py`、`apps/bridge/main.py`、`apps/bridge/tts.py`、`apps/bridge/tests/test_web_api.py`、`apps/bridge/tests/test_tts.py`（若加锁需要）、`apps/bridge/README.md`（端点表加一行）

Interfaces:

- `web_api.py`：常量 `ASR_TIMEOUT_S = 45.0`；路由 `@app.post("/mas3d/api/asr")`，`Depends(require_token)`，`raw = await request.body()`；`len(raw) > MAX_WAV_BYTES` → 413 `{"ok": False, "error": "音频太大（上限 8 MiB）"}`；调 `runner.handle_asr(raw)`，`ValueError` → 400 带原文，`AsrUnavailable` → 503 `{"ok": False, "error": "语音识别不可用"}`, `asyncio.TimeoutError` → 504；成功 `{"ok": True, "asr": {...}}`。
- `main.py`：`initialize()` 里建 `self._voice_lock = asyncio.Lock()`，`self.asr = AsrClient(lock=self._voice_lock)`；`handle_asr(self, raw: bytes) -> dict`；`self.tts = TtsClient(..., lock=self._voice_lock)`。
- `tts.py`：`TtsClient.__init__(..., lock: asyncio.Lock | None = None)`，`synthesize` 整体 `async with self._lock`（未传则不加）；既有行为不变。

TDD 步骤：

- [ ] 在 `tests/test_web_api.py` 追加：无 token → 401；非本机 host → 403；坏 WAV → 400 且 `error` 原文；`> 8 MiB` → 413；`handle_asr` 抛 `AsrUnavailable` → 503；成功 → 200 且 `asr.text` 原样透传。先让它们红。
- [ ] 实现路由让 6 条转绿。
- [ ] `tests/test_tts.py` 加一条：传入同一把锁时，TTS 与 ASR 不并行（假 TTS 请求 + 假 ASR 同时跑，断言峰值 1）；不传锁时行为不变。
- [ ] 跑 `pytest apps/bridge/tests -q` 全绿。

## Task 3 — Unity：WAV 编码器与 ASR HTTP 客户端

Files: `apps/unity/Assets/Scripts/Voice/WavEncoder.cs`、`apps/unity/Assets/Scripts/Voice/AsrClient.cs`（+ `.meta`）、`apps/unity/Assets/Tests/PlayMode/WavEncoderTests.cs`、`apps/unity/Assets/Tests/PlayMode/AsrClientTests.cs`、`apps/unity/Assets/Tests/PlayMode/ScriptedHttp.cs`

Interfaces:

- `namespace Mas3D.Voice`；`public static class WavEncoder { public static byte[] Encode(float[] samples, int sampleRate, int channels = 1); public static float[] Trim(float[] samples, int sampleRate, float seconds); }`：44 字节头（RIFF/WAVE/fmt /data），16-bit little-endian，样本 clamp 到 [-1,1]，空数组仍产出合法头。
- `public readonly struct AsrResult { public readonly string Text; public readonly int DurationMs; public readonly string Model; }`
- `public sealed class AsrClient { public const string DefaultEndpoint = "http://127.0.0.1:8790/mas3d/api/asr"; public string Endpoint = DefaultEndpoint; public string Token = string.Empty; public IEnumerator Transcribe(byte[] wav, Action<AsrResult> onOk, Action<string> onError); }`：`new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)`（**不能用** `UnityWebRequest.Post(string,string)`，它会装一个 form 上传处理器把裸 WAV 弄坏）+ `UploadHandlerRaw` + `Content-Type: audio/wav` + `Authorization: Bearer`；非 2xx 或 `ok != true` 都走 onError（带 `error` 字段原文，401 的 FastAPI 体是 `detail`，两者都读）；JSON 用 `JsonUtility` + 私有 DTO，缺 `asr` 用 `asr.text == null` 判别（JsonUtility 会把嵌套类内联，永远不给 null）。
- `ScriptedHttp`：`TcpListener` 起在 127.0.0.1:0，读请求头到空行、读 `Content-Length` 指定的 body，回固定响应（状态码 + body），暴露 `Requests` 与 `Url`；`Dispose` 关闭监听。

TDD 步骤：
- [ ] `WavEncoderTests`：头字段逐个断言（RIFF/WAVE/fmt 长度 16/PCM=1/声道/采样率/byteRate/blockAlign/bits=16/data 长度）、样本往返（0, 0.5, -0.5, 1.5→clamp, -1.5→clamp）、空数组仍 44 字节；先红。
- [ ] 实现 `WavEncoder` 转绿，再补 `Trim`（超过 `seconds` 截断，短于不补）。
- [ ] `AsrClientTests`：200 正常 JSON → `AsrResult.Text` 正确且请求带 `Authorization` 与 `audio/wav`；401 用真桥接的 `{"detail": ...}` 体 → onError 带上那句提示；200 但 `ok=false` → onError 带 `error`；坏 JSON → onError 不抛；先红。
- [ ] 实现 `AsrClient` 与 `ScriptedHttp` 转绿。
- [ ] 跑 PlayMode（`MissingMonkia` 守卫不影响本 Task），基线 59 → 期望 59 + 新增条数。

## Task 4 — Unity：按住 V 说话

Files: `apps/unity/Assets/Scripts/Voice/VoiceCapture.cs`、`apps/unity/Assets/Scripts/Voice/VoiceInput.cs`（+ `.meta`）、`apps/unity/Assets/Tests/PlayMode/VoiceInputTests.cs`

Interfaces:

- `public interface IRecorder { bool Recording { get; } string Error { get; } bool Start(); float[] Stop(out int sampleRate); }`；`public sealed class MicrophoneRecorder : IRecorder`（`Microphone.devices` 为空时 `Error = "没有可用的麦克风"`，`Start()` 返回 false；频率 16000，最长 30 s，`Microphone.GetPosition` 取实际长度）。
- `public sealed class VoiceInput : MonoBehaviour { public DialoguePanel Panel; public BridgeSettings Settings = new BridgeSettings(); public KeyCode HoldKey = KeyCode.V; public IRecorder Recorder; public AsrClient Client; public Func<byte[], Action<AsrResult>, Action<string>, IEnumerator> Poster; public bool KeyboardInput = true; public void Press(); public void Release(); public bool Listening { get; } public string LastStatus { get; } public const float MinSeconds = 0.3f; public const string ListeningCopy = "正在听…（松开 V 提交）"; public const string EmptyCopy = "没听清，再说一次？"; public const string FailCopy = "语音识别没有响应，先打字吧。"; public void Bind(DialoguePanel panel, BridgeSettings settings); }`
- `Poster` 与 `Press()/Release()` 是测试接缝：`Poster` 非空时用它代替 `Client.Transcribe`（PlayMode 不能真发网络请求），`KeyboardInput == false` 时 `Update` 不读键而由测试直接驱动状态机。运行时两处默认值都不改变行为。
- `Update()`：`GetKeyDown(HoldKey)` → `Recorder.Start()`，成功则 `Panel.SetStatus(ListeningCopy)`；`GetKeyUp(HoldKey)` 且正在听 → `Stop` → 短于 `MinSeconds` 给 `EmptyCopy` → 否则 `WavEncoder.Encode` 后 `StartCoroutine(Client.Transcribe(...))`；成功且 `text` 非空 → `Panel.InputText = text`（**不**触发 `Submitted`）；空文本 → `EmptyCopy`；失败 → `FailCopy`。

TDD 步骤：
- [ ] `VoiceInputTests`：假 recorder（返回固定样本）+ 假 poster（记录是否被调用）——按住松开后输入框被写入且 `Submitted` 未触发；短录音不发请求且状态是 `EmptyCopy`；没按就松开无副作用；recorder 报错时状态为 `Error`；识别为空 → `EmptyCopy`；重复按键不重启录音；`Bind` 推导端点与 Token；未接线只警告一次；先红。
- [ ] 实现 `VoiceCapture`/`VoiceInput` 转绿；`MicrophoneRecorder` 用 `[Explicit]` 真机用例或直接不测（无麦克风的 CI 会挂）。
- [ ] 跑 PlayMode 全绿。

## Task 5 — 场景接线与文档

Files: `apps/unity/Assets/Scripts/RoomBootstrap.cs`、`apps/unity/Assets/Editor/CliPlayer.cs`、`apps/unity/Assets/Scenes/Main.unity`（重建）、`apps/unity/Assets/Tests/PlayMode/AvatarSceneTests.cs` 或新 `VoiceSceneTests.cs`、`docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`、`apps/bridge/README.md`、`apps/bridge/TECH.md`、`tools/unity.ps1`（可选 `asr` 动作）

Interfaces:

- `RoomBootstrap`：`public VoiceInput Voice;`，`Boot()` 里 `if (Voice != null) Voice.Bind(Dialogue, new BridgeSettings { Token = ReadToken() });`；令牌读取复用 `BridgeSettings.TokenFilePath`（读不到就给空串并 `Debug.LogWarning`）。
- `CliPlayer.BuildScene`：给 Player 挂 `VoiceInput` 并写好 `Panel` 引用（`DialoguePanel` 在 UI 物体上，用 `Object.FindAnyObjectByType`）。
- 场景测试：`TheSceneBindsVoiceToTheDialoguePanel`（additive 加载 + finally 卸载，断言 `RoomBootstrap.Voice != null` 且 `Voice.Panel == DialoguePanel`）。
- 文档：spec §P3 标记完成并记实现事实（openai-whisper、small/tiny、锁、8 MiB、30 s）；README 端点表加 `/mas3d/api/asr`；TECH 记一处「为什么用 openai-whisper 而不是 faster-whisper」（AstrBot 环境已装 openai-whisper + torch cu128，faster-whisper 未装，避免新增依赖）。

TDD 步骤：
- [ ] 先写场景测试（红：`Voice` 字段不存在）。
- [ ] 接 `RoomBootstrap`/`CliPlayer` 转绿；`tools/unity.ps1 player` 重建 `Main.unity`。
- [ ] 跑 PlayMode / EditMode / smoke，记录计数；更新 spec + README + TECH。

## 交付口径

- 每个 Task 结束跑：`tools/unity.ps1 test -Mode PlayMode`（Unity 侧）或 `pytest apps/bridge/tests -q`（桥接侧），并附实际计数。
- 全部 Task 完成后由 Lead 跑整套（PlayMode + EditMode + smoke + pytest），再做整分支评审与提交；提交与 push 先问用户。
- 不做的部分：web 客户端的按住说话（Unity 是当前可玩目标）、真机麦克风自动化测试、whisper 模型进仓库。
