import type { Gaze } from '@mas3d/protocol';

/** 只依赖坐标三元组，便于单测（不引入 three）。 */
export interface Vec3Like {
  x: number;
  y: number;
  z: number;
}

export interface GazeAngles {
  /** 水平偏转，正值朝 +x。 */
  yaw: number;
  /** 俯仰，正值低头。 */
  pitch: number;
}

/** 眼球骨的机械极限（design D7：超出即夹取，避免眼珠翻白）。 */
export const GAZE_LIMIT = (35 * Math.PI) / 180;

const AWAY_YAW = 0.52; // 29.8°，满足「移开视线 ≥25°」
const DOWN_PITCH = 0.34;
const UP_PITCH = -0.34;
const SIDE_YAW = 0.45;

function clampAngle(value: number): number {
  return Math.min(GAZE_LIMIT, Math.max(-GAZE_LIMIT, value));
}

/**
 * 把「看向哪里」换算成 yaw/pitch。该 VRM 的 VRM.firstPerson.lookAt 为空（hasLookAt:false），
 * 所以这些角度由调用方直接写到 leftEye/rightEye 骨骼，并叠加 head/neck。
 */
export function gazeTarget(cam: Vec3Like, head: Vec3Like, mode: Gaze): GazeAngles {
  const dx = cam.x - head.x;
  const dy = cam.y - head.y;
  const dz = cam.z - head.z;
  const flat = Math.hypot(dx, dz);
  const baseYaw = flat > 1e-6 ? Math.atan2(dx, dz) : 0;
  const basePitch = flat > 1e-6 ? -Math.atan2(dy, flat) : 0;

  let yaw = baseYaw;
  let pitch = basePitch;
  switch (mode) {
    case 'away':
      yaw = baseYaw >= 0 ? AWAY_YAW : -AWAY_YAW;
      pitch = -0.06;
      break;
    case 'down':
      pitch = basePitch + DOWN_PITCH;
      break;
    case 'up':
      pitch = basePitch + UP_PITCH;
      break;
    case 'left':
      yaw = -SIDE_YAW;
      break;
    case 'right':
      yaw = SIDE_YAW;
      break;
    case 'player':
    default:
      break;
  }
  return { yaw: clampAngle(yaw), pitch: clampAngle(pitch) };
}
