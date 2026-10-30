import type { Gesture } from '@mas3d/protocol';
import { GESTURE_DURATION, PREEMPT_FADE_MS, PREEMPT_MARGIN } from './constants';
import { blendDelta, sampleGesture, usesArms, ZERO_DELTA, type PoseDelta } from './gestures';

export interface Clip {
  gesture: Gesture;
  intensity: number;
  startMs: number;
}

export interface ArbiterSample {
  gesture: Gesture;
  intensity: number;
  delta: PoseDelta;
}

function clamp01(v: number): number {
  return Math.min(1, Math.max(0, v));
}

/**
 * Tier1 预制动作的仲裁器：同一时刻只播一个剪辑，按强度抢占，Tier2 占用手臂时挡下用手臂的 Tier1。
 * 时间由调用方注入（nowMs），因此可确定性测试。
 */
export class Arbiter {
  private current: Clip | null = null;
  private previous: Clip | null = null;
  private preemptAtMs: number | null = null;
  private armsOwnedByTier2 = false;
  private armsOwnedSinceMs = 0;

  private live(nowMs: number): Clip | null {
    const cur = this.current;
    if (!cur) return null;
    return nowMs >= cur.startMs && nowMs < cur.startMs + GESTURE_DURATION[cur.gesture] ? cur : null;
  }

  request(gesture: Gesture, intensity: number, nowMs: number): boolean {
    if (gesture === 'none' || intensity <= 0) return false;
    if (this.armsOwnedByTier2 && usesArms(gesture)) return false;
    const cur = this.live(nowMs);
    if (cur && intensity <= cur.intensity + PREEMPT_MARGIN) return false;
    if (cur) {
      this.previous = cur;
      this.preemptAtMs = nowMs;
    }
    this.current = { gesture, intensity: clamp01(intensity), startMs: nowMs };
    return true;
  }

  setArmOwnedByTier2(owned: boolean, nowMs: number): void {
    if (owned && !this.armsOwnedByTier2) {
      this.armsOwnedSinceMs = nowMs;
      const cur = this.current;
      if (cur && usesArms(cur.gesture)) {
        this.previous = cur;
        this.preemptAtMs = nowMs;
        this.current = null;
      }
    }
    this.armsOwnedByTier2 = owned;
  }

  get armsOwnedSince(): number {
    return this.armsOwnedSinceMs;
  }

  sample(nowMs: number): ArbiterSample {
    const cur = this.live(nowMs);
    const base = cur ? sampleGesture(cur.gesture, nowMs - cur.startMs, cur.intensity) : { ...ZERO_DELTA };
    let delta = base;
    if (this.preemptAtMs !== null) {
      const elapsed = nowMs - this.preemptAtMs;
      if (elapsed < PREEMPT_FADE_MS && this.previous) {
        const outgoing = sampleGesture(this.previous.gesture, nowMs - this.previous.startMs, this.previous.intensity);
        delta = blendDelta(outgoing, base, elapsed / PREEMPT_FADE_MS);
      } else if (elapsed >= PREEMPT_FADE_MS) {
        this.previous = null;
        this.preemptAtMs = null;
      }
    }
    return { gesture: cur?.gesture ?? 'none', intensity: cur?.intensity ?? 0, delta };
  }
}
