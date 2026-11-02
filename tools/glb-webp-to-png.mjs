#!/usr/bin/env node
// Convert a GLB whose textures use EXT_texture_webp into one Unity can import:
// every WebP image becomes PNG (alpha) or JPEG (no alpha), the images are packed
// into a rebuilt BIN chunk, and the extension declarations are dropped.
//
//   node tools/glb-webp-to-png.mjs <in.glb> <out.glb>
//
// Web assets keep their WebP version; this derivative only exists for Unity.

import fs from "node:fs";
import sharp from "sharp";

const GLB_MAGIC = 0x46546c67;
const CHUNK_JSON = 0x4e4f534a;
const CHUNK_BIN = 0x004e4942;

function readGlb(file) {
  const buf = fs.readFileSync(file);
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  if (dv.getUint32(0, true) !== GLB_MAGIC) throw new Error("not a GLB: " + file);
  const version = dv.getUint32(4, true);
  if (version !== 2) throw new Error("only glTF 2.0 GLB supported, got " + version);
  let offset = 12;
  let json = null;
  let bin = Buffer.alloc(0);
  while (offset < buf.length) {
    const len = dv.getUint32(offset, true);
    const type = dv.getUint32(offset + 4, true);
    const data = buf.subarray(offset + 8, offset + 8 + len);
    if (type === CHUNK_JSON) json = JSON.parse(data.toString("utf8"));
    else if (type === CHUNK_BIN) bin = data;
    offset += 8 + len;
  }
  if (!json) throw new Error("GLB has no JSON chunk");
  return { json, bin };
}

function writeGlb(file, json, bin) {
  const jsonBuf = Buffer.from(JSON.stringify(json), "utf8");
  const jsonPad = (4 - (jsonBuf.length % 4)) % 4;
  const jsonChunk = Buffer.concat([jsonBuf, Buffer.alloc(jsonPad, 0x20)]);
  const binPad = (4 - (bin.length % 4)) % 4;
  const binChunk = Buffer.concat([bin, Buffer.alloc(binPad, 0)]);
  const total = 12 + 8 + jsonChunk.length + (binChunk.length ? 8 + binChunk.length : 0);
  const out = Buffer.alloc(total);
  const dv = new DataView(out.buffer, out.byteOffset, out.byteLength);
  dv.setUint32(0, GLB_MAGIC, true);
  dv.setUint32(4, 2, true);
  dv.setUint32(8, total, true);
  dv.setUint32(12, jsonChunk.length, true);
  dv.setUint32(16, CHUNK_JSON, true);
  jsonChunk.copy(out, 20);
  if (binChunk.length) {
    dv.setUint32(20 + jsonChunk.length, binChunk.length, true);
    dv.setUint32(24 + jsonChunk.length, CHUNK_BIN, true);
    binChunk.copy(out, 28 + jsonChunk.length);
  }
  fs.writeFileSync(file, out);
  return { total, jsonBytes: jsonChunk.length, binBytes: binChunk.length };
}

const [, , inPath, outPath] = process.argv;
if (!inPath || !outPath) {
  console.error("usage: node tools/glb-webp-to-png.mjs <in.glb> <out.glb>");
  process.exit(2);
}

const { json, bin } = readGlb(inPath);
const views = json.bufferViews || [];
const images = json.images || [];

// Encode every WebP image once, keyed by its bufferView index.
const encoded = new Map();
let webpCount = 0;
for (let i = 0; i < images.length; i++) {
  const image = images[i];
  if (!image.mimeType || !image.mimeType.includes("webp")) continue;
  const view = views[image.bufferView];
  const start = view.byteOffset || 0;
  const src = bin.subarray(start, start + view.byteLength);
  const meta = await sharp(src).metadata();
  const hasAlpha = Boolean(meta.hasAlpha);
  const out = hasAlpha
    ? await sharp(src).png({ compressionLevel: 9 }).toBuffer()
    : await sharp(src).jpeg({ quality: 92, mozjpeg: true }).toBuffer();
  encoded.set(image.bufferView, out);
  image.mimeType = hasAlpha ? "image/png" : "image/jpeg";
  webpCount++;
  console.log(
    "image " + String(i).padStart(2) + " " + (image.name || "") + " " + meta.width + "x" + meta.height +
    " webp->" + (hasAlpha ? "png" : "jpg") + " " + view.byteLength + " -> " + out.length + " bytes",
  );
}

// Rebuild the BIN chunk so replaced views actually shrink the file.
const chunks = [];
const newViews = [];
let offset = 0;
for (let i = 0; i < views.length; i++) {
  const view = views[i];
  const bytes = encoded.has(i) ? encoded.get(i) : bin.subarray(view.byteOffset || 0, (view.byteOffset || 0) + view.byteLength);
  const pad = (4 - (offset % 4)) % 4;
  if (pad) chunks.push(Buffer.alloc(pad, 0));
  offset += pad;
  const next = { ...view, byteOffset: offset, byteLength: bytes.length };
  delete next.byteStride;
  if (view.byteStride && !encoded.has(i)) next.byteStride = view.byteStride;
  newViews.push(next);
  chunks.push(bytes);
  offset += bytes.length;
}
json.bufferViews = newViews;
json.buffers = [{ byteLength: offset }];
const newBin = Buffer.concat(chunks);

// Hoist EXT_texture_webp sources onto the textures themselves.
for (const texture of json.textures || []) {
  const ext = texture.extensions && texture.extensions.EXT_texture_webp;
  if (!ext) continue;
  if (typeof texture.source !== "number") texture.source = ext.source;
  delete texture.extensions.EXT_texture_webp;
  if (Object.keys(texture.extensions).length === 0) delete texture.extensions;
}
const strip = (list) => (list || []).filter((name) => name !== "EXT_texture_webp");
if (json.extensionsUsed) json.extensionsUsed = strip(json.extensionsUsed);
if (json.extensionsRequired) json.extensionsRequired = strip(json.extensionsRequired);
if (json.extensionsUsed && json.extensionsUsed.length === 0) delete json.extensionsUsed;
if (json.extensionsRequired && json.extensionsRequired.length === 0) delete json.extensionsRequired;

const stats = writeGlb(outPath, json, newBin);
console.log("webp images converted: " + webpCount + " / " + images.length);
console.log("wrote " + outPath + " (" + stats.total + " bytes; json " + stats.jsonBytes + ", bin " + stats.binBytes + ")");
