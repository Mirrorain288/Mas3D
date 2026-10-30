/**
 * Tier 0 眨眼的确定性调度（design D7）。
 *
 * 这个 VRM 没有任何动画片段，也没有 VRM lookAt，因此呼吸/眨眼/视线全部程序生成。
 * 间隔 2.5–6 s，眼睑闭合 90 ms，全部走 mulberry32 伪随机以便测试与回放。
 */
export const BLINK_MIN_MS = 2500;
export const BLINK_MAX_MS = 6000;
export const BLINK_CLOSE_MS = 90;

/** 32 位确定性伪随机数发生器，返回 [0,1)。 */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** 下一次眨眼的时间戳：now + [2500,6000] ms，rnd 越界会被夹回。 */
export function nextBlink(nowMs: number, rnd: () => number): number {
  const raw = rnd();
  const clamped = Number.isFinite(raw) ? Math.min(1, Math.max(0, raw)) : 0;
  return nowMs + BLINK_MIN_MS + clamped * (BLINK_MAX_MS - BLINK_MIN_MS);
}

/** 一次性预生成若干眨眼间隔，便于断言确定性。 */
export function blinkSchedule(seed: number, count = 12): number[] {
  const rnd = mulberry32(seed);
  const out: number[] = [];
  for (let i = 0; i < count; i += 1) out.push(BLINK_MIN_MS + rnd() * (BLINK_MAX_MS - BLINK_MIN_MS));
  return out;
}
