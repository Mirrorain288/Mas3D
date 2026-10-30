/** 玩家可调的本地设置：只存浏览器，不进 AstrBot，也不随导出泄漏令牌。 */
export interface Settings {
  /** 语音音量 0..1（Task 13 的 TTS 播放使用）。 */
  volume: number;
  /** 视线跟随倍率 0.5..1.5：越大越容易被她盯住。 */
  gazeSensitivity: number;
  /** 动作增强倍率 0..1.5：乘在 Tier1 手势强度上。 */
  gestureBoost: number;
  /** 相机拖动/滚轮灵敏度 0.4..2。 */
  cameraSensitivity: number;
  /** 只显示字幕、不播放语音（TTS 不可用时的降级开关）。 */
  subtitlesOnly: boolean;
}

export const SETTINGS_KEY = "mas3d.settings";

export const DEFAULT_SETTINGS: Settings = {
  volume: 0.8,
  gazeSensitivity: 1,
  gestureBoost: 1,
  cameraSensitivity: 1,
  subtitlesOnly: false,
};

const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, Number.isFinite(v) ? v : lo));

/** 把任意来源（旧版本 localStorage、手改的 JSON）收敛成合法设置。 */
export function normalizeSettings(raw: unknown): Settings {
  const src = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const num = (key: keyof Settings, lo: number, hi: number): number =>
    typeof src[key] === "number" ? clamp(src[key] as number, lo, hi) : (DEFAULT_SETTINGS[key] as number);
  return {
    volume: num("volume", 0, 1),
    gazeSensitivity: num("gazeSensitivity", 0.5, 1.5),
    gestureBoost: num("gestureBoost", 0, 1.5),
    cameraSensitivity: num("cameraSensitivity", 0.4, 2),
    subtitlesOnly: typeof src.subtitlesOnly === "boolean" ? src.subtitlesOnly : DEFAULT_SETTINGS.subtitlesOnly,
  };
}

export function loadSettings(): Settings {
  try {
    const raw = globalThis.localStorage?.getItem(SETTINGS_KEY);
    if (!raw) return { ...DEFAULT_SETTINGS };
    return normalizeSettings(JSON.parse(raw));
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

export function saveSettings(settings: Settings): boolean {
  try {
    globalThis.localStorage?.setItem(SETTINGS_KEY, JSON.stringify(settings));
    return true;
  } catch {
    return false;
  }
}
