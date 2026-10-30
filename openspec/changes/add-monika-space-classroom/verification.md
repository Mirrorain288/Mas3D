# 验收记录 — add-monika-space-classroom

- 日期：2026-10-31（CST）
- 环境：Windows 11；AstrBot 4.28.2 桌面版（19 个插件，failed=0）；桥接插件 0.1.0 端点 `127.0.0.1:8790`；
  GPT-SoVITS api_v2 `127.0.0.1:9880`（权重 Moniar_v2_1-e15.ckpt + Moniar_v2_1_e12_s984.pth）；HAST-SEC v0.1.1 `127.0.0.1:8769`（device CUDA0）；
  浏览器 Edge + swiftshader 1280×800；会话 umo `Monika:FriendMessage:2441388327`。
- 本文只记观察到的事实（命令输出、DB 行、日志行、截图），不写断言式结论。

## 1. 自动化测试

| 项 | 命令 | 结果 |
| --- | --- | --- |
| 桥接 | `.venv\Scripts\python.exe -m pytest apps/bridge/tests -q` | 86 个用例通过，退出码 0 |
| 前端与协议 | `pnpm -r test` | protocol 6 + motion 9 + web 77 = 92 passed |
| 构建 | `pnpm --filter @mas3d/web build` | tsc --noEmit 干净；dist/assets/index-DHfAV6wE.js 1,029.18 kB（gzip 274.80 kB），仅 chunk>500 kB 提示 |
| 冒烟 | `pnpm --filter @mas3d/web e2e` | 8 checks ok，退出码 0（mock 桥接 8787） |

冒烟断言明细：mock 桥接启动 / 场景挂载 / 角色与表情驱动就绪 / 桥接在线 / 收到回复且剥离场景前缀 /
Fcl_ALL_Joy=0.680 / 断开桥接出现 `.chat-offline` / 无未捕获异常。

## 2. VRM 表情与视线

- 表情：一次 mock 回合后 `Fcl_ALL_Joy=0.680`、`Fcl_EYE_Joy=0.425`、`Fcl_MTH_Joy=0.34`
  （face.map.json 索引 3 / 17 / 33），说话期间嘴部被压到 0.085（0.34×0.25）。
- 视线：相机横拖 300 px 后 `leftEye.rotation.y` 0.006 → 0.336、`head.rotation.y` 0.012 → 0.211。
- 待机：30 s 内眨眼 7 次（≈14 次/分钟，配置区间 10–24）；`upperChest.rotation.x` ±0.0119 按 4 s 起伏；
  `head.rotation.y` ±0.010 按 7 s 起伏。
- 落座：hips y=0.52、脚 y≈-0.004（4 mm 误差），座位锚点由坐面顶点统计得出（seat.glb y=0.38 平面）。
- 截图：`.superpowers/shots/task7h.png`（正视）、`task7i.png`（侧视）。

## 3. 语音链路（真实 AstrBot + 真实 GPT-SoVITS）

- 页面发「我一直没睡，是因为有点害怕，你能陪我说说话吗？」→ 回复 27.7 s，`voiceCalls=[200]`，
  `seenSpeaking=true`、`maxMouth=0.90`、无 pageerror / console error；
  同一链路在跳过 AstrBot 自带 TTS 之前是 81.4 s。
- 端点直测：`POST /mas3d/api/voice/synth` 文本「嘿嘿，好呀🐷，我们走！」→ 200，
  `id=d889c72c12209066ec77d4a69094cb3e`、`durationMs=3140`、`englishText="Hehe, sure, let's go!"`（emoji 已剔除、中文已转英文）；
  `GET /mas3d/api/voice/<id>.wav` → 200，201,004 字节，头部 `RIFF`。
- 口型包络（Task 15 记录）：0 → 0.65 → 0.316 → 0.5 → 0 → 0.75 → 0.986 → 0.678 → 0.651，句间 4 帧归零，说完 1.2 s 回待机。
- 字幕中文、音频英文（`englishText` 与主回复文本不同）。截图 `.superpowers/shots/real-chain2.png`。

## 4. HAST-SEC 在线与离线

- 在线：`POST /mas3d/api/motion/plan` → 200，plan 为 `operation=command`、`intents=[right-挥手打招呼, hold=2s, delay=0]`、`reply=""`。
  页面 `rightUpperArm.rotation.z` 基准 -1.16 → 峰值 -0.247（t≈856 ms）→ 4319 ms 精确回基准；`rightLowerArm.z` 摆到 ±0.367。
- 离线（停掉 8769 后）：端点 503 `{"ok":false,"code":"bridge_down","message":"HAST-SEC 正在冷却"}`；
  页面手臂保持待机（y/z 0.18 / -1.16），对话仍可完成。
- `operation=unsupported` 与无法解析的 key（如 `后空翻`）：手臂姿态不变，无异常。
- 截图：`.superpowers/shots/task13-arm.png`（抬手）、`task13-idle.png`（回到待机）。

## 5. QQ 副作用

- 共用会话：`conversations` 最新行 `conversation_id=5d281073-b398-4c2e-aa70-ca3cfc6b445f`，
  content 长度 1,386,776 → 1,395,784 → 1,404,050 → 1,405,037 字符，turns 352；
  user 消息带「（3D教室）」前缀 + system_reminder，assistant 为完整回复。
- 未向 QQ 投递：`platform_message_history` 无新增行（仍只有 8 条 2026-10-21 的 webchat 记录）。
- 逐字一致：WS `reply` 文本与 DB 最后一条 assistant 完全一致（三段，137 字符）。
- 分段插件：修复后 backend.log 不再出现 `[Splitter]` 分段/发送失败（现存 1,058 条全部早于修复时刻 01:39:12）。
- AstrBot 自带 TTS：游戏回合不再触发 `TTS request`（修复前每回合 4 段、每段 3 次 400 失败，
  原因是其英文译文含 emoji，触发 `'gbk' codec can't encode character`）。

## 6. 断线恢复

- 杀掉 mock 桥接：4.2 s 后 `.chat-offline` 横幅出现；重连后横幅消失。
- `--scenario timeout`：8 s 后进入兜底台词「（她侧过头，像是在等信号回来）……」并保留错误状态与重试入口。
- `--scenario error`：错误卡 + 重试按钮；重试重发最后一条输入。
- 刷新页面：从 `localStorage['mas3d.transcript']` 恢复最近 ≤50 回合（实测 3 条，含 backend 灰底消息）。

## 7. 已知遗留

1. 前端单包 1.03 MB 未拆包（vite 提示 chunk>500 kB）。
2. AstrBot 必须用桌面版启动器 `astrbot-desktop-tauri.exe` 启动；直接跑 `launch_backend.py` 会解析到错误的 data 目录。
3. GSV 的 `tts_infer.yaml / weight.json / api-start.bat` 三处权重不一致，因此每次合成前显式锁权重。
4. 语音（9880）与动作（8769）后端需手工先起；未起时分别降级为「仅字幕」和 T0/T1。
5. 游戏回合跳过 AstrBot 自带 TTS（语音由本地合成负责），QQ 侧行为不变。
6. 真实链路脚本 `pnpm --filter @mas3d/web e2e:real` 需要 AstrBot、桥接插件与 GPT-SoVITS 同时在线。
