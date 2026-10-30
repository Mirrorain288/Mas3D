import type { Gesture } from '@mas3d/protocol';
import { GESTURE_AMPLITUDE, GESTURE_DURATION } from './constants';

export interface PoseDelta {
  headPitch: number;
  headYaw: number;
  headRoll: number;
  spineBend: number;
  armRaiseL: number;
  armRaiseR: number;
}

export const ZERO_DELTA: PoseDelta = { headPitch: 0, headYaw: 0, headRoll: 0, spineBend: 0, armRaiseL: 0, armRaiseR: 0 };

function easeInOutCubic(u: number): number {
  return u < 0.5 ? 4 * u * u * u : 1 - Math.pow(-2 * u + 2, 3) / 2;
}

function clamp01(v: number): number {
  return Math.min(1, Math.max(0, v));
}

/** 采样手势在 tMs 时刻的骨骼偏移；两端严格为 0，越界返回静止。 */
export function sampleGesture(g: Gesture, tMs: number, intensity: number): PoseDelta {
  const duration = GESTURE_DURATION[g];
  if (duration <= 0 || tMs <= 0 || tMs >= duration) return { ...ZERO_DELTA };
  const u = tMs / duration;
  const envelope = Math.sin(Math.PI * u) * easeInOutCubic(u);
  const gain = envelope * clamp01(intensity);
  const amp = GESTURE_AMPLITUDE[g];
  return {
    headPitch: amp.headPitch * gain,
    headYaw: amp.headYaw * gain,
    headRoll: amp.headRoll * gain,
    spineBend: amp.spineBend * gain,
    armRaiseL: amp.armRaiseL * gain,
    armRaiseR: amp.armRaiseR * gain,
  };
}

export function blendDelta(from: PoseDelta, to: PoseDelta, t: number): PoseDelta {
  const k = clamp01(t);
  return {
    headPitch: from.headPitch + (to.headPitch - from.headPitch) * k,
    headYaw: from.headYaw + (to.headYaw - from.headYaw) * k,
    headRoll: from.headRoll + (to.headRoll - from.headRoll) * k,
    spineBend: from.spineBend + (to.spineBend - from.spineBend) * k,
    armRaiseL: from.armRaiseL + (to.armRaiseL - from.armRaiseL) * k,
    armRaiseR: from.armRaiseR + (to.armRaiseR - from.armRaiseR) * k,
  };
}

export function usesArms(g: Gesture): boolean {
  const amp = GESTURE_AMPLITUDE[g];
  return amp.armRaiseL !== 0 || amp.armRaiseR !== 0;
}
