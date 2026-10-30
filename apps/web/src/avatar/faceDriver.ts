import type { Emotion } from '@mas3d/protocol';
import rawFaceMap from './face.map.json';

/** 一条表情绑定：VRoid 形变名 + 绝对权重。 */
export interface FaceBind {
  target: string;
  weight: number;
}

export type EmotionPresets = Record<Emotion, FaceBind[]>;

/**
 * 面部映射表（design D15）。`targets` 把 VRoid 形变名映射到 morphTargetInfluences 下标，
 * `meshIndex` 指出这些形变挂在第几个 mesh 上（本 VRM 是 0 号面部网格）。
 */
export interface FaceMap {
  version: 1;
  meshIndex?: number;
  targets: Record<string, number>;
  presets: EmotionPresets;
}

/** three.js Mesh 里我们唯一依赖的形状。 */
export interface MorphMesh {
  morphTargetInfluences?: number[] | Float32Array;
}

export const VOWELS = ['A', 'I', 'U', 'E', 'O'] as const;
export type Vowel = (typeof VOWELS)[number];

/** 说话时表情层写入口型通道时的缩放（design D18/D19）。 */
export const MOUTH_SUPPRESSION = 0.25;
export const VISEME_ATTACK_MS = 40;
export const VISEME_RELEASE_MS = 120;

/** 由 tools/extract-face-map.mjs 生成，作为运行时读不到 glTF 名称时的兜底。 */
export const FALLBACK_FACE_MAP = rawFaceMap as unknown as FaceMap;

function clamp01(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.min(1, Math.max(0, value));
}

/**
 * 从 glTF JSON 读取真实形变名单。本 VRM 的 VRM.blendShapeGroups 全部 binds 为空，
 * 只有 meshes[*].primitives[*].extras.targetNames 可靠，故以此为准。
 */
export function resolveFaceMap(gltfJson: unknown, fallback: FaceMap): FaceMap {
  const meshes = (gltfJson as { meshes?: unknown } | null | undefined)?.meshes;
  if (!Array.isArray(meshes)) return fallback;

  let bestMeshIndex = -1;
  let bestNames: string[] = [];
  for (let meshIndex = 0; meshIndex < meshes.length; meshIndex += 1) {
    const primitives = (meshes[meshIndex] as { primitives?: unknown } | undefined)?.primitives;
    if (!Array.isArray(primitives)) continue;
    for (const primitive of primitives) {
      const names = (primitive as { extras?: { targetNames?: unknown } } | undefined)?.extras?.targetNames;
      if (!Array.isArray(names)) continue;
      const strings = names.filter((name): name is string => typeof name === 'string');
      if (strings.length > bestNames.length) {
        bestNames = strings;
        bestMeshIndex = meshIndex;
      }
    }
  }
  if (bestNames.length === 0) return fallback;

  const targets: Record<string, number> = {};
  bestNames.forEach((name, index) => {
    if (!(name in targets)) targets[name] = index;
  });
  return { version: 1, meshIndex: bestMeshIndex, targets, presets: fallback.presets };
}

/**
 * 直接写 mesh.morphTargetInfluences 的面部驱动器。
 *
 * 两个图层：表情层（回合级、离散，setEmotion 立即生效）与口型层（逐帧、连续，
 * setVisemes 只记录目标值，由 update 做攻击/释放插值）。合成规则为同名取最大，
 * 再整体写回映射表里的每个下标（未命中的下标归零）。
 */
export class FaceDriver {
  private readonly meshes: MorphMesh[];
  private readonly map: FaceMap;
  private readonly maxIndex: number;
  private readonly mesh: MorphMesh | null;
  private readonly visemeTargets: Record<Vowel, number> = { A: 0, I: 0, U: 0, E: 0, O: 0 };
  private readonly visemeCurrent: Record<Vowel, number> = { A: 0, I: 0, U: 0, E: 0, O: 0 };
  private emotion: Emotion = 'neutral';
  private intensity = 0;
  private suppressed = false;
  private degraded = 0;

  constructor(meshes: MorphMesh[], map: FaceMap) {
    this.meshes = meshes;
    this.map = map;
    const indices = Object.values(map.targets);
    this.maxIndex = indices.length > 0 ? Math.max(...indices) : -1;
    this.mesh = this.pickMesh();
  }

  get degradedCount(): number {
    return this.degraded;
  }

  setEmotion(emotion: Emotion, intensity: number): void {
    this.emotion = emotion;
    this.intensity = clamp01(intensity);
    this.apply();
  }

  /** 传空对象表示本帧没有任何元音（全部归零）。 */
  setVisemes(visemes: Partial<Record<Vowel, number>>): void {
    for (const vowel of VOWELS) {
      this.visemeTargets[vowel] = clamp01(visemes[vowel] ?? 0);
    }
  }

  setMouthSuppressed(on: boolean): void {
    if (this.suppressed === on) return;
    this.suppressed = on;
    this.apply();
  }

  update(dtMs: number): void {
    const dt = Number.isFinite(dtMs) ? Math.max(0, dtMs) : 0;
    for (const vowel of VOWELS) {
      const target = this.visemeTargets[vowel];
      const current = this.visemeCurrent[vowel];
      if (current === target) continue;
      const raising = target > current;
      const span = raising ? VISEME_ATTACK_MS : VISEME_RELEASE_MS;
      const step = span > 0 ? dt / span : 1;
      this.visemeCurrent[vowel] = raising
        ? Math.min(target, current + step)
        : Math.max(target, current - step);
    }
    this.apply();
  }

  dispose(): void {
    this.meshes.length = 0;
  }

  private pickMesh(): MorphMesh | null {
    const declared = this.map.meshIndex;
    if (declared !== undefined) {
      const candidate = this.meshes[declared];
      if (candidate?.morphTargetInfluences) return candidate;
    }
    for (const candidate of this.meshes) {
      const influences = candidate.morphTargetInfluences;
      if (influences && influences.length > this.maxIndex) return candidate;
    }
    return null;
  }

  private apply(): void {
    this.degraded = 0;
    const weights = new Map<number, number>();
    const binds = this.map.presets[this.emotion] ?? [];
    for (const bind of binds) {
      const index = this.map.targets[bind.target];
      if (index === undefined) {
        this.degraded += 1;
        continue;
      }
      let weight = clamp01(bind.weight * this.intensity);
      if (this.suppressed && bind.target.startsWith('Fcl_MTH_')) weight *= MOUTH_SUPPRESSION;
      weights.set(index, Math.max(weights.get(index) ?? 0, weight));
    }
    for (const vowel of VOWELS) {
      const index = this.map.targets['Fcl_MTH_' + vowel];
      const weight = this.visemeCurrent[vowel];
      if (index === undefined) {
        if (weight > 0) this.degraded += 1;
        continue;
      }
      weights.set(index, Math.max(weights.get(index) ?? 0, weight));
    }

    const influences = this.mesh?.morphTargetInfluences;
    if (!influences) {
      if (weights.size > 0) this.degraded += weights.size;
      return;
    }
    for (const index of Object.values(this.map.targets)) {
      if (index < influences.length) influences[index] = weights.get(index) ?? 0;
    }
  }
}
