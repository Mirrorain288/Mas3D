import { describe, expect, it } from 'vitest';
import { Arbiter, GESTURE_DURATION, sampleGesture, ZERO_DELTA } from '../src/index';

describe('Arbiter', () => {
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
    expect(s.delta).toEqual({ headPitch: 0, headYaw: 0, headRoll: 0, spineBend: 0, armRaiseL: 0, armRaiseR: 0 });
  });

  it('starts and ends a clip at zero', () => {
    expect(sampleGesture('nod', 0, 1).headPitch).toBe(0);
    expect(sampleGesture('nod', GESTURE_DURATION.nod, 1).headPitch).toBe(0);
  });
});
