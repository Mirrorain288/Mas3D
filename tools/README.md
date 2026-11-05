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

动作冒烟只证明「五件动作都能从场景入口进、都能干净退出」，不判几何精度——掌心残差、`Held` 时长、腿骨下限那些在 `Action*Tests` 里。要五步全回归（web / bridge / unity-edit / unity-play / smoke）用 `tools/check.ps1`，见下节。

⚠️ `tools/unity.ps1 player` 会**重建** `apps/unity/Assets/Scenes/Main.unity`（整篇重序列化：diff 会很大，fileID 可能重排）。只有改了场景接线才需要它；日常验证走 `test` / `smoke` / `check.ps1`，跑完 `player` 要看一眼 diff 再决定是否提交。

`tools/unity.ps1` 与 `tools/check.ps1` 内部一律调 `pnpm.cmd`：`pnpm.ps1` 会被执行策略拦下（命令静默、退出码为空）。

## 一条命令全回归

```powershell
powershell -ExecutionPolicy Bypass -File tools/check.ps1
```

五道门按顺序串跑（web → bridge → unity-edit → unity-play → smoke），每道门**只看子进程退出码**，不解析日志文本——日志只留给人看，判红永远来自子进程的退出码。

| 步骤 | 跑什么 | 日志 |
| --- | --- | --- |
| `web` | `pnpm.cmd -r test` | `apps/unity/Logs/check-web.log` |
| `bridge` | `<python> -m pytest apps/bridge/tests -p no:cacheprovider --tb=no -q` | `check-bridge.log` |
| `unity-edit` | `tools/unity.ps1 test -Mode EditMode` | `check-edit.log` |
| `unity-play` | `tools/unity.ps1 test -Mode PlayMode` | `check-play.log` |
| `smoke` | `tools/unity.ps1 smoke` | `check-smoke.log` |

每步打一行 `<step> <PASS|FAIL|SKIP> exit=<n> <秒>s`，末尾一行 `summary: <passed>/<ran> ok`；任一步失败则整体 `exit 1`，并在 `summary` 之前用 `failed: …` 点名。`SKIP` 既不算通过也不进分母：有跳过时先打 `note: skipped …`，`summary` 变成 `summary: <passed>/<ran> ok (<n> skipped)`。**有失败时 `failed: …` 打在最前**、`summary` 始终是最后一行（按末行取状态的包装不会读错）。缺 `pnpm.cmd` 时 web **判 FAIL 而不是跳过**——必跑的门口不许静默变绿。

参数：

- `-SkipWeb` / `-SkipBridge` / `-SkipUnity` / `-SkipSmoke`：跳过对应步骤；`-SkipUnity` 只跳两条 Unity 测试，smoke 有自己的开关。
- `-Python <path>`：带 pytest 的解释器。查找顺序 `-Python` → `$env:MAS3D_PYTHON` → PATH 里的 `python`，每个候选用 `-c "import sys"` 实跑验证后才会被信任（Windows 应用商店的 `python.exe` 别名存在但以 9009 静默失败，只看命令存不存在会踩空）。都不可用时打印 `bridge SKIP (no python; set MAS3D_PYTHON)`，**不计失败**。
- `-Timeout <秒>`：透传给 `tools/unity.ps1 -Timeout`，管每一段 Unity 批处理。

注意事项：

- **Unity 单实例**：跑之前别开着编辑器。撞到工程锁时 Unity 段以非 0 退出码 FAIL；判红从不来自 `TestResults/*.xml`，所以撞锁只会变红，不会读到上一轮的旧结果假装全绿。
- 完整跑一遍约 9 分钟，Unity 三段占大头；web 与 bridge 各十几秒。
- `pnpm` 必须走 `pnpm.cmd`（`pnpm.ps1` 会被执行策略拦，表现为命令静默、退出码为空），脚本里由 `cmd /c` 转一层，因为 `Start-Process` 没法直接重定向 `.cmd` 的输出。
- `check-*.log` 是 `Start-Process -RedirectStandardOutput` 直接接到子进程管道后写出的**原始字节**（本机按控制台代码页，中文可能显示成乱码）；PowerShell 自己的 `*>` 才写 UTF-16、才需要 `-Encoding Unicode`。两种不要混：判定从不读日志，日志只供人看。

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
