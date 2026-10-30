# Mas3D 太空教室实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把已经活在 QQ 文本框里的 Monika 搬进浏览器里的 3D 太空教室，与 QQ 共用同一条会话、同一套人设与记忆，并让她说话、看人、做动作。

**Architecture:** pnpm monorepo。`packages/protocol` 定义前后端共享帧与校验，`packages/motion` 是与 DOM 无关的纯函数层（情绪推导、手势曲线、仲裁），`apps/web` 是 Vite + React + three + @pixiv/three-vrm 的渲染与对话前端，`apps/bridge` 是跑在 AstrBot 进程里的插件（自定义平台适配器把 `unified_msg_origin` 指向既有 QQ 会话，`context.register_web_api` 暴露 WS/HTTP）。语音由桥接直连本机 GPT-SoVITS `api_v2`，口型在前端由音频包络驱动面部 morph。

**Tech Stack:** TypeScript 5.x / pnpm workspaces / Vite / React 18 / three 0.180.0 / @pixiv/three-vrm 3.5.5 / Zustand / Vitest / Playwright / Python 3 (AstrBot 4.28.2 内嵌) / pytest / FastAPI(Starlette)

**Spec:** `openspec/changes/add-monika-space-classroom/`（proposal.md、design.md、tasks.md、specs/ 七份 delta）

## Global Constraints

- Node `>=22`；包管理器 pnpm（工作区根 `pnpm-workspace.yaml`），不用 npm/yarn。
- `three@0.180.0` 与 `@pixiv/three-vrm@3.5.5` 固定版本，与 HAST-SEC 侧一致。
- 端口：web dev `5173`、AstrBot 面板 `6185`、HAST-SEC `8769`、GPT-SoVITS `9880`。桥接端点一律只绑 `127.0.0.1`。
- 令牌：首次启动生成 32 字节随机值，写 `<AstrBot data>/plugin_data/mas3d/token`，HTTP 用 `Authorization: Bearer`、WS 用 query `?token=`；无令牌/错令牌一律 401。
- 共享会话 umo 默认 `Monika:FriendMessage:2441388327`，回合文本注入前缀 `（3D教室）`，展示时剥离。
- 玩家输入 1–300 字；transcript 只保留最近 50 回合；客户端状态机 `idle→sending→thinking→streaming→idle`（含 `error`）。
- HAST-SEC：`generation='v15'`，`text` ≤300 字，`context` ≤4 条 / 总长 ≤1200 / 单条 ≤600，会话 GUID 持久，探活 1.5s、失败冷却 60s。
- 语音：字幕中文、合成英文；单句超时 30s、整轮 60s；缓存 `<plugin_data>/mas3d/cache/tts/<sha256(text|weights|params)>.wav`，LRU 500；语音默认开启，"仅字幕"开关关闭后不发起任何合成请求。
- 口型：说话期间表情对 `Fcl_MTH_*` 的写入压到 `0.25`，语音结束 200ms 内恢复；眨眼间隔 2.5–6s、闭合 90ms；呼吸周期 4s；相机限制在座位前 90° 弧。
- 大体积二进制（`素材/`、教室 GLB、HDRI、PBR）不入库，只有 `素材/莫妮卡.vrm`（22.01 MB）入库。产品非商用，页脚署名 `Monika model by tyler1234506 · hub.vroid.com`。
- 每个任务结束都要 commit，且只在本地分支 `add-monika-space-classroom` 上提交；推送前必须征询用户。

## Review Focus

1. **引号内的情绪词被当成她的情绪** —— 玩家发"她说她很生气"，她不该真的生气。
2. **桥接不可达时发送** —— 界面不能永久停在 thinking，3 秒内出现横幅并可重试。
3. **VRM 换版本或形变通道缺失** —— 不能白屏或抛异常，缺通道要静默降级。
4. **TTS 未启动 / 合成 500 / 翻译 500** —— 回合必须照常完成，只留字幕，控制台无异常堆栈。
5. **非法 WS 帧、错令牌、超长输入** —— 桥接不得崩溃，且任何非法回合都不得写入 QQ 会话。

---

## Task 1: monorepo 骨架与 protocol 包

**Files:**
- Create: `package.json`、`pnpm-workspace.yaml`、`tsconfig.base.json`、`.npmrc`
- Create: `packages/protocol/package.json`、`packages/protocol/tsconfig.json`、`packages/protocol/src/index.ts`、`packages/protocol/src/frames.ts`、`packages/protocol/src/plan.ts`、`packages/protocol/test/frames.test.ts`
- Modify: `.gitignore`（已含 `/素材/`、`.worktrees/`，追加 `/apps/web/public/assets/`）

**Interfaces:**
- Produces（`@mas3d/protocol`）:
```ts
export type Emotion = 'neutral'|'joy'|'fun'|'sorrow'|'angry'|'surprised';
export type Gaze = 'player'|'away'|'down'|'up'|'left'|'right';
export type Gesture = 'none'|'nod'|'shake'|'tilt'|'wave'|'lean_in'|'look_away';
export interface ExpressionCue { emotion: Emotion; intensity: number; gaze: Gaze; gesture: Gesture }
export interface VoiceRef { audioUrl: string; durationMs: number; sentenceStartsMs: number[]; englishText: string }
export type ServerFrame =
  | { type:'ack'; turnId:string } | { type:'thinking'; turnId:string }
  | { type:'reply'; turnId:string; text:string; cue:ExpressionCue; voice?:VoiceRef }
  | { type:'intent'; turnId:string; plan:MotionPlan }
  | { type:'error'; turnId?:string; code:'bridge_down'|'timeout'|'model_error'|'bad_request'|'rate_limited'; message:string };
export interface MotionIntent { key:string; speed:number; strength:number; hold:number; delay:number }
export interface MotionPlan { base:{ change:boolean; name:string; motion:number; rest:boolean }; reply?:string; operation:'chat'|'command'|'release'|'unsupported'; timing:'同时'|'依次'; intents:MotionIntent[] }
export class ProtocolError extends Error {}
export function parseServerFrame(raw: unknown): ServerFrame;
export function validatePlan(raw: unknown): MotionPlan;
export const MAX_INPUT_CHARS = 300;
```
- `validatePlan` 边界（照 `HAST-SEC/motion_result.mjs` 的 `validateResult` 收窄）：`intents` ≤4；`speed` 0.4–2；`strength` 0–1；`hold` 0–20；`delay` 0–30；`base.name` ≤40 且非空；`reply` ≤600；`operation==='command'` 需至少 1 条 intent 或 `base.change`；非 `command` 时 `intents` 必须为空。

- [ ] **Step 1: 写失败测试** `packages/protocol/test/frames.test.ts`
```ts
it('parses a valid reply frame', () => {
  const f = parseServerFrame({ type:'reply', turnId:'t1', text:'好呀', cue:{ emotion:'joy', intensity:0.8, gaze:'player', gesture:'nod' } });
  expect(f.type).toBe('reply');
});
it('rejects an unknown frame type', () => {
  expect(() => parseServerFrame({ type:'nope' })).toThrow(ProtocolError);
});
it('rejects a plan with five intents', () => {
  expect(() => validatePlan({ base:{ change:false, name:'x', motion:0, rest:true }, operation:'command', timing:'同时', intents:Array(5).fill({ key:'arm', speed:1, strength:1, hold:1, delay:0 }) })).toThrow(ProtocolError);
});
it('rejects a chat plan carrying intents', () => {
  expect(() => validatePlan({ base:{ change:false, name:'x', motion:0, rest:true }, operation:'chat', timing:'同时', intents:[{ key:'arm', speed:1, strength:1, hold:1, delay:0 }] })).toThrow(ProtocolError);
});
```
- [ ] **Step 2: 跑测试确认失败** — `pnpm --filter @mas3d/protocol test`；预期 `Cannot find module` / `parseServerFrame is not a function`。
- [ ] **Step 3: 实现** — `frames.ts` 用穷举 `switch` 校验并抛 `ProtocolError`；`plan.ts` 逐字段夹取（越界抛错，不静默修正）；`index.ts` 重导出；根 `package.json` 加 `"packageManager": "pnpm@9"`、`"engines": { "node": ">=22" }`、workspace 根脚本 `"test": "pnpm -r test"`。
- [ ] **Step 4: 跑测试确认通过** — `pnpm --filter @mas3d/protocol test`；预期 4 passed。
- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(protocol): add shared frames and plan validation"`

## Task 2: motion 包的情绪推导 `deriveCue`

**Files:**
- Create: `packages/motion/package.json`、`tsconfig.json`、`src/deriveCue.ts`、`src/lexicon.ts`、`test/deriveCue.test.ts`
- Modify: `tools/`… （无）

**Interfaces:**
- Produces: `export function deriveCue(text: string, opts?: { questionToPlayer?: boolean }): ExpressionCue`
- 规则（design D6）：先剥离成对引号 `"" '' 「」『』` 与 `说|说：|表示` 后的转述内容；情绪词表命中取最高分，同权重时按 `sorrow<angry<surprised<joy<fun` 次序；`!` 每个 +0.15、`？` 结尾且命中疑问 → `gaze='player'`；连续重复字符（≥3）每处 +0.1；强度夹到 `[0,1]`，默认 `0.5`；动作词表 `点头|嗯|好的`→`nod`、`摇头|不`→`shake`、`歪头|嗯？`→`tilt`、`挥手|拜拜`→`wave`、`凑近|靠近`→`lean_in`、`移开|别看我`→`look_away`。

- [ ] **Step 1: 写失败测试**
```ts
it('ignores emotion words inside quoted speech', () => {
  expect(deriveCue('她说"我很生气"，然后笑了').emotion).not.toBe('angry');
});
it('is deterministic', () => {
  expect(deriveCue('太好了！！')).toEqual(deriveCue('太好了！！'));
});
it('clamps intensity to 1 with repeated exclamation', () => {
  expect(deriveCue('真的！！！').intensity).toBeLessThanOrEqual(1);
});
it('maps 点头 to nod', () => { expect(deriveCue('嗯，点头').gesture).toBe('nod'); });
it('returns neutral for empty-ish text', () => { expect(deriveCue('……').emotion).toBe('neutral'); });
```
- [ ] **Step 2: 跑测试确认失败** — `pnpm --filter @mas3d/motion test`。
- [ ] **Step 3: 实现** — 词表放 `lexicon.ts`（`EMOTION_LEXICON: Record<Emotion, {words:string[], weight:number}[]>`、`GESTURE_LEXICON`）；`deriveCue` 纯函数、无 IO、无随机。
- [ ] **Step 4: 跑测试确认通过** — 5 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(motion): derive expression cues from reply text"`

## Task 3: 手势曲线与仲裁器

**Files:**
- Create: `packages/motion/src/gestures.ts`、`packages/motion/src/arbiter.ts`、`packages/motion/src/constants.ts`、`test/arbiter.test.ts`、`README.md`

**Interfaces:**
- Produces:
```ts
export interface PoseDelta { headPitch:number; headYaw:number; headRoll:number; spineBend:number; armRaiseL:number; armRaiseR:number }
export const GESTURE_DURATION: Record<Gesture, number>; // nod 900, shake 800, tilt 1200, wave 1600, lean_in 1400, look_away 1500, none 0
export function sampleGesture(g: Gesture, tMs: number, intensity: number): PoseDelta; // tMs∈[0,duration]，两端必须回到 0
export class Arbiter {
  request(g: Gesture, intensity: number, nowMs: number): boolean; // 返回是否被接受
  sample(nowMs: number): { gesture: Gesture; intensity: number; delta: PoseDelta };
  setArmOwnedByTier2(owned: boolean, nowMs: number): void;
}
```
- 仲裁规则（D7）：Tier2 占手臂时拒绝新的 Tier1；同级请求强度必须 > 当前 0.2 才抢占；抢占淡出 150ms（`PREEMPT_FADE_MS`）；`hold` 上限 20s 由调用方保证。

- [ ] **Step 1: 写失败测试**
```ts
it('rejects a new gesture while tier2 owns the arms', () => {
  const a = new Arbiter(); a.setArmOwnedByTier2(true, 0);
  expect(a.request('wave', 0.9, 0)).toBe(false);
});
it('does not interrupt a strong gesture with a weak one', () => {
  const a = new Arbiter(); a.request('wave', 0.9, 0);
  expect(a.request('nod', 0.4, 100)).toBe(false);
});
it('returns to rest after the clip ends', () => {
  const a = new Arbiter(); a.request('nod', 1, 0);
  const s = a.sample(GESTURE_DURATION.nod + 50);
  expect(s.delta).toEqual({ headPitch:0, headYaw:0, headRoll:0, spineBend:0, armRaiseL:0, armRaiseR:0 });
});
it('starts and ends a clip at zero', () => {
  expect(sampleGesture('nod', 0, 1).headPitch).toBe(0);
  expect(sampleGesture('nod', GESTURE_DURATION.nod, 1).headPitch).toBe(0);
});
```
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 曲线用 `easeInOutCubic` × `sin(π·u)`，手势表按 `headPitch/headYaw/headRoll/spineBend/armRaise{L,R}` 给目标幅度；`constants.ts` 集中所有魔数。
- [ ] **Step 4: 跑测试确认通过** — 9 passed（5+4）。
- [ ] **Step 5: Commit** — `git commit -am "feat(motion): gesture clips and arbitration rules"`

## Task 4: 资产管线与 manifest

**Files:**
- Create: `tools/sync-assets.mjs`、`tools/assets.manifest.json`、`tools/README.md`
- Modify: `package.json`（脚本 `"assets": "node tools/sync-assets.mjs"`、`"assets:check": "node tools/sync-assets.mjs --check"`）

**Interfaces:**
- Produces：`apps/web/public/assets/` 下 `hdri/night-sky-2k.exr`、`hdri/night-sky-1k.jpg`、`pbr/{clay,wood-floor,ceiling}-{color,normal,roughness}@1k.webp`、`classroom/{desk,chair,blackboard,light}.glb`、`monika/Monika.vrm`。
- `--check` 语义：不写文件，只比对 manifest 中每个 `target` 是否存在且 `bytes` 与 `sha256` 匹配，任一不符退出码 1 并打印差异。

- [ ] **Step 1: 写清单** `tools/assets.manifest.json`：每条 `{ "source": "素材/…", "target": "apps/web/public/assets/…", "kind": "hdri|pbr|glb|vrm", "transform": "resize:2048|webp:1024|copy" }`；VRM 用 `"transform": "copy", "tracked": true`。
- [ ] **Step 2: 先写检查** — 在 `sync-assets.mjs` 中实现 `--check` 与空实现的分支，运行 `pnpm assets:check`；预期退出码 1 且打印 `missing: apps/web/public/assets/hdri/night-sky-2k.exr`。
- [ ] **Step 3: 实现四种 transform** — HDRI 用 `sharp` 出 1K JPG（EXR→2K 用 `oiiotool` 若存在，否则直接复制 8K 并在 README 记「2K 派生待手工」）；PBR 用 `sharp` 转 webp q=82；GLB 按 manifest 白名单复制并改名；VRM 直拷。
- [ ] **Step 4: 验证** — `pnpm assets && pnpm assets:check`；预期退出码 0，`apps/web/public/assets/monika/Monika.vrm` 为 23,081,108 字节。
- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(tools): asset pipeline with manifest check"`

## Task 5: web 骨架与太空教室场景

**Files:**
- Create: `apps/web/{package.json,tsconfig.json,vite.config.ts,index.html}`、`src/main.tsx`、`src/App.tsx`、`src/scene/{Scene.tsx,classroom.ts,skybox.ts,lights.ts,cameraRig.ts}`、`src/scene/__tests__/cameraRig.test.ts`
- Modify: `pnpm-workspace.yaml`

**Interfaces:**
- Produces：`export function clampOrbit(yaw:number, pitch:number, distance:number): {yaw:number;pitch:number;distance:number}`（yaw ∈ 座位前 ±45°、pitch ∈ −10°..25°、distance ∈ 1.6..3.2）；`<Scene onReady?={(api:SceneApi)=>void} />`，`SceneApi = { setEmotion(e:Emotion,i:number):void; playGesture(g:Gesture,i:number):void }`（本任务先给空实现，Task 6/8 填充）。

- [ ] **Step 1: 写失败测试** `cameraRig.test.ts`：`clampOrbit(90, 0, 2)` 的 yaw 被夹到 45；`clampOrbit(0, 90, 9)` 得 pitch 25、distance 3.2。
- [ ] **Step 2: 跑测试确认失败** — `pnpm --filter @mas3d/web test`。
- [ ] **Step 3a: 写失败测试** `qualityLadder.test.ts`：`nextQuality('high', 24)` 得到 `'medium'`；`nextQuality('low', 24)` 仍是 `'low'`（降级项用尽不抛错）；`nextQuality('low', 60)` 回到 `'medium'`。实现 `export function nextQuality(cur: Quality, fps: number): Quality`（像素比 2→1.5→1、阴影 high→off、环境贴图 1K→512，恢复需连续 3 秒高于阈值 55fps）。
- [ ] **Step 3: 实现场景** — `classroom.ts` 装配单个座位区（桌、椅、黑板、灯带，来自 `/assets/classroom/*.glb`，缺失时用色块占位并置 `degraded=true`）；`skybox.ts` 用 1K JPG 做 `scene.background = equirect` 的反向球体；`lights.ts` 给一盏方向性 key + 环境补光；`cameraRig.ts` 实现受限轨道。`clampOrbit` 单独放在 `cameraRig.ts` 以便测试。
- [ ] **Step 4: 加载过程与署名** — 进度条按 `LoadingManager` 的 `loaded/total` 更新；加载失败显示错误卡与“重试”按钮（重新 `location.reload()` 前清 `scene`）；页脚固定渲染 `Monika model by tyler1234506 · hub.vroid.com · 非商用`（字号 ≥12px，任何视口可读）。
- [ ] **Step 5: 验证** — `pnpm --filter @mas3d/web dev` 打开 5173：能看到桌椅、缺口星空、Monika 座位空着；篡改一个 GLB 文件名后刷新出现提示条（含重试按钮）而不是白屏；把窗口压到 640px 宽页脚仍可读；`pnpm --filter @mas3d/web test` 2 passed。
- [ ] **Step 6: Commit** — `git commit -am "feat(web): space classroom scene with constrained camera"`

## Task 6: face.map.json 与 FaceDriver

**Files:**
- Create: `tools/extract-face-map.mjs`、`apps/web/public/assets/monika/face.map.json`（生成物，入库）、`apps/web/src/avatar/faceDriver.ts`、`apps/web/src/avatar/FaceDriver.ts`、`apps/web/src/avatar/__tests__/faceDriver.test.ts`

**Interfaces:**
- Produces：
```ts
export interface FaceMap { version:1; targets:Record<string, number>; presets:Record<Emotion, { target:string; weight:number }[]> }
export function resolveFaceMap(gltfJson: unknown, fallback: FaceMap): FaceMap; // 优先读 meshes[*].primitives[*].extras.targetNames
export class FaceDriver {
  constructor(meshes: { morphTargetInfluences?: number[] }[], map: FaceMap);
  setEmotion(e: Emotion, intensity: number): void;
  setVisemes(v: Partial<Record<'A'|'I'|'U'|'E'|'O', number>>): void;
  setMouthSuppressed(on: boolean): void;
  update(dtMs: number): void;
}
```
- 组合表（D15）：`joy` = `Fcl_ALL_Joy` 0.8 + `Fcl_EYE_Joy` 0.5 + `Fcl_MTH_Joy` 0.4；`fun` = `Fcl_ALL_Fun` 0.9；`sorrow` = `Fcl_ALL_Sorrow` 0.8 + `Fcl_BRW_Sorrow` 0.5；`angry` = `Fcl_ALL_Angry` 0.8 + `Fcl_BRW_Angry` 0.6；`surprised` = `Fcl_ALL_Surprised` 0.8 + `Fcl_EYE_Spread` 0.4 + `Fcl_MTH_Large` 0.3；`neutral` = 空数组。写入时同名取 max、总和 clamp ≤1。缺 target 时静默跳过并计数 `degradedCount`。

- [ ] **Step 1: 写提取脚本并生成映射** — `node tools/extract-face-map.mjs apps/web/public/assets/monika/Monika.vrm`；断言输出含 57 条 targets、`targets['Fcl_EYE_Close']===13`、`targets['Fcl_MTH_A']===39`、`targets['Fcl_MTH_O']===43`；不一致则脚本退出码 1。
- [ ] **Step 2: 写失败测试** `faceDriver.test.ts`：给定 `{ targets:{ 'Fcl_ALL_Joy':0,'Fcl_MTH_Joy':1 }, presets:{ joy:[{target:'Fcl_ALL_Joy',weight:0.8},{target:'Fcl_MTH_Joy',weight:0.4}], neutral:[], fun:[], sorrow:[], angry:[], surprised:[] } }`，`setEmotion('joy',1)` 后 `influences[0]===0.8 && influences[1]===0.4`；`setEmotion('sorrow',1)` 不抛错且数组归零；`setMouthSuppressed(true)` 后 `influences[1]===0.1`（0.4×0.25）。
- [ ] **Step 3: 跑测试确认失败**
- [ ] **Step 4: 实现 `FaceDriver`** — 内部维护 `target`（表情层）与 `viseme`（口型层）两组权重，`update` 时合并写入（口型优先），表情层 200ms 线性插值到目标值。
- [ ] **Step 5: 跑测试确认通过** — 3 passed；`git commit -am "feat(avatar): face map extraction and morph driver"`

## Task 7: VRM 装载与 Tier 0 待机

**Files:**
- Create: `apps/web/src/avatar/MonikaAvatar.tsx`、`src/avatar/idle.ts`、`src/avatar/gaze.ts`、`src/avatar/__tests__/idle.test.ts`

**Interfaces:**
- Produces：`export function blinkSchedule(seed:number): number[]`（间隔序列 2.5–6s，闭合 90ms，确定性伪随机）；`export function nextBlink(nowMs:number, rnd:()=>number): number`；`export function gazeTarget(cam: {x:number;y:number;z:number}, head:{x:number;y:number;z:number}, mode:Gaze): {yaw:number; pitch:number}`（对 `away/down` 偏置，超出 ±35° 夹取）；`MonikaAvatar` 暴露 `SceneApi` 的三个方法。

- [ ] **Step 1: 写失败测试**：`blinkSchedule(1)` 首值 ∈ [2500,6000] 且两次调用相同；`gazeTarget` 在 `mode='down'` 时 pitch 为正（低头）；`mode='away'` 的 |yaw| ≥ 25°。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 用 `GLTFLoader` + `VRMLoaderPlugin` 载入 `/assets/monika/Monika.vrm`；呼吸：`chest/spine` 旋转 ±0.012 rad、周期 4s，头部微摆周期 7s；眨眼走 `FaceDriver` 的 `Fcl_EYE_Close`；视线驱动 `leftEye/rightEye` 骨骼（该模型 `hasLookAt:false`）并叠加 head/neck；加载失败渲染占位人形 + 提示，不抛错。
- [ ] **Step 4: 验证** — dev 页面连续观察 60s：眨眼 10–24 次、呼吸连续、拖相机时眼神跟随且眼球不越界；删除 VRM 后出现占位而非白屏；4 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(avatar): load VRM and drive procedural idle"`

## Task 8: Tier 1 手势接线与 Tier 2 握手位

**Files:**
- Modify: `apps/web/src/avatar/MonikaAvatar.tsx`、`packages/motion/src/arbiter.ts`（如需）
- Create: `apps/web/src/avatar/applyPose.ts`、`__tests__/applyPose.test.ts`

**Interfaces:**
- Produces：`export function applyPose(bones: Record<'head'|'neck'|'spine'|'upperArmL'|'upperArmR', {rotation:{x:number;y:number;z:number}}>, delta: PoseDelta, weight:number): void`；`MonikaAvatar` 每帧 `arbiter.sample(now)` + `applyPose`，并在收到 `intent` 帧且 `operation==='command'` 时 `setArmOwnedByTier2(true)` 直到最后一个 intent 的 `delay+hold` 结束。

- [ ] **Step 1: 写失败测试**：`applyPose` 对 `weight=0` 不改变骨骼；`weight=1` 时 head.x 增加 delta.headPitch。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 注意 `MeshStandardMaterial`/骨骼命名差异：用 `vrm.humanoid.getNormalizedBoneNode('head')` 等归一化节点，避免直接猜 `J_Bip_*` 名称。
- [ ] **Step 4: 验证** — 控制台手动 `playGesture('nod')` 三次，动作不叠加、不弹跳；`pnpm --filter @mas3d/web test` 6 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(avatar): apply gesture poses through the arbiter"`

## Task 9: mock bridge 与对话界面

**Files:**
- Create: `tools/mock-bridge.mjs`、`apps/web/src/chat/{ChatPanel.tsx,useBridge.ts,store.ts}`、`src/chat/__tests__/stateMachine.test.ts`、`src/settings.ts`

**Interfaces:**
- Produces：`export type Phase = 'idle'|'sending'|'thinking'|'streaming'|'error'`；`export function reduce(state:{phase:Phase;turns:Turn[]}, ev:{type:'send'|'ack'|'thinking'|'reply'|'error'|'reset'; payload?:any}): {phase:Phase;turns:Turn[]}`；`useBridge(url, token)` 返回 `{phase, turns, send, retry}`。
- `mock-bridge.mjs`：`node tools/mock-bridge.mjs --port 8787 --scenario happy|timeout|error|command`，按 `ServerFrame` 发帧，含 `--token` 校验。

- [ ] **Step 1: 写失败测试**：`reduce` 的 `send→thinking→reply→idle` 序列；`reply` 后 `turns.length` +1 且 `text` 已剥离 `（3D教室）` 前缀；`error` 后 `phase==='error'` 且 `turns` 不变。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现 UI** — 输入框 1–300 字（超长截断 + 计数、空输入禁用发送）、Enter 发送 / Shift+Enter 换行、thinking 时禁用提交、消息流自动滚动且只渲染最近 50 回合、来源徽标（主通道/降级）、断线 >3s 横幅 + 重连按钮、设置（音量、视线灵敏度、动作增强、相机灵敏度）写 `localStorage`、导出最近 50 回合为 JSON（不含令牌）。
- [ ] **Step 3a: 持久化与焦点** — 回合写入 `localStorage['mas3d.transcript']`（只留 50 条，`QuotaExceededError` 时降级为内存并提示一次）；「清空历史」必须二次确认，取消时不清、确认后清本地并可选择是否同时停止显示（不影响 AstrBot 一侧）；输入框聚焦时方向键/空格不驱动相机，`Esc` 退出输入后恢复键盘视角。
- [ ] **Step 3b: 动作后端回复不覆盖真相** — 若 `MotionPlan.reply` 存在且与主回复不同，作为独立一条“动作后端”消息展示（灰底 + 标签），永不替换主回复气泡。
- [ ] **Step 4: 验证** — `node tools/mock-bridge.mjs --scenario command` 时页面收到 intent 帧且手臂抬起；`--scenario timeout` 时 8s 后进 error 并可重试；杀 mock 进程 3s 内出现横幅；刷新后历史恢复但不超过 50 条；`--scenario timeout` 期间按方向键相机不动（焦点在输入框）；9 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(chat): dialogue console against a scripted mock bridge"`

## Task 10: 插件骨架与热链接

**Files:**
- Create: `apps/bridge/metadata.yaml`、`apps/bridge/main.py`、`apps/bridge/requirements.txt`、`apps/bridge/README.md`、`tools/link-plugin.ps1`

**Interfaces:**
- Produces：插件名 `astrbot_plugin_mas3d_game`，`main.py` 暴露 `class Mas3dGamePlugin(Star)` 与 `async def initialize(self)`（Task 12 在此注册 web api），`__version__ = '0.1.0'`。
- `link-plugin.ps1`：在 `$env:USERPROFILE\.astrbot\data\plugins\astrbot_plugin_mas3d_game` 建 Junction 指向 `<repo>\apps\bridge`，已存在则先删除旧联接（仅当目标是本仓库）。

- [ ] **Step 1: 写插件骨架**（metadata + 空 Star 子类，日志打印版本）
- [ ] **Step 2: 链接并加载** — `pwsh tools/link-plugin.ps1` 后在 AstrBot 面板重载插件；预期日志出现 `mas3d_game 0.1.0 loaded` 且路径指向 `D:\Mas3D\.worktrees\add-monika-space-classroom\apps\bridge`。
- [ ] **Step 3: 写 pytest 骨架**（`apps/bridge/tests/conftest.py` 用 stub `Context`，`test_plugin_loads` 断言版本号可读）— 运行 `python -m pytest apps/bridge/tests -q` 预期 1 passed。
- [ ] **Step 4: Commit** — `git commit -am "feat(bridge): plugin skeleton loadable by AstrBot"`

## Task 11: 平台适配器与 umo 覆写

**Files:**
- Create: `apps/bridge/adapter.py`、`apps/bridge/session.py`、`apps/bridge/tests/test_adapter.py`
- Modify: `apps/bridge/main.py`

**Interfaces:**
- Produces：`class Mas3dPlatformAdapter(AstrMessageEvent)` 子类 `class GameMessageEvent(AstrMessageEvent)`，构造签名 `GameMessageEvent(text:str, umo:str, on_send: Callable[[str], Awaitable[None]])`；`async def run_turn(bridge: TurnBridge, text: str) -> str`：构造事件 → 覆写 `unified_msg_origin` → 交给 `context.get_platform_inst(...)` 之外的最小管线（直接 `await bridge.dispatch(event)`）→ 从 `on_send` 取回文本。
- 场景前缀常量 `SCENE_PREFIX = '（3D教室）'`，注入 `f'{SCENE_PREFIX}{text}'`，回传前 `strip_scene_prefix`。

- [ ] **Step 1: 写失败测试** `test_adapter.py`：`test_umo_is_overwritten`（断言事件 `unified_msg_origin == 'Monika:FriendMessage:2441388327'`）；`test_qq_send_queue_stays_empty`（stub 平台实例的 `send` 未被调用，而 `on_send` 收到文本）；`test_scene_prefix_injected_and_stripped`。
- [ ] **Step 2: 跑测试确认失败** — `python -m pytest apps/bridge/tests/test_adapter.py -q`。
- [ ] **Step 3: 实现** — 覆写走 `astr_message_event.py:112` 的 property setter；setter 缺失时捕获 `AttributeError` 并降级为独立 umo `mas3d:GameMessage:<uuid>`，同时置 `self.degraded=True` 供前端展示。
- [ ] **Step 4: 跑测试确认通过** — 3 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(bridge): custom event with QQ umo overwrite and reply capture"`

## Task 12: WS/HTTP 端点与令牌鉴权

**Files:**
- Create: `apps/bridge/web_api.py`、`apps/bridge/token.py`、`apps/bridge/tests/test_web_api.py`
- Modify: `apps/bridge/main.py`

**Interfaces:**
- Produces：
  - `GET /mas3d/api/health` → `{ ok:true, plugin:'0.1.0', umo:'<umo>', degraded:false }`
  - `WS /mas3d/ws?token=…`：收 `{type:'say', turnId, text}`，回 `ack`→`thinking`→（可选 `intent`）→`reply`；错误回 `{type:'error', code}` 且不断连
  - `load_token(data_dir: Path) -> str`：不存在则 `secrets.token_hex(32)` 写入 `plugin_data/mas3d/token`（0o600 尽力而为）
  - `require_token(headers_or_query) -> None` 抛 `HTTPException(401)`
- `MAX_INPUT_CHARS = 300`；空白输入回 `bad_request` 且**不**写 QQ。

- [ ] **Step 1: 写失败测试** — `test_health_without_token_401`、`test_health_with_token_200`、`test_ws_bad_token_closes_401`、`test_empty_text_does_not_dispatch`、`test_token_file_created_once`（两次调用返回同值）、`test_second_turn_waits_for_first`（同一连接连发两句，第二句的 `ack` 晚于第一句的 `reply`）、`test_rate_limited_after_five_turns`（10 秒内第 6 句收到 `{type:'error', code:'rate_limited'}` 且未写入 QQ）。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 用 `context.register_web_api` 注册路由；WS 用 Starlette `WebSocket`；所有路由在同一个 `APIRouter` 上，并在注册时断言绑定主机为 `127.0.0.1`（取不到 host 时记录告警而非崩溃）。同连接回合用 `asyncio.Lock` 串行（队列深度 2，超出直接 `rate_limited`）；令牌桶 5 回合 / 10 秒、按连接维度；被限流的回合不进入管线，因此不写 QQ。
- [ ] **Step 4: 验证** — 启动 AstrBot 后：`curl -H "Authorization: Bearer $(cat token)" 127.0.0.1:6185/mas3d/api/health` 返回 200；无头请求 401；`wscat -c ws://127.0.0.1:6185/mas3d/ws?token=…` 发一句拿到 reply；连发 7 句看到第 6 句 `rate_limited`；`python -m pytest apps/bridge/tests -q` 10 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(bridge): localhost web api with token auth"`

## Task 13: HAST-SEC 代理

**Files:**
- Create: `apps/bridge/hastsec.py`、`apps/bridge/tests/test_hastsec.py`
- Modify: `apps/bridge/web_api.py`

**Interfaces:**
- Produces：`POST /mas3d/api/motion/plan` body `{text:string, turnId?:string}` → `MotionPlan`；内部 `async def plan(text:str) -> MotionPlan`：
  - `GET /v13-api/status` 探活 1.5s，失败则抛 `BackendUnavailable` 并置 60s 冷却
  - `POST /v13-api/plan`，body `{text: text[:300], context: <最近 ≤4 回合，按 `[{role:'user'|'assistant', content:string}]` 组装，单条 ≤600 字、总长 ≤1200，超出从最旧开始丢>, session: <持久 GUID>, generation: 'v15'}`，头部 `Host`、`Origin` 均为 `http://127.0.0.1:8769`，`content-type: application/json`
  - 返回经 `validatePlan` 校验，非法则抛 `ProtocolError`
- GUID 存 `plugin_data/mas3d/session.json`，形如 `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`。

- [ ] **Step 1: 写失败测试**（用本地 `http.server` 起一个 echo 服务）— `test_forwards_origin_and_host`（断言收到两个头部等于目标 URL）、`test_truncates_text_to_300`、`test_context_capped_to_4_and_1200`、`test_rejects_invalid_plan`、`test_cooldown_after_failure`（失败后 60s 内不再发起连接）。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 若 `httpx` 改写了 `Host`，回退为 `http.client.HTTPConnection` 手写请求；所有超时 2s（探活 1.5s）。
- [ ] **Step 4: 验证** — HAST-SEC 未启动时 `curl` 该端点返回 503 + `{code:'bridge_down'}`；启动 `node service/start.mjs` 后返回合法 plan；12 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(bridge): HAST-SEC plan proxy with header forging"`

## Task 14: TTS 客户端（合成、翻译、缓存、逐句流水线）

**Files:**
- Create: `apps/bridge/tts.py`、`apps/bridge/translate.py`、`apps/bridge/tests/test_tts.py`
- Modify: `apps/bridge/web_api.py`（`POST /mas3d/api/voice/synth`、`GET /mas3d/api/voice/{id}.wav`）

**Interfaces:**
- Produces：
  ```python
  async def synthesize(text: str) -> VoiceRef  # {'id','durationMs','sentenceStartsMs','englishText'}
  async def translate_zh_to_en(text: str) -> str  # 失败原样返回
  ```
  - 权重常量：`GPT_WEIGHTS='D:/GPT-SoVITS-v2pro-20250604/GPT_weights_v2ProPlus/Moniar_v2_1-e15.ckpt'`、`SOVITS_WEIGHTS='…/SoVITS_weights_v2ProPlus/Moniar_v2_1_e12_s984.pth'`，每次合成前 `GET /set_gpt_weights`+`/set_sovits_weights`
  - `/tts` 参数：`text_lang='en'`、`prompt_lang='en'`、`top_k=10`、`top_p=1`、`temperature=1`、`text_split_method='cut5'`、`repetition_penalty=1.35`、`speed_factor=1.0`、`streaming_mode=False`、`media_type='wav'`、参考音频与 `prompt_text` 用 `gsv_default_parms` 的原值
  - 缓存键 `sha256(f'{text}|{gpt}|{sovits}|{params_json}')`；LRU 500 按文件 mtime
  - 单并发 `asyncio.Semaphore(1)`，单句 30s、整轮 60s 超时

- [ ] **Step 1: 写失败测试**（stub 一个 `aiohttp`-/httpx 级 mock，返回 32kHz WAV 字节）— `test_sets_weights_before_synth`、`test_caches_by_hash`（第二次不发请求）、`test_translate_failure_falls_back`、`test_translate_reads_astrbot_config`、`test_timeout_raises_and_returns_no_audio`。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 逐句切分用 `[。！？.!?]` 保留标点；`sentenceStartsMs` 由每段 WAV 的 `data` 长度除以 32000×2 得到时长后累加。
- [ ] **Step 4: 验证** — GSV 在线时 `curl -X POST …/voice/synth -d '{"text":"今天想聊点什么？"}'` 返回 `englishText` 为英文、`durationMs>0`；杀掉 GSV 后该端点 503 且 `/mas3d/api/health` 仍 200；17 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(voice): GPT-SoVITS client with cache and sentence pipeline"`

## Task 15: 口型驱动与嘴部通道抑制

**Files:**
- Create: `apps/web/src/avatar/lipsync.ts`、`src/avatar/visemes.ts`、`__tests__/lipsync.test.ts`
- Modify: `apps/web/src/avatar/FaceDriver.ts`、`MonikaAvatar.tsx`

**Interfaces:**
- Produces：
```ts
export function envelopeToMouth(rms: number, prev: number): number; // 门限 0.02，上升 0.9/帧、下降 0.25/帧
export function wordToVisemes(word: string): Array<{ v:'A'|'I'|'U'|'E'|'O'; w:number }>;
export class LipSync { constructor(ctx: AudioContext, driver: FaceDriver); play(ref: VoiceRef, url: string): Promise<void>; stop(): void; readonly speaking: boolean }
```
- 规则（D18）：`a/ɑ→A`、`i/ɪ/y→I`、`u/ʊ/w→U`、`e/ɛ→E`、`o/ɔ→O`、闭唇与停顿→0.15 基线；说期间 `driver.setMouthSuppressed(true)`，`stop()` 后 200ms 恢复。

- [ ] **Step 1: 写失败测试**：`envelopeToMouth(0, 0.5)` 归零且有下限 0；`envelopeToMouth(0.09, 0)` 上升快于下降（对比两个方向）；`wordToVisemes('monika')` 首项 `v==='O'`；`wordToVisemes('wow')` 含 `U`。
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现** — 用 `AnalyserNode.getFloatTimeDomainData` 求 RMS；播放用 `decodeAudioData` + `BufferSource`；句子切换时按 `sentenceStartsMs` 归零 120ms；`onended` 触发 `setMouthSuppressed(false)`。
- [ ] **Step 4: 验证** — 真实一句：字幕中文、音频英文、嘴随能量开合、句间闭合、说完嘴部回到笑意（用 `faceDriver.debugInfluences()` 在控制台核对 `Fcl_MTH_Joy` 由 0.1 回到 0.4）；11 passed。
- [ ] **Step 5: Commit** — `git commit -am "feat(voice): lip sync with mouth channel ownership"`

---

## Self-Review 记录

**Spec 覆盖**：scene → Task 5；avatar/monika-runtime → Task 6/7/8；ui/dialogue-console → Task 9；bridge/astrbot-session → Task 10/11/12；motion/action-intent → Task 2/3/8；motion/hast-sec-adapter → Task 13；voice/tts-lipsync → Task 14/15；资产与许可 → Task 4 + Task 5 页脚（页脚在 Task 5 的 `App.tsx` 中实现，文案见 Global Constraints）。

**Review Focus 落点**：1 → Task 2 Step 1 第一条；2 → Task 9 Step 4 横幅；3 → Task 7 Step 4 删 VRM；4 → Task 14 Step 4 杀 GSV；5 → Task 12 Step 1 的 `test_empty_text_does_not_dispatch` 与 401 用例。

**类型一致性**：`ExpressionCue`/`ServerFrame`/`MotionPlan` 只在 Task 1 定义，Task 2/9/13 只消费；`FaceDriver` 的方法名在 Task 6 定稿，Task 7/8/15 引用同一组（`setEmotion`/`setVisemes`/`setMouthSuppressed`/`update`）。
