# tools/

本目录只放**构建期脚本**，不参与前端打包。运行环境：Node ≥ 22（见根 `package.json` 的 `engines`）。

## 资产管线

```bash
pnpm assets        # 从 素材/ 派生到 apps/web/public/assets/，并把 bytes/sha256 回写清单
pnpm assets:check  # 只校验：每个 target 是否存在、字节数与 sha256 是否与清单一致
```

- `素材/` 是本地原始素材（**不入库**，见根 `.gitignore`）。
- `apps/web/public/assets/` 是派生结果（**不入库**，唯一例外是 `monika/Monkia.vrm` 与 `monika/Monika.vrm` 两副 VRM，它们都获再分发许可，见下）。
- `assets.manifest.json` 是唯一事实来源：每条 `{ source, target, kind, transform }`，生成后追加 `bytes`/`sha256`。
- `--check` 不写任何文件，不符合时打印 `missing: <target>` / `size: …` / `sha256: …` 并以退出码 1 结束，CI 与验收都跑它。

### transform 取值

| transform | 行为 |
| --- | --- |
| `copy` | 原样复制（GLB / VRM） |
| `resize:<px>` | `sharp` 等比缩到宽 `<px>`，输出 JPEG q85（mozjpeg） |
| `webp:<px>` | `sharp` 等比缩到宽 `<px>`，输出 WebP q82（PBR 贴图） |

### 清单里有什么

| kind | 条目 | 来源 |
| --- | --- | --- |
| `hdri` | `hdri/night-sky-2k.jpg`、`hdri/night-sky-1k.jpg` | `NightSkyHDRI008_8K_TONEMAPPED.jpg` |
| `pbr` | `pbr/{clay,wood-floor,ceiling}-{color,normal,roughness}@1k.webp` | `Clay002_4K-JPG`、`WoodFloor070_4K-JPG`、`OfficeCeiling001_2K-JPG` |
| `glb` | `classroom/{desk,chair,blackboard,light}.glb` | `StylooClassroomAssetPack …/classroom/GLTF/{desk,chair,blackboardbig,lamp}.glb` |
| `vrm` | `monika/Monkia.vrm`（默认，`tracked: true`） | `素材/3705117291637205523.vrm`（17,143,268 字节） |
| `vrm` | `monika/Monika.vrm`（回退，`tracked: true`） | `素材/莫妮卡.vrm`（23,081,108 字节） |

## Unity 冒烟（smoke）

```powershell
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 smoke
```

两次 Unity 批处理串行：先桥接（`BridgeSmokeTests.OneTurnAgainstTheRealMockBridge`，真跑一轮对话，marker 落 `apps/unity/Logs/smoke-marker.txt`，里面要有 `[smoke] turn ok`），再动作（`ActionSmokeTests.EveryActionPlaysOnceThroughTheScene`，五件动作各演 1.5 s，marker 落 `smoke-actions-marker.txt`，五个 id 各一行）。整条约 3 分钟；跑之前别开着编辑器（Unity 单实例）。

判据不只看退出码：Unity CLI 对不存在的 `--filter` 会给 exit 0 + XML `total="0"`（「空跑通过」），所以脚本另外查 XML 有没有 Failed、`total` 是不是 0、marker 要求的行是否齐全，任一不满足就 `exit 1`。

动作冒烟只证明「五件动作都能从场景入口进、都能干净退出」，不判几何精度——掌心残差、`Held` 时长、腿骨下限那些在 `Action*Tests` 里。要六步全回归（web / bridge / unity-edit / unity-play / smoke / player）用 `tools/check.ps1`，见下节。

⚠️ `tools/unity.ps1 player` 会**重建** `apps/unity/Assets/Scenes/Main.unity`（整篇重序列化：diff 会很大，fileID 可能重排）。只有改了场景接线才需要它；日常验证走 `test` / `smoke` / `check.ps1`，跑完 `player` 要看一眼 diff 再决定是否提交。

`tools/unity.ps1` 与 `tools/check.ps1` 内部一律调 `pnpm.cmd`：`pnpm.ps1` 会被执行策略拦下（命令静默、退出码为空）。

## 打包（build）

```powershell
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 build                                 # 默认 build/Mas3D.exe
powershell -ExecutionPolicy Bypass -File tools/unity.ps1 build -Out build/Dev/Mas3D.exe -dev   # 换输出位置、出开发版
```

- 后端是 **IL2CPP**（x64 / Release / managed stripping Low），窗口 1280×720，productName 与 companyName 都是 `Mas3D`。这些由 `apps/unity/Assets/Editor/CliBuild.cs` 在构建时写进 PlayerSettings，不靠手改 `ProjectSettings.asset`。
- 耗时：首建墙钟 **3 分 54 秒**（CliBuild 自记 `elapsed=184.7`，编辑器启动与脚本重编译约占 50 s），不动脚本的复跑 `elapsed=11.5`。产物 **259.4 MB / 28 个文件**，其中 `Mas3D.exe` 667,136 B、`Mas3D_Data/` 186,630,142 B。
- `-Target` 只接受 `StandaloneWindows64`（别的值直接 throw，构建设置是 Windows-only）；`-Timeout` 管这一批 Unity 的超时；`-dev` 从剩余参数透传开发版；`-Out` 的相对路径按仓库根解析。
- 退出码与产物两道门：脚本跑完自己 `Test-Path` exe 与 `<name>_Data/il2cpp_data`，缺任一条打印 `build arm: player artifacts missing: …` 并 `exit 1`。编辑器说成功不算数，只有这两个东西在盘上才算。
- 换机器先装 IL2CPP 模块，否则只报 `Error building Player: Currently selected scripting backend (IL2CPP) is not installed.`。装它：`unity install-modules -e 6000.6.7f1 -m windows-il2cpp --accept-eula -y --no-elevate --retries 2`（本机实测下载 258.93 MB、落地 1.04 GB；机器级改动，不在仓库里）。
- 同一个输出目录别跨后端复用：里面放过 Mono 包，再切 IL2CPP 会 `Build path contains a project previously built with the Mono2x scripting backend`。脚本不自动清目录（怕误删），换后端请换目录或手工清空。
- 启动顺序与令牌：先起 AstrBot（插件监听 `127.0.0.1:8790`），再开 exe；反了会在提示条上看到「连不上桥接 … 先启动 AstrBot」，后台退避重连、桥接起来自动连上。令牌按 `<仓库>/apps/bridge/data/mas3d/token`（exe 在仓库 `build/` 里时就是这份，仓库根 = exe 所在目录的上一级）→ `<exe 目录>/mas3d-token` → `<persistentDataPath>/token` 取第一个非空，所以 `build/Mas3D.exe` 双击就能连；`-token` 仍可覆盖。细节见根 README 快速开始与 spec §5.22。
- 玩家身体截图入口：`-executeMethod Mas3D.Editor.CliBody.Capture`（`apps/unity/Assets/Editor/CliBody.cs`），产物落 `apps/unity/Shots/`；`test` / `build` 的用法没变。

## 玩家包自检（player-smoke）

```powershell
powershell -ExecutionPolicy Bypass -File tools/player-smoke.ps1            # 在线：脚本自起 mock 桥接
powershell -ExecutionPolicy Bypass -File tools/player-smoke.ps1 -Offline   # 离线：只验「连不上要报超时」
```

跑的是**打包产物** `build/Mas3D.exe`（先 `tools/unity.ps1 build`），不是编辑器。脚本自己从 8791 起探一个空闲端口，自己起 `node tools/mock-bridge.mjs --port <端口> --scenario happy`，再用 `-bridge 127.0.0.1:<端口> -selftest <marker>` 启 exe——**默认不传 `-token`**，走的就是双击那条令牌发现路径；判据是退出码 0 加五行 marker（`token src=<路径> len=64` / `bridge ok` / `reply ok len=<n>` / `action hands held=<x.xx> ok` / `done`）。`-WithToken` 额外覆盖一次 `-token` 分支，`-Reply "嗯。"` 用短回复证明「回没回」不看面板长度。结束只杀自己起的两个进程（8790 上跑着的真 AstrBot 不碰）。

判据是 **exe 的退出码 + marker 文件**（`apps/unity/Logs/player-marker.log`，每行 `yyyy-MM-dd HH:mm:ss.fff [selftest] …`）：

| 情形 | exe 退出码 | marker 里必须有 |
| --- | --- | --- |
| 全过 | `0` | `token src=<路径> len=64` / `bridge ok` / `reply ok len=<n>` / `action hands held=<x.xx> ok` / `done`（五行） |
| 45 s 没连上桥接 | `3` | `bridge timeout elapsed=<s>` |
| 30 s 没等到回复 | `4` | `reply timeout …` |
| 动作 1.5 s 内没进、或没保持 1 s | `5` | `action failed …` |

脚本输出一行 `player PASS|FAIL (online|offline) exit=<n> <秒>s`，失败时点名缺哪几行 marker 并打印 marker 原文；缺 exe 或缺 node 时打 `player SKIP (...)` 并以 `2` 退出（`check.ps1` 把 `2` 当 SKIP 记 `note:`，不算通过）。另外两份日志：`player-run.log`（exe 自己的日志）、`player-mock.log`（mock 桥接的）。`-Exe` 可换成别的产物、`-Timeout`（默认 150 秒）改等它退出的上限。

`-Offline` 故意不给桥接：断言的是「连不上也要在 45 s 内自己退出来，别把全回归拖死」，所以离线跑的期望是 `exit 3`。

两条与退出码有关的实测事实（改脚本时别踩）：① 打包 player 里 `Application.Quit(code)` 之后进程根本不会退出（连 `-quit` 单独跑 45 s 仍活着），单独的 `Environment.Exit(code)` 也不退出；`PlayerSelfTest` 因此改走 kernel32 `ExitProcess(code)`，这是唯一能把 3/4/5 原样交给 OS 的路。② `Start-Process -PassThru` 返回的 Process 在本机 shell 里 `ExitCode` 读回 `$null`（对照组 `cmd /c exit 7` 也是空），而 `[int]$null` 等于 0，会把任何码读成 0；脚本改用 .NET `Process.Start(UseShellExecute=$false)` 起 exe，退出码才是真值。

## 一条命令全回归

```powershell
powershell -ExecutionPolicy Bypass -File tools/check.ps1
```

六道门按顺序串跑（web → bridge → unity-edit → unity-play → smoke → player），每道门**只看子进程退出码**，不解析日志文本——日志只留给人看，判红永远来自子进程的退出码。

| 步骤 | 跑什么 | 日志 |
| --- | --- | --- |
| `web` | `pnpm.cmd -r test` | `apps/unity/Logs/check-web.log` |
| `bridge` | `<python> -m pytest apps/bridge/tests -p no:cacheprovider --tb=no -q` | `check-bridge.log` |
| `unity-edit` | `tools/unity.ps1 test -Mode EditMode` | `check-edit.log` |
| `unity-play` | `tools/unity.ps1 test -Mode PlayMode` | `check-play.log` |
| `smoke` | `tools/unity.ps1 smoke` | `check-smoke.log` |
| `player` | `tools/player-smoke.ps1`（打包 exe + 自起 mock 桥接） | `check-player.log` |

每步打一行 `<step> <PASS|FAIL|SKIP> exit=<n> <秒>s`，末尾一行 `summary: <passed>/<ran> ok`；任一步失败则整体 `exit 1`，并在 `summary` 之前用 `failed: …` 点名。`SKIP` 既不算通过也不进分母：有跳过时先打 `note: skipped …`，`summary` 变成 `summary: <passed>/<ran> ok (<n> skipped)`。**有失败时 `failed: …` 打在最前**、`summary` 始终是最后一行（按末行取状态的包装不会读错）。缺 `pnpm.cmd` 时 web **判 FAIL 而不是跳过**——必跑的门口不许静默变绿。

参数：

- `-SkipWeb` / `-SkipBridge` / `-SkipUnity` / `-SkipSmoke` / `-SkipPlayer`：跳过对应步骤；`-SkipUnity` 只跳两条 Unity 测试，smoke 与 player 各自有开关。
- `-Python <path>`：带 pytest 的解释器。查找顺序 `-Python` → `$env:MAS3D_PYTHON` → PATH 里的 `python`，每个候选用 `-c "import sys"` 实跑验证后才会被信任（Windows 应用商店的 `python.exe` 别名存在但以 9009 静默失败，只看命令存不存在会踩空）。都不可用时打印 `bridge SKIP (no python; set MAS3D_PYTHON)`，**不计失败**。
- `-Timeout <秒>`：透传给 `tools/unity.ps1 -Timeout`，管每一段 Unity 批处理。

注意事项：

- **Unity 单实例**：跑之前别开着编辑器。撞到工程锁时 Unity 段以非 0 退出码 FAIL；判红从不来自 `TestResults/*.xml`，所以撞锁只会变红，不会读到上一轮的旧结果假装全绿。
- 完整跑一遍约 9–12 分钟，Unity 三段占大头，player 门再加十几秒；web 与 bridge 各十几秒。
- `player` 门没有 `build/Mas3D.exe` 时打 `player SKIP (no build/Mas3D.exe)` 并进 `note:`——不伪装成通过，也不让没打包的一次全回归变红。
- `pnpm` 必须走 `pnpm.cmd`（`pnpm.ps1` 会被执行策略拦，表现为命令静默、退出码为空），脚本里由 `cmd /c` 转一层，因为 `Start-Process` 没法直接重定向 `.cmd` 的输出。
- `check-*.log` 是 `Start-Process -RedirectStandardOutput` 直接接到子进程管道后写出的**原始字节**（本机按控制台代码页，中文可能显示成乱码）；PowerShell 自己的 `*>` 才写 UTF-16、才需要 `-Encoding Unicode`。两种不要混：判定从不读日志，日志只供人看。

## 观感验收：一条命令出数与比对

```
powershell -ExecutionPolicy Bypass -File tools\look-shoot.ps1
```

1. `unity.ps1 player` 重建可玩场景（材质、灯、描边、眼睛、家具 casting 的数值在这一步写进 `Main.unity`）；
2. `unity.ps1 capture -noShadows 1 -hideplayer 1` 出交付图（`play-01..04`、`avatar-seated`、`face`、`eyes`、`foot-01..08`）加两对底片：`no-shadow-01/03`（带玩家）与 `room-shadow-01`、`room-noshadow-01`（同机位隐藏玩家，只留房间自己的影子）；
3. 跑 `look-compare.mjs`（十项：脸颊 B/R、脸峰值、房间对比度、描边像素、脚下亮度比，加虹膜饱和度/色相/高光/外圈暗带）与 `shadow-diff.mjs`（两对图的下半幅阴影差 + 配对守卫），退出码 = 失败项数。

单独跑其中一件：

```
node tools/look-compare.mjs --mine apps/unity/Shots --ref docs/superpowers/specs/assets/2026-11-06-reference.png --report .superpowers/look/2026-11-07/metrics.json
node tools/shadow-diff.mjs --min-lower 0.03 --min-peak 0 apps/unity/Shots/play-01.png apps/unity/Shots/no-shadow-01.png
node tools/shadow-diff.mjs --min-lower=0.02 --min-peak 25 apps/unity/Shots/room-shadow-01.png apps/unity/Shots/room-noshadow-01.png
```

`shadow-diff.mjs` 除了「变暗像素占比」，还查「参照里**变亮** ≥2% 的像素占比」（上限 0.5%）——阴影只会拿光不会给光，亮回去就说明两张图不是同一个场景（C1 那次 `no-shadow-01` 与 `room-noshadow-01` 逐字节相同，判定因此恒绿）。`--min-lower` / `--min-peak` 支持空格与等号两种写法，缺值或非数值直接 exit 2，不静默回落。

眼睛贴图不在仓库里（`apps/unity/Assets/_Local/avatars/eyes/iris.png` 被 gitignore），用 `node tools/eyes-recolor.mjs` 从 VRM 原图重生；改色相/亮度看它的 `PALETTE`。

三条口径是量出来定的，改判据前先读 `docs/superpowers/plans/2026-11-06-rendering-alignment.md` 的「Task 6 实测偏差」：脸峰值取 99.5 分位（不是 max）、描边取暗带长度的 75 分位（不是 max）、阴影差的分母取下半幅（影子只能落在照得到直射光的地方）。脚下光斑不做像素门，改由 `RoomLightTests` 从灯光配置上钉。

`look-compare.mjs` 依赖 `sharp`（仓库根 `node_modules` 已有）。`player` / `capture` 接受的额外开关会原样转发给编辑器，例如 `capture -noShadows 1`、`capture -hideplayer 1`。

## 为什么天空盒不是 EXR

原始文件 `NightSkyHDRI008_8K_HDR.exr` 是 **112,336,889 字节** 的 8K OpenEXR。它能被 three 的 `EXRLoader` 读取，但：

1. 112 MB 无法随前端发布，也不该进仓库；
2. 我们的场景是「太空教室」——天空只提供背景与一点点环境光，不需要 HDR 高光保真；
3. `sharp` 读不了 EXR（预编译 libvips 未启用 OpenEXR），要用 `oiiotool` 才能降采样，等于给构建加一个系统级依赖。

所以清单只用同目录的 `NightSkyHDRI008_8K_TONEMAPPED.jpg`（8K，已 tonemap）派生出 2K/1K JPEG：2K 给天空盒背景，1K 给低配或远处。**如果以后确实需要 HDR 高光**（例如做金属反射），把 `oiiotool` 装进开发机，补两条 `transform: "exr:<px>"` 的条目即可，前端 `assets.ts` 会按 manifest 找文件，不需要改场景代码。

## 许可

- `Monkia.vrm`（默认）：作者 **LilowLeeLani**，title `Monkia`，许可证 `Other`（hub.vroid.com）：再分发 allow、修改 allow、**署名 required**、企业商用与个人商用 disallow、性表现 disallow。页脚署名由模型 meta 派生（`apps/web/src/avatar/modelCredit.ts`），不写死作者。
- `Monika.vrm`（回退）：作者 **tyler1234506**，reference `DDLC blazer`，许可证 `Other`（hub.vroid.com），条款与上面同一档。用 `VITE_VRM_URL=/assets/monika/Monika.vrm` 切回。改 FACE 形变只改运行时，不重写文件。
- Styloo 教室包：随包 README 声明，仅用于非商用作品；本项目只取 4 个 GLB，不打包其原始贴图集以外内容。
- Poly Haven 材质与 HDRI：CC0。
