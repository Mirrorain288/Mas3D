# Mas3D 接手说明（HANDOFF）

> 给下一个接手的 AI / 人：先读这一页，再读 `AGENTS.md` 与 `docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`。
> 最后更新：2026-11-14，交接时仓库 HEAD = `90cacab`。

## 1. 这个项目是什么

Mas3D 是一个 Windows 桌面 3D 应用：Unity 6.6（URP）渲染一间卡通卧室与角色（Mita / Monika），
对话由本地 AstrBot bridge 驱动；同时有一个 web 原型（three.js）用来快速试房间与材质。
单机、不联网游戏、不做 WebGL/移动端。

三个可运行的部分：

| 目录 | 是什么 | 语言/框架 |
|---|---|---|
| `apps/unity` | 正式桌面应用（主战场） | Unity 6000.6.7f1 + URP + Cinemachine + glTFast |
| `apps/web` | 房间/材质原型与回归探针 | Vite + three.js + vitest |
| `apps/bridge` | 与 AstrBot 的桥接（LLM 对话、TTS/ASR） | TypeScript / Python 混合 |

## 2. 当前状态（一句话）

房间已经换成《帽子米塔的房间》整套平层（卧室 / 客厅 / 餐厅 / 浴室 / 开放厨房），
烘焙成 `apps/unity/Assets/_Local/room/Flat.prefab` 并接进 `Main.unity`；
玩家能出生在粉色卧室、走过门洞进客厅。**门扇还是实体**，二层楼板还没楼梯。

### 已完成

- 平层导出：`D:\Mas3D\.superpowers\export-flat2.py`（Blender 4.5.15）→ `Assets/_Local/room/mita-flat.glb`
  （428 网格 / 251,735 面 / 227 材质带贴图 / 63.8 MB）。
- 烘焙：`Assets/Editor/CliFlat.cs` → `Flat.prefab`（423 个网格、双面 MeshCollider、5 盏顶灯 + 月光、`HouseLighting` 环境光组件）。
- 接线：`RoomBootstrap.FlatRoom / FlatSpawn(-3.15, 0.10, 0.20) / FlatYaw 90`；`-flat 1` 进平层，默认仍是旧卧室。
- 备选资产：`Assets/_Local/room/House.prefab`（无门.fbx 的写实公寓，已布光，风格不搭，留作参考）。
- 旧卧室：`Assets/_Local/room/mita-room.glb` + `RoomDress.cs`（平顶/材质/夜间布光）。

### 未完成（按优先级）

1. **门交互**：三扇门是实体网格，撞上就停；需要开/关或直接开口。
2. **二层**：客厅上方有 15 m² 楼板（y=1.77），源资产没有楼梯；补楼梯或封掉。
3. **窗外星空卡**：从室外看是一大块斜插的面片，室内正确；外部取景需裁。
4. **锚点重算**：`docs/.../2026-11-01-unity-rebuild-design.md` §5.17 的 A1–A6/座位槽是旧卧室坐标，平层要重算。
5. **web 侧**仍用旧卧室（`apps/web`），是否同步未定。
6. `apps/unity` 的改动**尚未提交**（用户明确要求提交前先问）。

## 3. 环境（本机）

| 工具 | 路径 / 版本 |
|---|---|
| Unity 编辑器 | `D:\Unity_Editor\6000.6.7f1\Editor\Unity.exe`（6000.6.7f1） |
| Unity CLI | `C:\Users\Rain zhou\AppData\Local\Unity\bin\unity.exe`（1.0.0-beta.13，**不在 PATH**） |
| Blender | `D:\tools\blender-4.5.15-windows-x64\blender.exe` |
| Node | v24.20.0（`sharp` 装在 `D:\Mas3D\node_modules`） |
| PowerShell | 只有 Windows PowerShell 5.1，**没有 `pwsh`** |

## 4. 怎么跑（照抄即可）

```powershell
cd D:\Mas3D

# 六门总检（web 断言 / bridge / Unity EditMode / PlayMode / smoke / 打包体自检）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check.ps1

# 单跑
cd apps\web; pnpm exec tsc --noEmit; pnpm exec vitest run; cd ../..
powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity.ps1 test -Mode EditMode
powershell -NoProfile -ExecutionPolicy Bypass -File tools\unity.ps1 test -Mode PlayMode
node apps\web\e2e\smoke.mjs
powershell -NoProfile -ExecutionPolicy Bypass -File tools\player-smoke.ps1

# 平层：烘焙 / 出图 / 接进场景
powershell -NoProfile -ExecutionPolicy Bypass -File tools\_bake.ps1 -Method Mas3D.Editor.CliFlat.Bake  -Log flat-bake
powershell -NoProfile -ExecutionPolicy Bypass -File tools\_shot.ps1 -Method Mas3D.Editor.CliFlat.Shot  -Log flat-shot
powershell -NoProfile -ExecutionPolicy Bypass -File tools\_bake.ps1 -Method Mas3D.Editor.CliFlat.Wire  -Log flat-wire
```

## 5. 踩过的坑（别重复踩）

1. **PowerShell 5.1**：`.ps1` 必须纯 ASCII（中文注释会让解析器报「switch 语句缺少语句块」）；`$ErrorActionPreference` 用 `Continue`，Unity 往 stderr 写噪声会变成终止错误。
2. `unity run` 自带 `-batchmode/-quit`，再传会冲突；转发参数必须 `-executeMethod` 打头。
3. **无头渲染不能加 `-nographics`**（要 GPU）；截图的 Render() 要连调两次，否则第一帧环境光/SH 是旧的。
4. 命令行传向量不要加引号：`-cam 0,1.5,0`；加了引号会被当字面字符串。
5. Unity 6.6：`Object.GetInstanceID()` 已是编译错误（error CS0619），用名字做 key。
6. **glTFast 会重写材质名**：多网格会取到别人的贴图；`RoomMaterials.Rebuild` 用 `renderer.name` 查 `glb-materials.json` 才是权威。
7. MMD 材质（`mmd_shader`/`mmd_tex_uv`）glTF 导出会变成 metallic=1 无贴图：必须沿 `Base Tex` 递归回溯转 Principled，**不要接 Alpha**（会整件消失）。
8. GLB 里的 WebP：Unity 不认，先跑 `node tools/glb-webp-to-png.mjs`。
9. 平层天花板只有 **1.77 m**：灯放 1.40 m，放 2.55 m 全在天花板上方，整片发黑。
10. 归一化房间不能取包围盒 `min.y`：窗外星空片挂得更低；取「最大水平面」当楼层基准（`CliFlat.DominantFloorY`）。
11. 场景里的 prefab 实例不要靠 `transform.position = zero` 归位——偏移要加在子物体上，否则重存后坐标全错。

## 6. 约定

- **不要自己 `git commit` / `git push`**，提交前先问用户。
- `openspec/` 是唯一需求与验收依据；改行为先改 `openspec/changes/<id>/`。
- 改动前跑 `openspec validate`；完成前给出真实验证结果（测试日志、截图像素差、构建退出码），不要只说「应该可以」。
- 提交信息用 `feat(scope): …` / `fix(scope): …` / `docs(scope): …`，一次提交一件事。

## 7. 不要塞进上下文的目录

| 目录 | 体积 | 说明 |
|---|---|---|
| `.superpowers/text-encoders`、`llama3` | 各 15 GB | 本地模型权重，已在 `.gitignore` |
| `.superpowers/kimodo-venv`、`wheels` | 7.5 GB | Python 环境 |
| `apps/unity/Library` | ~7 GB | Unity 缓存，可删可重建 |
| `素材/` | 0.93 GB | 原始 .blend/.fbx，不入库 |

接手时让 IDE 只索引 `apps/`、`tools/`、`docs/`、`openspec/`、`packages/` 和根目录的 md 文件即可。

## 8. 关键文件速查

- 房间烘焙：`apps/unity/Assets/Editor/CliFlat.cs`、`CliHouse.cs`、`CliLamp.cs`、`HouseLook.cs`、`RoomLayout.cs`
- 运行时：`apps/unity/Assets/Scripts/RoomBootstrap.cs`、`RoomColliders.cs`、`HouseLighting.cs`
- 导出脚本：`.superpowers/export-flat2.py`、`export-bedroom5.py`、`tools/glb-webp-to-png.mjs`、`tools/pbr-for-unity.mjs`
- 设计文档：`docs/superpowers/specs/2026-11-01-unity-rebuild-design.md`（§5.17 锚点、§5.29 动作三段式）
- 截图：`apps/unity/Shots/`（`flat-player.png` 是平层出生帧，`flat-wide.png` 是剖切总览）
