# web 版归档（2026-11-04）

## 归档日期

2026-11-04。从这一天起，Unity 版（`apps/unity`）是唯一在维护的主线；`apps/web` 保留在仓库里、仍可运行，但不再是产品入口。

## 归档理由

- 主线只剩 Unity：画面与性能、玩家身体、双击运行的桌面形态都在 `apps/unity` 继续做，web 版没有第二条并行的需求线。
- web 版仍有价值：它是协议与观感的**对照基线**——`pnpm.cmd -r test` 里 `apps/web` 的 130 个用例、`packages/protocol` 的 51 个、`packages/motion` 的 9 个仍在 `tools/check.ps1` 的第一步跑；bridge 加字段若把老客户端弄坏，这一门会先红。
- 保留可运行，是为了 Unity 侧出问题时还有一条已知能跑的参考实现，也是将来复活时的起点。

## 仍然怎么跑

一次安装（仓库根）：

```powershell
pnpm install
pnpm assets      # 派生 apps/web/public/assets（贴图与模型，不入库）
```

前端（仓库根；`dev` 是 `apps/web/package.json` 里的 `vite`，根 `package.json` 没有 `dev` 脚本）：

```powershell
& pnpm.cmd --filter @mas3d/web dev     # http://localhost:5173
```

没有 AstrBot 时用剧本化 mock 桥接（根脚本 `mock:bridge` → `tools/mock-bridge.mjs`，剧本有 `happy|timeout|error|command`）：

```powershell
& pnpm.cmd mock:bridge --port 8787 --scenario happy   # ws://127.0.0.1:8787/mas3d/ws
$env:VITE_MAS3D_WS = "ws://localhost:8787/mas3d/ws"   # 再启前端；回真桥接就删掉这个变量
```

真桥接仍是 `ws://127.0.0.1:8790/mas3d/ws`：起 AstrBot 并加载 `apps/bridge` 插件（`tools/link-plugin.ps1`，细节见 `apps/bridge/README.md`），令牌在 `apps/bridge/data/mas3d/token`。

回归：`& pnpm.cmd -r test`；观感基线 `node e2e/smoke.mjs`（8 项）与 `node e2e/room-angles.mjs --room /assets/room/mita-room.glb --dir <输出目录>`，两条都在 `apps/web` 下跑。

## 不再做什么

- **不加功能**：web 版不接新玩法、新面板、新入口；Unity 是唯一的功能落点。
- **不修观感**：构图、曝光、材质、房间补丁一律冻结；四机位差异不当作 bug。
- **不跟进新协议字段**：`packages/protocol` 与 bridge 的纯增量可选字段（如 expression 名、手势档位提示）web 端不追，能忽略就忽略。
- 例外只有两类：**安全**（凭据泄漏、任意代码执行、越权读写）与**阻断性 bug**（起不来、`pnpm.cmd -r test` 红、mock 桥接连不上）。这两类照修，修完仍要过 `pnpm.cmd -r test`。
