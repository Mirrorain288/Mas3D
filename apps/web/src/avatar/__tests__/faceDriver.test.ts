import { describe, expect, it } from 'vitest';
import { FaceDriver, resolveFaceMap, type FaceMap } from '../faceDriver';

const testMap: FaceMap = {
  version: 1,
  targets: { Fcl_ALL_Joy: 0, Fcl_MTH_Joy: 1, Fcl_MTH_A: 2 },
  presets: {
    neutral: [],
    joy: [
      { target: 'Fcl_ALL_Joy', weight: 0.8 },
      { target: 'Fcl_MTH_Joy', weight: 0.4 },
    ],
    fun: [],
    sorrow: [],
    angry: [],
    surprised: [],
  },
};

function make(meshCount = 1) {
  const meshes = Array.from({ length: meshCount }, () => ({ morphTargetInfluences: [0, 0, 0] }));
  return { meshes, driver: new FaceDriver(meshes, testMap) };
}

describe('FaceDriver', () => {
  it('writes preset weights straight to morphTargetInfluences', () => {
    const { meshes, driver } = make();
    driver.setEmotion('joy', 1);
    expect(meshes[0]!.morphTargetInfluences![0]).toBeCloseTo(0.8);
    expect(meshes[0]!.morphTargetInfluences![1]).toBeCloseTo(0.4);
  });

  it('scales by intensity and clamps it to 0..1', () => {
    const { meshes, driver } = make();
    driver.setEmotion('joy', 0.5);
    expect(meshes[0]!.morphTargetInfluences![0]).toBeCloseTo(0.4);
    driver.setEmotion('joy', 3);
    expect(meshes[0]!.morphTargetInfluences![0]).toBeCloseTo(0.8);
  });

  it('zeroes previous targets when switching to an emotion whose binds are missing', () => {
    const { meshes, driver } = make();
    driver.setEmotion('joy', 1);
    expect(() => driver.setEmotion('sorrow', 1)).not.toThrow();
    expect(meshes[0]!.morphTargetInfluences![0]).toBe(0);
    expect(meshes[0]!.morphTargetInfluences![1]).toBe(0);
  });

  it('suppresses the expression mouth channel to a quarter', () => {
    const { meshes, driver } = make();
    driver.setEmotion('joy', 1);
    driver.setMouthSuppressed(true);
    expect(meshes[0]!.morphTargetInfluences![1]).toBeCloseTo(0.1);
    expect(meshes[0]!.morphTargetInfluences![0]).toBeCloseTo(0.8);
    driver.setMouthSuppressed(false);
    expect(meshes[0]!.morphTargetInfluences![1]).toBeCloseTo(0.4);
  });

  it('ramps visemes toward the requested vowel over time and beats the expression layer', () => {
    const { meshes, driver } = make();
    driver.setEmotion('joy', 1);
    driver.setVisemes({ A: 1 });
    driver.update(16);
    const early = meshes[0]!.morphTargetInfluences![2]!;
    expect(early).toBeGreaterThan(0);
    expect(early).toBeLessThan(1);
    driver.update(500);
    expect(meshes[0]!.morphTargetInfluences![2]).toBeCloseTo(1);
    driver.setVisemes({});
    driver.update(1000);
    expect(meshes[0]!.morphTargetInfluences![2]).toBe(0);
  });

  it('counts targets that the avatar does not actually have', () => {
    const meshes = [{ morphTargetInfluences: [0] }];
    const driver = new FaceDriver(meshes, testMap);
    driver.setEmotion('joy', 1);
    expect(driver.degradedCount).toBeGreaterThan(0);
  });

  it('ignores meshes without morphTargetInfluences instead of throwing', () => {
    const driver = new FaceDriver([{}], testMap);
    expect(() => driver.setEmotion('joy', 1)).not.toThrow();
    expect(driver.degradedCount).toBeGreaterThan(0);
  });
});

describe('resolveFaceMap', () => {
  const fallback: FaceMap = { version: 1, targets: { fallback: 0 }, presets: testMap.presets };

  it('reads targetNames from primitive extras and keeps fallback presets', () => {
    const gltf = {
      meshes: [
        { primitives: [{ extras: { targetNames: ['A', 'B'] } }] },
        { primitives: [{ extras: { targetNames: ['x', 'y', 'z'] } }] },
      ],
    };
    const map = resolveFaceMap(gltf, fallback);
    expect(map.targets).toEqual({ x: 0, y: 1, z: 2 });
    expect(map.meshIndex).toBe(1);
    expect(map.presets).toBe(fallback.presets);
  });

  it('falls back when the gltf has no targetNames at all', () => {
    expect(resolveFaceMap({ meshes: [{ primitives: [{}] }] }, fallback)).toBe(fallback);
    expect(resolveFaceMap(null, fallback)).toBe(fallback);
  });
});
