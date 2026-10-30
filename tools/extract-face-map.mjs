#!/usr/bin/env node
// 从 VRM(GLb) 中提取面部 morph target 名单，生成 face.map.json（入库）。
// 用法: node tools/extract-face-map.mjs [vrmPath] [outPath]
// 依据 design D15：该 VRM 的 VRM.blendShapeGroups 全部 binds 为空，
// 因此表情只能按 meshes[*].primitives[*].extras.targetNames 的 VRoid 名称直写 morphTargetInfluences。
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';

const GLB_MAGIC = 0x46546c67; // 'glTF'
const CHUNK_JSON = 0x4e4f534a; // 'JSON'

export const EMOTION_PRESETS = {
  neutral: [],
  joy: [
    { target: 'Fcl_ALL_Joy', weight: 0.8 },
    { target: 'Fcl_EYE_Joy', weight: 0.5 },
    { target: 'Fcl_MTH_Joy', weight: 0.4 },
  ],
  fun: [{ target: 'Fcl_ALL_Fun', weight: 0.9 }],
  sorrow: [
    { target: 'Fcl_ALL_Sorrow', weight: 0.8 },
    { target: 'Fcl_BRW_Sorrow', weight: 0.5 },
  ],
  angry: [
    { target: 'Fcl_ALL_Angry', weight: 0.8 },
    { target: 'Fcl_BRW_Angry', weight: 0.6 },
  ],
  surprised: [
    { target: 'Fcl_ALL_Surprised', weight: 0.8 },
    { target: 'Fcl_EYE_Spread', weight: 0.4 },
    { target: 'Fcl_MTH_Large', weight: 0.3 },
  ],
};

export function readGlbJson(buf) {
  if (buf.readUInt32LE(0) !== GLB_MAGIC) throw new Error('不是 GLB 文件（magic 不匹配）');
  const version = buf.readUInt32LE(4);
  if (version !== 2) throw new Error('仅支持 glTF 2.0，实际 version=' + version);
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

export function findFaceTargets(gltf) {
  let best = null;
  (gltf.meshes ?? []).forEach((mesh, meshIndex) => {
    for (const prim of mesh.primitives ?? []) {
      const names = prim.extras?.targetNames;
      if (Array.isArray(names) && names.length > (best?.names.length ?? 0)) {
        best = { meshIndex, names };
      }
    }
  });
  return best;
}

export function buildFaceMap(gltf) {
  const found = findFaceTargets(gltf);
  if (!found) throw new Error('没有任何 primitive 带 extras.targetNames，无法建立面部映射');
  const targets = {};
  found.names.forEach((name, i) => {
    if (typeof name === 'string' && !(name in targets)) targets[name] = i;
  });
  return { version: 1, meshIndex: found.meshIndex, targets, presets: EMOTION_PRESETS };
}

function main() {
  const vrmPath = process.argv[2] ?? 'apps/web/public/assets/monika/Monika.vrm';
  const outPath = process.argv[3] ?? 'apps/web/src/avatar/face.map.json';
  const gltf = readGlbJson(readFileSync(vrmPath));
  const map = buildFaceMap(gltf);

  const count = Object.keys(map.targets).length;
  const errors = [];
  if (count !== 57) errors.push('target 数量应为 57，实际 ' + count);
  if (map.targets['Fcl_EYE_Close'] !== 13) errors.push("targets['Fcl_EYE_Close'] 应为 13，实际 " + map.targets['Fcl_EYE_Close']);
  if (map.targets['Fcl_MTH_A'] !== 39) errors.push("targets['Fcl_MTH_A'] 应为 39，实际 " + map.targets['Fcl_MTH_A']);
  if (map.targets['Fcl_MTH_O'] !== 43) errors.push("targets['Fcl_MTH_O'] 应为 43，实际 " + map.targets['Fcl_MTH_O']);
  for (const [emotion, binds] of Object.entries(map.presets)) {
    for (const bind of binds) {
      if (!(bind.target in map.targets)) errors.push('preset ' + emotion + ' 引用了不存在的 target ' + bind.target);
    }
  }
  if (errors.length) {
    console.error('face.map.json 校验失败:');
    for (const e of errors) console.error('  - ' + e);
    process.exit(1);
  }

  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, JSON.stringify(map, null, 2) + '\n', 'utf8');
  console.log('face map ok: ' + count + ' targets, meshIndex=' + map.meshIndex + ' -> ' + outPath);
  console.log('  Fcl_EYE_Close=13 Fcl_MTH_A=39 Fcl_MTH_O=43 Fcl_HA_Hide=' + map.targets['Fcl_HA_Hide']);
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(new URL(import.meta.url).pathname.replace(/^\//, ''))) main();
