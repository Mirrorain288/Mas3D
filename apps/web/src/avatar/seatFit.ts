/**
 * 落座贴合：坐面高度是场景量出来的（classroom 的 HIP_ABOVE_SEAT），和模型的小腿长度
 * 没有关系。腿比标定模型长的角色，hips 对齐座面后脚会陷进地面；短的则悬空。
 *
 * 这里的做法不是整体升降模型（那样会让她浮在座面上），而是把差量折成小腿的额外弯曲：
 * hips 留在座面，膝盖多弯一点把脚抬回地面。求解用割线/牛顿混合，先由 ±probeRad
 * 两个采样估出「每弧度抬高多少米」，再迭代收敛——不假设骨骼的旋转正负方向。
 */

/** 探针弧度：用它算局部斜率，必须小到不改变姿态观感、大到不吃浮点噪声。 */
export const PROBE_RAD = 0.12;
/** 允许的残留误差（米）。2 mm 远低于屏幕像素。 */
export const TOLERANCE_M = 0.002;
/** 迭代上限：一次落座只跑一遍，多几次也无所谓。 */
export const MAX_ITERATIONS = 6;
/**
 * 膝盖额外弯曲的上下限（弧度）。下限放到 -1.1 是因为标定姿态本身把小腿留得太直：
 * 实测站立脚踝高约 0.10 m，而标定坐姿的脚踝落在地面以下约 0.02 m（脚埋进地板 10 cm，
 * 只是被课桌挡住看不见）。补到「小腿竖直、脚底贴地」需要约 -0.9 rad，仍在本姿态族内。
 */
export const MIN_DELTA_RAD = -1.1;
export const MAX_DELTA_RAD = 0.9;

export interface FitOptions {
  probeRad?: number;
  toleranceM?: number;
  iterations?: number;
  minDeltaRad?: number;
  maxDeltaRad?: number;
}

const clamp = (value: number, min: number, max: number): number => Math.min(max, Math.max(min, value));

/**
 * 求解「膝盖该多弯多少弧度」才能把脚底放回地面。
 *
 * @param measure 给定额外弧度，返回当前脚（脚踝骨骼）的世界高度；允许有副作用。
 * @param targetY 目标高度（通常是站立时的脚踝高度，即脚底贴地那一刻）。
 * @returns 需要的额外弧度；已经到底或够不到时返回被夹取后的边界值。
 */
export function solveKneeDelta(
  measure: (deltaRad: number) => number,
  targetY: number,
  options: FitOptions = {},
): number {
  const probe = options.probeRad ?? PROBE_RAD;
  const tolerance = options.toleranceM ?? TOLERANCE_M;
  const iterations = options.iterations ?? MAX_ITERATIONS;
  const minDelta = options.minDeltaRad ?? MIN_DELTA_RAD;
  const maxDelta = options.maxDeltaRad ?? MAX_DELTA_RAD;

  const slopeAt = (delta: number): number => {
    const high = measure(delta + probe);
    const low = measure(delta - probe);
    return (high - low) / (2 * probe);
  };

  const errorAt = (delta: number): number => targetY - measure(delta);

  const baseSlope = slopeAt(0);
  if (!Number.isFinite(baseSlope) || Math.abs(baseSlope) < 1e-6) {
    // 膝盖对这个模型没有垂直杠杆（骨骼缺失或已经伸直到底）：不要瞎动
    return 0;
  }

  let delta = clamp(errorAt(0) / baseSlope, minDelta, maxDelta);
  for (let i = 0; i < iterations; i += 1) {
    const error = errorAt(delta);
    if (!Number.isFinite(error) || Math.abs(error) <= tolerance) break;
    const slope = slopeAt(delta);
    if (!Number.isFinite(slope) || Math.abs(slope) < 1e-6) break;
    const next = clamp(delta + error / slope, minDelta, maxDelta);
    if (Math.abs(next - delta) < 1e-6) {
      delta = next;
      break;
    }
    delta = next;
  }
  return delta;
}
