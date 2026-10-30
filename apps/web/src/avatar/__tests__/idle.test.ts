import { describe, expect, it } from 'vitest';
import { BLINK_CLOSE_MS, BLINK_MAX_MS, BLINK_MIN_MS, blinkSchedule, nextBlink } from '../idle';

describe('blinkSchedule', () => {
  it('keeps the first interval inside 2.5–6 s', () => {
    const first = blinkSchedule(1)[0]!;
    expect(first).toBeGreaterThanOrEqual(BLINK_MIN_MS);
    expect(first).toBeLessThanOrEqual(BLINK_MAX_MS);
  });

  it('is deterministic for the same seed and differs across seeds', () => {
    expect(blinkSchedule(7)).toEqual(blinkSchedule(7));
    expect(blinkSchedule(7)).not.toEqual(blinkSchedule(8));
  });

  it('produces the requested number of intervals', () => {
    expect(blinkSchedule(3, 5)).toHaveLength(5);
  });

  it('exposes a 90 ms eyelid close', () => {
    expect(BLINK_CLOSE_MS).toBe(90);
  });
});

describe('nextBlink', () => {
  it('returns now plus an interval drawn from rnd', () => {
    expect(nextBlink(1000, () => 0)).toBe(1000 + BLINK_MIN_MS);
    expect(nextBlink(1000, () => 1)).toBe(1000 + BLINK_MAX_MS);
  });

  it('clamps a misbehaving rnd to the legal range', () => {
    expect(nextBlink(0, () => 5)).toBe(BLINK_MAX_MS);
    expect(nextBlink(0, () => -3)).toBe(BLINK_MIN_MS);
  });
});
