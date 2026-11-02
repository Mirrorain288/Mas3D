
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';

const require = createRequire('D:/Mas3D/package.json');
const sharp = require('sharp');

const src = process.argv[2] || 'D:/Mas3D/apps/unity/Assets/_Local/room/mita-room.glb';
const outDir = process.argv[3] || 'D:/Mas3D/apps/unity/Assets/_Local/tex';
const manifestPath = process.argv[4] || 'D:/Mas3D/apps/unity/Assets/_Local/room/glb-materials.json';

const buf = fs.readFileSync(src);
const magic = buf.readUInt32LE(0);
if (magic !== 0x46546c67) throw new Error('not a glb');
const jsonLen = buf.readUInt32LE(12);
const json = JSON.parse(buf.subarray(20, 20 + jsonLen).toString('utf8'));
let off = 20 + jsonLen;
const binLen = buf.readUInt32LE(off);
const binStart = off + 8;
const bin = buf.subarray(binStart, binStart + binLen);

fs.mkdirSync(outDir, { recursive: true });
fs.mkdirSync(path.dirname(manifestPath), { recursive: true });

const safe = (s) => s.replace(/[^A-Za-z0-9_.-]/g, '_');
const images = [];

for (let i = 0; i < json.images.length; i++) {
  const im = json.images[i];
  const bv = json.bufferViews[im.bufferView];
  const start = (bv.byteOffset || 0) + (im.byteOffset || 0);
  const raw = bin.subarray(start, start + bv.byteLength);
  const name = safe(im.name || ('image_' + i));
  const file = name + '.png';
  const png = await sharp(Buffer.from(raw)).png({ compressionLevel: 9 }).toBuffer();
  const meta = await sharp(png).metadata();
  fs.writeFileSync(path.join(outDir, file), png);
  images.push({
    index: i,
    name: im.name || '',
    file,
    srcMime: im.mimeType,
    width: meta.width,
    height: meta.height,
    hasAlpha: !!meta.hasAlpha,
    bytes: png.length,
    md5: crypto.createHash('md5').update(png).digest('hex'),
  });
}

const textures = json.textures.map((t, i) => ({ index: i, source: t.source, image: images[t.source].name, file: images[t.source].file }));

const materials = json.materials.map((m, i) => {
  const pbr = m.pbrMetallicRoughness || {};
  const tex = pbr.baseColorTexture ? pbr.baseColorTexture.index : -1;
  const img = tex >= 0 ? textures[tex] : null;
  return {
    index: i,
    name: m.name,
    baseColorTexture: tex,
    image: img ? img.image : '',
    file: img ? img.file : '',
    baseColorFactor: pbr.baseColorFactor || [1, 1, 1, 1],
    metallicFactor: pbr.metallicFactor === undefined ? 1 : pbr.metallicFactor,
    roughnessFactor: pbr.roughnessFactor === undefined ? 1 : pbr.roughnessFactor,
    alphaMode: m.alphaMode || 'OPAQUE',
    doubleSided: m.doubleSided === true,
  };
});

const meshes = json.meshes.map((me, i) => ({
  index: i,
  name: me.name,
  materials: me.primitives.map((p) => (p.material === undefined ? -1 : p.material)),
}));

const manifest = { src, images, textures, materials, meshes };
fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));

const noTex = materials.filter((m) => m.baseColorTexture < 0);
console.log('[tex] images=' + images.length + ' textures=' + textures.length + ' materials=' + materials.length + ' meshes=' + meshes.length);
console.log('[tex] noTextureMaterials=' + noTex.map((m) => m.name).join(','));
console.log('[tex] wrote ' + path.join(outDir, '*.png') + ' and ' + manifestPath);
for (const im of images) console.log('  ' + im.index + ' ' + im.file.padEnd(18) + ' ' + im.width + 'x' + im.height + ' a=' + (im.hasAlpha ? 1 : 0) + ' ' + im.bytes + 'B');
