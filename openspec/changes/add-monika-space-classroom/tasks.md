# Tasks

Design decisions referenced as D1–D14 are in `design.md`; requirements in `specs/`.

## 1. 仓库骨架与工程基建

- [ ] 1.1 建立 `apps/web`、`apps/bridge`、`packages/protocol`、`packages/motion`、`tools` 目录与根 `package.json`（pnpm workspaces），验证 `pnpm -r exec node -e "console.log(1)"` 在四个 workspace 全部返回 1
- [ ] 1.2 配置 `packages/protocol`：`say/ack/thinking/reply/intent/error` 帧与 `ExpressionCue`、`MotionPlan` 类型 + 运行时校验函数，验证 `pnpm --filter @mas3d/protocol test` 覆盖合法帧接受与四类非法帧拒绝
- [ ] 1.3 配置 `packages/motion` 为 DOM 无关 TS 包（vitest + tsconfig 严格模式），验证 `pnpm --filter @mas3d/motion test` 在空测试集下以 0 退出
- [ ] 1.4 更新 `.gitignore`：显式忽略 `/素材/`、`/apps/web/public/assets/`、`.tmp/`，并在 `README.md` 写明素材不入库与 `tools/sync-assets.mjs` 的用法，验证 `git status --porcelain` 在同步素材后仍为空
- [ ] 1.5 新增 `assets.manifest.json` 声明素材来源与派生目标（HDRI、PBR、教室 GLB、VRM 路径），验证 `node tools/sync-assets.mjs --check` 能报出缺失项而不写文件

## 2. 资产管线

- [ ] 2.1 实现 `tools/sync-assets.mjs` 的 HDRI 分支：从 `素材/NightSkyHDRI008_8K` 生成 2K EXR 与 1K 预览 JPG 到 `apps/web/public/assets/sky/`，验证目标文件存在且 2K EXR < 8 MB
- [ ] 2.2 实现 PBR 分支：Clay002 / WoodFloor070 / OfficeCeiling001 的 Color、Normal、Roughness 转为 1K WebP，验证输出目录共 9 个文件且每个 < 400 KB
- [ ] 2.3 实现教室 GLB 分支：从 `素材/StylooClassroomAssetPack GLTF & FBX` 挑选课桌椅、黑板、灯带的 GLB 并复制为语义化文件名，验证场景清单里的每个道具路径可由脚本生成并存在
- [ ] 2.4 复制 `素材/莫妮卡.vrm` 到 `apps/web/public/assets/monika/Monika.vrm` 并纳入 git（唯一入库的大文件），验证 `git ls-files apps/web/public/assets/monika` 返回该文件且体积 22.01 MB
- [ ] 2.5 在 `README.md` 记录素材许可与署名要求（VRM 作者 `tyler1234506`、非商用；HAST-SEC 非商用；Poly Haven CC0），验证文档中三处署名与 `design.md` 风险条目一致

## 3. 3D 场景（space-classroom）

- [ ] 3.1 搭建 Vite + React + three 渲染入口，加载到位姿固定的空场景并渲染一帧统计信息，验证 `pnpm --filter @mas3d/web dev` 打开后无控制台错误
- [ ] 3.2 组装太空教室：单个座位区（桌、椅、黑板、灯带）+ 缺口舱壁 + 反向球体天空盒 + 补光，验证截图里能同时看到桌椅、缺口与星空，且 Monika 面部没有欠曝
- [ ] 3.3 实现受限相机：围绕座位的 90° 弧内轨道，俯仰与距离夹取，验证拖拽到边界后角度不再变化且不穿墙
- [ ] 3.4 场景加载失败与资源缺失时的可见降级（占位地面 + 提示条），验证删除一个 GLB 后页面仍可进入并显示提示而不是白屏

## 4. 角色运行时（Tier 0）

- [ ] 4.1 用 `@pixiv/three-vrm` 加载 VRM **0.0** 模型（22.01 MB）并渲染静止姿态，验证模型朝向正确、无 T-pose 塌陷、spring bone 头发自然下垂
- [ ] 4.2 实现呼吸：胸/脊骨正弦驱动（约 4 秒周期）+ 头部微摆，验证连续观察 30 秒无抖动跳变，且停帧前后姿态连续
- [ ] 4.3 实现眨眼：2.5–6 秒随机间隔、90 ms 闭合，驱动 `Fcl_EYE_Close`（单眼时用 `Fcl_EYE_Close_L`/`Fcl_EYE_Close_R`），验证 60 秒内眨眼 10–24 次且左右不完全同步
- [ ] 4.4 实现视线：因 `hasLookAt` 为假，直接驱动 `leftEye`/`rightEye` 骨骼并叠加 head/neck 微量转动，验证注视点在玩家位置与摄像机位置之间切换时有可见差异且眼球不越界
- [ ] 4.5 实现 `FaceDriver`：从 `meshes[*].primitives[*].extras.targetNames` 建 名称→索引 表，缺名时回退 `face.map.json`，直接写 `mesh.morphTargetInfluences`（不使用 `VRMExpressionManager`，因该 VRM 的 18 个表情组 binds 全为空），用 VRoid 分项组合出 中性/喜悦/愉悦/悲伤/生气/惊讶 并做总和 clamp，验证每种情绪切换 < 200 ms 且无残留形变、对应索引的 influences 确实变化
- [ ] 4.6 添加可见署名页脚（`Monika model by tyler1234506 · hub.vroid.com · 非商用`），验证 DOM 中存在该署名且在所有视口宽度下可读

## 5. 表现意图层与动作仲裁

- [ ] 5.1 实现 `deriveCue(text)` 纯函数：剥离引号内转述、命中情绪词表、按标点与重复词累加强度、问句结尾看玩家、动作词表映射，验证单元测试覆盖「引号内情绪词不计入」「强度夹取到 0–1」「相同输入相同输出」三类断言
- [ ] 5.2 实现 Tier 1 程序化手势（点头/摇头/歪头/挥手/前倾/移开视线）关键帧曲线，验证每个手势的采样输出在起止帧与待机姿态连续
- [ ] 5.3 实现仲裁器：Tier 2 占用手臂时抑制新 Tier 1、表情可叠加、强度可抢占、抢占淡出 150 ms、每层最长时长上限，验证单元测试覆盖「低强度不打断手势」「高强度打断」「超时回待机」三条规则
- [ ] 5.4 为 `packages/motion` 补 README 说明规则表与常量文件位置，验证文档中列出的常量名与源码符号一致

## 6. 桥接服务（AstrBot 插件，D1/D2/D4）

- [ ] 6.1 创建插件包 `apps/bridge`（`metadata.yaml`、`main.py`、`requirements.txt`），在 AstrBot 面板中可加载并打印版本，验证 AstrBot 日志出现插件加载记录且无异常堆栈
- [ ] 6.2 用 `tools/link-plugin.ps1` 建立 `~/.astrbot/data/plugins/astrbot_plugin_mas3d_game` → `apps/bridge` 的目录联接，验证重载后插件从仓库目录被加载（日志路径指向 D:\Mas3D\apps\bridge）
- [ ] 6.3 实现自定义平台适配器与事件构造：为每个游戏回合构造 `AstrMessageEvent`，把 `unified_msg_origin` 指向配置的 QQ 会话（默认 `Monika:FriendMessage:2441388327`），验证 pytest 断言覆写后的 umo 与配置值一致
- [ ] 6.4 实现回复捕获：在适配器发送边界截获文本并转交 WS，而不是发往 QQ；验证 pytest 中模拟管线回复后，QQ 发送队列为空而 WS 收到 `reply` 帧
- [ ] 6.5 实现场景标记：回合文本注入 `（3D教室）` 前缀、前端展示时剥离，验证 pytest 断言注入文本与数据库写入内容一致、且响应帧里不含前缀
- [ ] 6.6 用 `context.register_web_api` 暴露 `/mas3d/ws` 与 `/mas3d/api/health`，绑定 127.0.0.1，验证 `curl 127.0.0.1:6185/mas3d/api/health` 返回 200 且 `curl 0.0.0.0` 外部地址无法连通
- [ ] 6.7 实现令牌鉴权：首次启动生成 32 字节随机令牌写入 `plugin_data/mas3d/token`，WS 与 HTTP 均校验，验证无令牌与错令牌各返回 401、正确令牌握手成功
- [ ] 6.8 实现启动自检：探测 `unified_msg_origin` setter 是否存在，缺失时降级为独立 umo 并在日志与前端给出告警，验证 pytest 模拟缺失 setter 时走降级分支且不抛异常
- [ ] 6.9 为桥接层写 `TECH.md`：依赖的 AstrBot 4.28.2 内部符号、已知升级风险、关闭插件即恢复 QQ 原状的说明，验证文档中的每个符号路径可在当前安装中 `grep` 命中

## 7. 对话前端与状态机（dialogue-console）

- [ ] 7.1 实现 WS 客户端与客户端状态机 `idle→sending→thinking→streaming→idle`（含 `error` 与重试），验证用 `tools/mock-bridge.mjs` 逐状态驱动时 UI 文案与禁用态正确
- [ ] 7.2 实现对话记录区：渲染最近 50 回合、自动滚动、时间戳、来源徽标（主通道/降级），验证 mock 注入 60 条后只渲染 50 条且滚动到底
- [ ] 7.3 实现输入框约束：1–300 字（与动作后端契约一致）、Enter 发送、Shift+Enter 换行、思考中禁止重复提交，验证超长输入被截断并有计数提示
- [ ] 7.4 实现设置持久化（音量、视线灵敏度、动作增强开关、相机灵敏度）到 localStorage，验证刷新后设置保持且关闭动作增强后不再请求动作后端
- [ ] 7.5 实现断线与降级提示：WS 断开 >3 秒显示横幅并允许重连、主通道失败时展示本地兜底回复并标注来源，验证拔掉桥接进程后 3 秒内出现横幅、输入仍可用
- [ ] 7.6 实现会话窗口导出（最近 50 回合为文本/JSON），验证导出的文件包含全部可见回合且不含令牌

## 8. HAST-SEC 适配（D8）

- [x] 8.1 实现服务端代理 `POST /mas3d/api/motion/plan`：转发到 `http://127.0.0.1:8769/v13-api/plan`，显式设置 `Host` 与 `origin`（两者均为该 URL）、`content-type: application/json`，验证本地 echo 服务的 pytest 断言收到的两个头部与预期完全一致
- [x] 8.2 实现入参整形：`generation=v15`、`text` 截断到 300 字、`context` ≤4 条/总长 ≤1200/单条 ≤600、稳定会话 GUID（按游戏会话持久化），验证 pytest 覆盖超长文本截断与 GUID 复用
- [x] 8.3 实现返回值校验：按 `motion_result.mjs` 规则校验 `base`、`operation`、`timing`、`intents` 边界，非法计划丢弃并记录可诊断错误，验证用三组合法/两组非法 fixture 断言通过与否
- [x] 8.4 实现探活与冷却：会话开始与失败后 1.5 秒超时探测 `/v13-api/status`，失败则禁用 60 秒后重试，验证杀掉 8769 服务后不再发起请求、恢复服务 60 秒后自动重新可用
- [x] 8.5 实现 Tier 2 映射：`side`/`action`/`height` → 上臂/前臂/手旋转目标，遵守 `speed`、`strength`、`hold`、`delay` 与 `timing` 次序，验证单元测试对八种 action 各自产生不同且有界的旋转序列
- [x] 8.6 实现 `unsupported` 与空计划的降级：丢弃到 Tier 1 纯闲聊表现，验证传入 `后空翻` 类计划后角色无突变且无控制台异常
- [x] 8.7 在 `README.md` 记录启动 HAST-SEC 的命令（`node service/start.mjs`，端口 8769）与「意图级消费、不引入其运行时」的边界，验证文档命令在 `D:\HAST-SEC-v0.1.1\HAST-SEC-v0.1.1` 下可执行并返回 status

## 9. 语音与口型（D15–D19）

- [x] 9.1 实现 `tools/extract-face-map.mjs`：解析 VRM 的 `meshes[*].primitives[*].extras.targetNames` 与 `blendShapeMaster`，输出 `face.map.json`（名称→索引 + 语义组合表），验证 57 个名称全部落表、索引 13/14/15 分别为 `Fcl_EYE_Close`/`_R`/`_L`、索引 39–43 为 `Fcl_MTH_A/I/U/E/O`
- [x] 9.2 实现表情组合表（neutral/joy/fun/sorrow/angry/surprised/blink/wink）：按 VRoid 分项叠加并 clamp 总和 ≤ 1，验证单元测试覆盖每个组合的权重和与切换时长
- [x] 9.3 实现 `TtsClient`：先 `/set_gpt_weights` 与 `/set_sovits_weights` 锁权重，再 POST `127.0.0.1:9880/tts`（参数见 D16），验证返回 WAV 为 32 kHz 单声道 16-bit，且日志记录生效权重文件名与耗时
- [x] 9.4 实现中文→英文转换：读 `~/.astrbot/data/gsv_tts_translate.json` 的 `api_base`/`model`/`temperature`，失败回退原文，验证翻译端点返回 500 时仍能合成出音频
- [x] 9.5 实现逐句切分与流水线：服务端按标点切句（9.3）并支持 `sentence` 下标切片，客户端 `VoicePlayer` 在播放第 N 句时预取第 N+1 句（队列深度 ≤ 2），单句 30 秒 / 整轮 60 秒超时，验证两句回复串行播放、句间由 L1 归零
- [x] 9.6 实现音频缓存 `<plugin_data>/mas3d/cache/tts/<sha256(text|weights|params)>.wav`（LRU 500），验证同一句第二次请求不产生 HTTP 调用
- [x] 9.7 实现口型 L0：WebAudio `AnalyserNode` 包络 → `Fcl_MTH_A`（门限 0.02 + 上升 0.9/帧、下降 0.25/帧的非对称平滑；开口单一通道承载，不叠 `Fcl_MTH_Down` 以免双重张颌），验证静音段权重归零、有声段跟随能量
- [x] 9.8 实现口型 L1/L2：句间 120 ms 归零窗口 + 英文 grapheme→viseme 规则表（a/ɑ→A、i/ɪ/y→I、u/ʊ/w→U、e/ɛ→E、o/ɔ→O，无元音词与停顿不写口型），验证示例词的 viseme 选择与句间归零
- [x] 9.9 实现嘴部通道抑制：语音播放期间 `Fcl_MTH_*` 的表情写入压到 0.25、结束后 200 毫秒恢复到当前情绪，验证单元测试断言「说话中触发喜悦」的嘴部权重不超过阈值、结束后回到期望值
- [x] 9.10 实现「仅字幕」开关与语音降级：TTS 未启动时静音并给出一次非阻断提示、冷却 60 秒后自动重试，验证杀掉 9880 进程后回合仍完成、无异常堆栈
- [x] 9.11 在 `README.md` 记录 GPT-SoVITS 启动命令与「权重显式锁定」要求，验证文档命令与 `api-start.bat` 一致且能返回 status

## 10. 集成与验收

- [x] 10.1 编写 `tools/mock-bridge.mjs`：可剧本化的 WS 服务（正常回复/超时/错误/动作计划），验证 Playwright 冒烟用例全部基于该 mock 通过
- [x] 10.2 Playwright 冒烟：场景挂载 → 发送一句 → 收到回复 → 表情变化 → 断开连接出现横幅，验证 `pnpm --filter @mas3d/web e2e` 退出码 0
- [x] 10.3 真实链路联调：在 AstrBot 在线、QQ 会话为 `Monika:FriendMessage:2441388327` 的情况下完成一回合，验证 3D 教室回复与 QQ 会话中同一回合的文本完全一致（逐字比对）
- [x] 10.4 语音链路联调：一句带情绪的中文回复完整走过 翻译 → 合成 → 播放 → 口型 → 结束恢复，验证字幕为中文、音频为英文、口型随能量变化且在句间归零
- [x] 10.5 验收清单记录：把「VRM 表情/视线、语音与口型、HAST-SEC 在线与离线两种手臂表现、QQ 副作用、断线恢复」逐项结果写入 `openspec/changes/add-monika-space-classroom/verification.md`，验证清单每项都有观察到的事实而非断言

## Workflow follow-up

- 用户验收通过后，用 `openspec-archive-change` 归档本变更并同步主规格。
- 归档前按 AGENTS.md 要求先征询用户再 commit/push。