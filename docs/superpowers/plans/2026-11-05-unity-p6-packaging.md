# P6 — IL2CPP 打包与启动顺序

- 依据：`docs/superpowers/specs/2026-11-01-unity-rebuild-design.md` §7「P6 打包」与 §10「验收」（第 279 行：**双击出包可跑一轮完整对话与至少一件动作**）。
- 现状：`apps/unity` 已能无头出图、跑 PlayMode/EditMode、跑 smoke；`tools/unity.ps1 build` 只是把命令转给 Unity CLI（`unity build --target StandaloneWindows64`），**从未跑过**，项目 PlayerSettings 仍是 `productName: unity`、`defaultScreenWidth: 1920`、`scriptingBackend: {}`（默认 Mono）。
- 本阶段只做「能出包、能双击、接不上时看得懂、可自检、有文档」。不改玩法、不改观感、不改协议。

## Global Constraints

- **不自行 commit / push**；每个 Task 结束只 `git add -A`，报告里列暂存清单。提交由 YG 决定。
- **Unity 单实例**：跑任何批处理前先查残留 `Unity.exe`（`Get-CimInstance Win32_Process`）；构建前不要开着编辑器。
- **PowerShell 5.1 / 纯 ASCII**：新增或修改的 `.ps1` 必须 0 个非 ASCII 字节、无 BOM、LF；不能依赖 `pwsh`。
- **判据只看子进程退出码**，不解析日志文本（与 P5 的 `tools/check.ps1` 同一条纪律）。
- **不新增第三方包**；不动 `Packages/manifest.json`。
- **不改既有测试的判据**；新增测试放 `apps/unity/Assets/Tests/{EditMode,PlayMode}`。
- 新增 C# 文件必须带 `.meta`（由 Unity 首次导入生成，或与文件一起 `git add`）。
- 运行时的中文提示沿用现有风格（C# 源码 UTF-8）；`.uxml` 里的元素名与 class 用 ASCII。
- 构建产物不进仓库：根 `.gitignore` 增加 `/build/`。

## Task 1 — IL2CPP 出包（编辑器入口 + 脚本入口）

**Files**
- Create `apps/unity/Assets/Editor/CliBuild.cs`(+meta)
- Modify `tools/unity.ps1`（`build` 臂）
- Modify `.gitignore`（根，加 `/build/`）
- Modify `apps/unity/ProjectSettings/ProjectSettings.asset`（由脚本写入后产生的差异）

**Interfaces**
- 新方法：`public static void Mas3D.Editor.CliBuild.Build()`
- 命令行：`-out <abs path>`（缺省 `<repo>/build/Mas3D.exe`）、`-dev`（可选，Development Build）
- 日志行：`[build] target=StandaloneWindows64 backend=IL2CPP result=Succeeded path=<abs> bytes=<n> elapsed=<s>`，失败时 `[build] result=Failed error=<first line>` 并 `EditorApplication.Exit(1)`
- `tools/unity.ps1 build` 的对外契约不变：`-Out`、`-Target`、`-Timeout`；内部改成走 `-executeMethod`

**Steps**
- [ ] 1. **先拿红证**：用现在的 `tools/unity.ps1 build -Out build/before.exe` 跑一次（约几分钟），记录产物**没有** `before_Data/il2cpp_data`、`ProjectSettings.asset` 里 `scriptingBackend` 仍为 `{}`——证明当前臂出的是 Mono 包且设置没被写过。
- [ ] 2. 写 `CliBuild.Build()`：
  - 解析 `-out` / `-dev`（复用 `CliCapture` 的 `ParseFloat/ParseString` 风格，自己写 `ArgValue`）；
  - 场景列表取 `EditorBuildSettings.scenes`，为空或文件缺失直接 `[build] result=Failed error=no scenes` + exit 1；
  - `PlayerSettings.productName = "Mas3D"`、`companyName = "Mas3D"`、`defaultScreenWidth = 1280`、`defaultScreenHeight = 720`、`fullScreenMode = FullScreenMode.Windowed`、`resizableWindow = true`、`runInBackground = true`；
  - `PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP)`、`PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard_2_1)`、`PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Low)`、`PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone, Il2CppCompilerConfiguration.Release)`；
  - `BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes, locationPathName = outPath, target = StandaloneWindows64, options = dev ? Development : None })`；
  - 断言产物存在、`<name>_Data/il2cpp_data` 存在，再打日志行；`EditorApplication.Exit(0/1)`。
- [ ] 3. `tools/unity.ps1` 的 `build` 臂改成：
  `Invoke-Batch -Method 'Mas3D.Editor.CliBuild.Build' -Extra @('-out', $outPath) [-Extra '-dev']`（沿用 `Invoke-Batch` 的 `-logFile` 与 `[cli]` 过滤；**不加 `-nographics`**，IL2CPP 构建需要图形设备初始化）。
- [ ] 4. 跑一次真构建：`tools/unity.ps1 build`（首次 IL2CPP 预计 10–30 分钟），记录耗时与包大小；确认 `build/Mas3D.exe` 与 `build/Mas3D_Data/il2cpp_data` 都在。
- [ ] 5. `git add -A`。报告要贴：红证（无 il2cpp_data）、绿证的 `[build]` 日志行原文、`Get-ChildItem build` 顶层清单、`ProjectSettings.asset` 的 diff 关键行。

**Verification**
- `build/Mas3D.exe` 存在且 `build/Mas3D_Data/il2cpp_data` 存在；
- `tools/unity.ps1 build` 退出码 0；
- `ProjectSettings.asset` 里能读到 `productName: Mas3D` 与 `scriptingBackend` 下的 `Standalone: 1`。

## Task 2 — 启动参数 + 「桥接没起来」的提示

**Files**
- Create `apps/unity/Assets/Scripts/Boot/BootArgs.cs`(+meta)
- Create `apps/unity/Assets/Scripts/UI/ConnectionBanner.cs`(+meta)
- Modify `apps/unity/Assets/UI/Dialogue.uxml`（加 `connection-banner` 一节）
- Modify `apps/unity/Assets/Scripts/DialogueBridge.cs`（把 `BridgeEvent` 的 Failure/Connected 转给 banner）
- Create `apps/unity/Assets/Tests/EditMode/BootArgsTests.cs`(+meta)
- Create `apps/unity/Assets/Tests/PlayMode/ConnectionBannerTests.cs`(+meta)

**Interfaces**
- `public readonly struct BootArgs { public string Host; public int Port; public string Token; public string SelfTestPath; public bool HasBridge; public static BootArgs Parse(string[] args); }`
  - 支持 `-bridge 127.0.0.1:8790`、`-bridge 8790`（只给端口）、`-token <str>`、`-selftest <path>`；未知参数忽略；端口非法时 `HasBridge=false` 并 `Debug.LogWarning`。
- `public static string ConnectionBanner.TextFor(BridgeState state, int attempt, float seconds)`（纯函数，可单测）：连接中 `正在连接 {host}:{port}…`、失败 `连不上桥接 {host}:{port}（第 {n} 次，{s:F0}s 后重试）· 先启动 AstrBot，确认 bridge 正在监听`、已连接 `已连接`。
- `ConnectionBanner` 组件挂在 `DialoguePanel` 同一 GameObject 上，`Bind(BridgeClient)` 后订阅事件；连接成功后 2 s 隐藏。

**Steps**
- [ ] 1. 先写 `BootArgsTests`（红）：5 个用例——`-bridge 127.0.0.1:8790`、`-bridge 8790`、`-bridge foo`（非法）、`-selftest C:/tmp/a b.txt`（含空格）、无参数。跑 `test -Mode EditMode` 确认编译失败即红。
- [ ] 2. 实现 `BootArgs`，`RoomBootstrap.Boot()` 开头应用：`var boot = BootArgs.Parse(Environment.GetCommandLineArgs()); if (boot.HasBridge) { settings.Host = boot.Host; settings.Port = boot.Port; } if (!string.IsNullOrEmpty(boot.Token)) settings.Token = boot.Token;` 并打 `[boot] bridge=host:port token=set|disk selftest=none|path`。
- [ ] 3. `Dialogue.uxml` 加 `<ui:VisualElement name="connection-banner" class="connection-banner">` 内含 `Label name="connection-text"`；`ConnectionBanner` 绑定与刷新（`schedule` 或 `Update` 里 0.5 s 限流）。
- [ ] 4. `DialogueBridge` 暴露 `public BridgeClient Client { get; }`（若没有）或新增 `public event Action<BridgeEvent> Event`，banner 订阅；不改协议、不改重连逻辑。
- [ ] 5. `ConnectionBannerTests`（PlayMode）：加载 Main 场景 → 取 banner → 手工调用 `TextFor(Failed, 3, 2.0f)` 断言包含 `8790`/`第 3 次`/`AstrBot`；`Bind` 一个假客户端（`BridgeClient` 不可 mock 时改测 presenter 的公开方法）后注入 failure 事件，断言 `connection-text` 文本变化。
- [ ] 6. `git add -A`；报告贴两组测试的红/绿原文。

**Verification**
- `test -Mode EditMode` 全绿（新增 BootArgs 5 例）；
- `test -Mode PlayMode` 全绿（新增 banner 用例 ≥ 2）；
- 手动：`tools/unity.ps1 run -- -bridge 127.0.0.1:1` 能看到「连不上…先启动 AstrBot」而不是黑屏。

## Task 3 — 玩家自检模式 + `player-smoke.ps1` + check 的 player 门

**Files**
- Create `apps/unity/Assets/Scripts/Boot/PlayerSelfTest.cs`(+meta)
- Create `tools/player-smoke.ps1`（纯 ASCII）
- Modify `tools/check.ps1`（新增 `player` 门 + `-SkipPlayer`）
- Modify `tools/README.md`

**Interfaces**
- 运行时开关：`-selftest <marker path>`；超时与退出码：
  - 45 s 内没连上 → 写 `[selftest] bridge timeout elapsed=<s>` → `Application.Quit(3)`
  - `你好` 发出后 30 s 没回复 → `[selftest] reply timeout` → `Quit(4)`
  - 动作 `hands` 1.5 s 内没进入/没保持 ≥ 1.0 s → `[selftest] action failed` → `Quit(5)`
  - 全过 → 写 `[selftest] bridge ok` / `[selftest] reply ok len=<n>` / `[selftest] action hands held=<f2> ok` / `[selftest] done` → `Quit(0)`
- `tools/player-smoke.ps1` 参数：`-Exe`（默认 `build/Mas3D.exe`）、`-Timeout 150`、`-Offline`（默认按 8790 是否监听自动判断）
  - exe 不存在 → 打 `player SKIP (no build/Mas3D.exe)`、退出码 2
  - 桥接没监听 → 用 `-Offline` 期望退出码 3 且 marker 里有 `bridge timeout`（证明失败提示链路真的会走）
  - 桥接在听 → 期望退出码 0 且四行 marker 齐全
- `check.ps1`：`player` 门排在 smoke 之后（第六门），exe 不存在时 SKIP 并进 `note:`；`-SkipPlayer` 可跳过。

**Steps**
- [ ] 1. 先写 `player-smoke.ps1` 的**离线红证**：此时 `PlayerSelfTest` 还不存在，exe 也还没自检开关 → 脚本报「marker 缺行」并退出 1，贴原文。
- [ ] 2. 实现 `PlayerSelfTest`：`Awake` 里读 `Environment.GetCommandLineArgs()`，无 `-selftest` 直接 `enabled=false`；`Start` 起协程等待 `DialogueBridge`/`BridgeClient` 的 `Connected`（没有就每帧查状态），之后走 `DialoguePanel.Submit` 或 `DialogueBridge.Send`（以现有公开 API 为准），等 `ServerFrame.Reply` 后调 `ActionDirector.TryEnterAction("hands")`，`Held` 采样；全程写 marker（`File.AppendAllText`）。
- [ ] 3. 把 `PlayerSelfTest` 接进 `RoomBootstrap`（`Boot()` 末尾 `if (boot.SelfTestPath != null) gameObject.AddComponent<PlayerSelfTest>()…`），并在 `CliPlayer.BuildScene` 时不需要额外对象（运行时添加）。
- [ ] 4. 重建包（Task 1 的 build），跑 `tools/player-smoke.ps1 -Offline`（桥接不启动）→ 退出码 3、marker 有 `bridge timeout`；再启动 bridge（`node`/`python` 现有 mock 或真 bridge）跑在线模式 → 退出码 0、四行 marker。
- [ ] 5. `check.ps1` 加 `player` 门；跑 `-SkipUnity -SkipSmoke` 验证门序与 summary（web、bridge、player 三门）。
- [ ] 6. `git add -A`。

**Verification**
- 离线：`player-smoke.ps1` 退出码 3 + marker 含 `bridge timeout`；
- 在线：退出码 0 + 四行 marker；
- `check.ps1 -SkipUnity -SkipSmoke` 输出里有 `player PASS`（或 SKIP，若 exe 被删）。

## Task 4 — 文档：构建、启动顺序、失败提示

**Files**
- Modify `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（新增 `### 5.22 P6 出包（2026-11-05 实测）`）
- Modify `README.md`（快速开始补 build / 双击 / 启动顺序）
- Modify `tools/README.md`（build、player-smoke、check 的 player 门）

**Steps**
- [ ] 1. spec §5.22 写：产物路径与大小、IL2CPP 后端与设置（1280×720 窗口、productName、stripping Low）、构建耗时、**启动顺序**（先 AstrBot/bridge 监听 8790，再双击 `Mas3D.exe`；顺序反了会看到「连不上…重试」，桥接起来后自动连上）、`-bridge/-token/-selftest` 三个参数、`player-smoke` 的两种模式与退出码、check 的第六门。
- [ ] 2. 根 `README.md` 快速开始改成两步：`tools/unity.ps1 build` → 双击 `build/Mas3D.exe`；写清桥接先起。
- [ ] 3. `tools/README.md`：`build` 用法与耗时、`player-smoke` 用法、`check.ps1` 的 `player` 门与 `-SkipPlayer`。
- [ ] 4. 所有数字来自实跑（Task 1/3 的报告），不许估。
- [ ] 5. `git add -A docs tools README.md`。

**Verification**
- 三处文档的命令与实际脚本参数逐字一致；spec 里的构建耗时/包大小与 Task 1 报告对得上。
