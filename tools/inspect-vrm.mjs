#!/usr/bin/env node
// 换模型前的兼容性体检：把一个候选 VRM 与我们运行时依赖的骨架 / 形变 / 表情对一遍。
// 用法: node tools/inspect-vrm.mjs <model.vrm> [--json]
// 只读 GLB 的 JSON chunk，不加载 three，不碰网络。
import { readFileSync } from 'node:fs';

const GLB_MAGIC = 0x46546c67;
const CHUNK_JSON = 0x4e4f534a;

export const REQUIRED_BONES = [
  'hips', 'spine', 'chest', 'upperChest', 'neck', 'head',
  'leftEye', 'rightEye',
  'leftUpperArm', 'rightUpperArm', 'leftLowerArm', 'rightLowerArm', 'leftHand', 'rightHand',
  'leftUpperLeg', 'rightUpperLeg', 'leftLowerLeg', 'rightLowerLeg', 'leftFoot', 'rightFoot',
];

export const REQUIRED_TARGETS = [
  'Fcl_MTH_A', 'Fcl_MTH_I', 'Fcl_MTH_U', 'Fcl_MTH_E', 'Fcl_MTH_O', 'Fcl_EYE_Close',
  'Fcl_ALL_Joy', 'Fcl_ALL_Fun', 'Fcl_ALL_Sorrow', 'Fcl_ALL_Angry', 'Fcl_ALL_Surprised',
];

export const VRM1_EXPRESSION_EQUIVALENTS = {
  'Fcl_MTH_A': 'aa', 'Fcl_MTH_I': 'ih', 'Fcl_MTH_U': 'ou', 'Fcl_MTH_E': 'ee', 'Fcl_MTH_O': 'oh',
  'Fcl_EYE_Close': 'blink',
  'Fcl_ALL_Joy': 'happy', 'Fcl_ALL_Fun': 'relaxed', 'Fcl_ALL_Sorrow': 'sad',
  'Fcl_ALL_Angry': 'angry', 'Fcl_ALL_Surprised': 'surprised',
};

export function readGlbJson(buf) {
  if (buf.readUInt32LE(0) !== GLB_MAGIC) throw new Error('不是 GLB 文件（magic 不匹配）');
  let offset = 12;
  while (offset + 8 <= buf.length) {
    const len = buf.readUInt32LE(offset);
    const type = buf.readUInt32LE(offset + 4);
    const start = offset + 8;
    if (type === CHUNK_JSON) return JSON.parse(buf.subarray(start, start + len).toString('utf8'));
    offset = start + len;
  }
  throw new Error('GLB 中找不到 JSON chunk');
}

function humanBones(gltf) {
  const vrm0 = gltf.extensions?.VRM?.humanoid?.humanBones;
  const vrm1 = gltf.extensions?.VRMC_vrm?.humanoid?.humanBones;
  const bones = {};
  const collect = (list) => {
    if (!list) return;
    if (Array.isArray(list)) {
      for (const entry of list) if (entry?.bone) bones[entry.bone] = entry.node;
    } else {
      for (const [name, entry] of Object.entries(list)) bones[name] = entry?.node;
    }
  };
  collect(vrm0);
  collect(vrm1);
  return bones;
}

function faceTargets(gltf) {
  let best = [];
  for (const mesh of gltf.meshes ?? []) {
    for (const prim of mesh.primitives ?? []) {
      const names = prim.extras?.targetNames;
      if (Array.isArray(names) && names.length > best.length) best = names;
    }
  }
  return best;
}

function expressions(gltf) {
  const out = { custom: [], presets: [] };
  const vrm1 = gltf.extensions?.VRMC_vrm?.expressions;
  if (vrm1) {
    out.presets = Object.keys(vrm1.preset ?? {}).filter((k) => k !== 'unknown');
    out.custom = Object.keys(vrm1.custom ?? {});
  }
  const vrm0 = gltf.extensions?.VRM?.blendShapeMaster?.blendShapeGroups;
  if (Array.isArray(vrm0)) {
    for (const group of vrm0) {
      if (group?.presetName && group.presetName !== 'unknown') out.presets.push(group.presetName);
      if (group?.name) out.custom.push(group.name);
    }
  }
  return out;
}

function roughHeight(gltf) {
  const accessors = gltf.accessors ?? [];
  let min = Infinity;
  let max = -Infinity;
  for (const mesh of gltf.meshes ?? []) {
    for (const prim of mesh.primitives ?? []) {
      const pos = prim.attributes?.POSITION;
      if (typeof pos !== 'number') continue;
      const acc = accessors[pos];
      if (!acc?.min || !acc?.max) continue;
      min = Math.min(min, acc.min[1]);
      max = Math.max(max, acc.max[1]);
    }
  }
  return Number.isFinite(min) && Number.isFinite(max) ? Number((max - min).toFixed(3)) : null;
}

export function inspect(gltf) {
  const targets = faceTargets(gltf);
  const bones = humanBones(gltf);
  const expr = expressions(gltf);
  const version = gltf.extensions?.VRMC_vrm ? '1' : gltf.extensions?.VRM ? '0' : 'unknown';
  const missingBones = REQUIRED_BONES.filter((name) => !(name in bones));
  const missingTargets = REQUIRED_TARGETS.filter((name) => !targets.includes(name));
  const equivalents = missingTargets.map((name) => [name, VRM1_EXPRESSION_EQUIVALENTS[name] ?? null]);
  const usableEquivalents = equivalents.filter(([, alias]) => alias
    && (expr.presets.includes(alias) || expr.custom.includes(alias)));
  return {
    version,
    title: gltf.extensions?.VRMC_vrm?.meta?.name ?? gltf.extensions?.VRM?.meta?.title ?? null,
    faceTargetCount: targets.length,
    faceTargets: targets,
    heightsApproxM: roughHeight(gltf),
    meshes: (gltf.meshes ?? []).length,
    materials: (gltf.materials ?? []).length,
    bones: Object.keys(bones).length,
    missingBones,
    missingTargets,
    expressionPresets: [...new Set(expr.presets)],
    expressionCustom: [...new Set(expr.custom)],
    equivalentAliases: usableEquivalents,
    verdict: missingBones.length === 0 && missingTargets.length === 0
      ? 'ready'
      : usableEquivalents.length === missingTargets.length && missingBones.length === 0
        ? 'needs-alias-map'
        : 'needs-port',
  };
}

function main() {
  const path = process.argv[2];
  if (!path) {
    console.error('用法: node tools/inspect-vrm.mjs <model.vrm>');
    process.exit(2);
  }
  const report = inspect(readGlbJson(readFileSync(path)));
  console.log(JSON.stringify(report, null, 2));
}

main();
