# @mas3d/motion

表现层的纯逻辑：把一段回复文本收敛成「情绪 / 注视 / 手势」，再把有限的手势播放成骨骼偏移。

## 三层结构（design D7）

| 层 | 负责 | 实现位置 |
| --- | --- | --- |
| Tier 0 | 呼吸、眨眼、眼球视线等持续待机 | `apps/web`（依赖 three-vrm 与时钟） |
| Tier 1 | 点头 / 摇头 / 歪头 / 挥手 / 前倾 / 移开 | `gestures.ts` + `arbiter.ts`（本包） |
| Tier 2 | HAST-SEC 的手臂意图 | `apps/bridge` 代理 + `arbiter.setArmOwnedByTier2` |

## 规则

- `deriveCue(text)` 是纯函数：先剥离引号内的转述，再按词表打分；同一输入永远得到同一结果。
- `sampleGesture` 的两端严格为 0，因此任何手势都能无缝接回待机。
- `Arbiter` 的抢占条件：新意图强度 > 当前强度 + `PREEMPT_MARGIN`(0.2)；抢占时旧动作在 `PREEMPT_FADE_MS`(150ms) 内淡出。
- Tier 2 占用手臂期间，使用手臂的 Tier 1 手势被拒绝；纯头部手势仍可播放。
- 时间一律由调用方注入 `nowMs`，本包不读时钟，便于单测。

## 测试

```bash
pnpm --filter @mas3d/motion test
```
