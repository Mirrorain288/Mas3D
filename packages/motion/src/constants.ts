import type { Gesture } from '@mas3d/protocol';
import type { PoseDelta } from './gestures';

export const GESTURE_DURATION: Record<Gesture, number> = {
  none: 0,
  nod: 900,
  shake: 800,
  tilt: 1200,
  wave: 1600,
  lean_in: 1400,
  look_away: 1500,
};

/** 抢占时旧动作的淡出时间。 */
export const PREEMPT_FADE_MS = 150;
/** 同级抢占所需的强度领先量：新意图必须比当前强这么多才允许打断。 */
export const PREEMPT_MARGIN = 0.2;
/** 任何单次表现的最长播放时长，防止角色僵在单一姿态。 */
export const MAX_HOLD_MS = 20000;

/** 每个手势在峰值处的骨骼偏移量（弧度）。 */
export const GESTURE_AMPLITUDE: Record<Gesture, PoseDelta> = {
  none: { headPitch: 0, headYaw: 0, headRoll: 0, spineBend: 0, armRaiseL: 0, armRaiseR: 0 },
  nod: { headPitch: 0.17, headYaw: 0, headRoll: 0, spineBend: 0.03, armRaiseL: 0, armRaiseR: 0 },
  shake: { headPitch: 0, headYaw: 0.22, headRoll: 0, spineBend: 0, armRaiseL: 0, armRaiseR: 0 },
  tilt: { headPitch: 0, headYaw: 0, headRoll: 0.28, spineBend: 0, armRaiseL: 0, armRaiseR: 0 },
  wave: { headPitch: -0.04, headYaw: 0, headRoll: 0.08, spineBend: 0, armRaiseL: 0, armRaiseR: 0.9 },
  lean_in: { headPitch: 0.1, headYaw: 0, headRoll: 0, spineBend: 0.14, armRaiseL: 0, armRaiseR: 0 },
  look_away: { headPitch: 0, headYaw: 0.34, headRoll: 0.06, spineBend: 0, armRaiseL: 0, armRaiseR: 0 },
};
