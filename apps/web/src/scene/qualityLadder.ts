export type Quality = "low" | "medium" | "high";

/** 低于这个帧率就降一级 */
export const FPS_FLOOR = 45;
/** 高于这个帧率并持续 3 秒才恢复一级（见 Scene 的 sustained 计时） */
export const FPS_CEILING = 55;

const ORDER: readonly Quality[] = ["low", "medium", "high"] as const;

/**
 * 纯函数：掉帧降一级、帧率富余升一级、两端都不越界。
 * 只回答「下一档是什么」，何时调用由 Scene 决定（恢复要连续 3 秒）。
 */
export function nextQuality(current: Quality, fps: number): Quality {
  const index = ORDER.indexOf(current);
  if (fps < FPS_FLOOR) return ORDER[Math.max(0, index - 1)] as Quality;
  if (fps >= FPS_CEILING) return ORDER[Math.min(ORDER.length - 1, index + 1)] as Quality;
  return current;
}

export interface QualitySettings {
  /** devicePixelRatio 的上限 */
  pixelRatio: number;
  shadows: boolean;
  /** 环境贴图（天空盒）边长 */
  skySize: 1024 | 2048;
}

export function qualitySettings(quality: Quality): QualitySettings {
  switch (quality) {
    case "high":
      return { pixelRatio: 2, shadows: true, skySize: 2048 };
    case "medium":
      return { pixelRatio: 1.5, shadows: false, skySize: 2048 };
    case "low":
      return { pixelRatio: 1, shadows: false, skySize: 1024 };
  }
}
