#!/usr/bin/env node
// 虹膜贴图改造：把 VRM 里那张荧光黄绿的虹膜（img3 / _02）重画成低饱和青绿，
// 并把外圈那圈厚黑边削成 2-3 个纹理像素的深棕细边。
//
//   node tools/eyes-recolor.mjs --src extracted/img3__02.png --out apps/unity/Assets/_Local/avatars/eyes/iris.png
//
// 为什么不改 VRM：VRM 是导入产物，重导入就丢；改出来的图放在 Assets/_Local/avatars/eyes/，
// 由 AvatarEyes 在场景副本材质上引用，场景与打包版都带着它。
//
// 只动颜色，不动 UV：两只虹膜在原图里的位置、形状、瞳孔大小全部沿用，
// 这样网格采样到的还是同一块区域，不需要重新对 UV。

import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(path.resolve("package.json"));
const sharp = require("sharp");

const argv = process.argv.slice(2);
const arg = (name, fallback = null) => {
  const i = argv.indexOf("--" + name);
  return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback;
};

const src = arg("src", ".superpowers/sdd/2026-11-07-eyes-and-room-shadows/extracted/img3__02.png");
const out = arg("out", "apps/unity/Assets/_Local/avatars/eyes/iris.png");
const preview = arg("preview", path.join(path.dirname(out), "..", "..", "..", "..", "..", ".superpowers/sdd/2026-11-07-eyes-and-room-shadows/extracted/iris-preview.png"));

// 目标观感（参考立绘量出来的那组数）：色相 150–170、饱和度 0.40–0.55，
// 顶部深、越往下越亮（原画就是上蓝下绿），外圈换成细的深棕而不是黑。
//
// 做法是「保结构、换颜色」：原画里那块青绿是分层画的——深蓝顶盖、青色瞳孔、亮黄绿下摆，
// 先用径向渐变把它抹平过一次，结果整只眼变成一块单色塑料（try1，见 .superpowers 里的预览）。
// 所以这里保留每个像素原来的明暗关系，只把色相/饱和度重新映射到青绿区间；
// 外圈那圈厚黑边靠「离边缘的距离」拆掉：最外 2 px 留深棕，往里到 14 px 一律涂成虹膜本色。
// 贴图这一侧按「渲染会放大」反推：第一版贴图平均饱和度约 0.50，进画面量出来 0.823
// （MToon 的 shade 相乘 + ACES 之后普遍抬 1.6–1.8 倍），所以贴图目标压到 0.30 上下。
// 色相同理，画面比贴图偏 +15°~20°，这里取 140–158，落到画面上才在 160 出头。
const PALETTE = {
  hueBright: 140, hueDark: 158,
  satBase: 0.16, satGain: 0.20,
  valBase: 0.22, valGain: 0.66,
  pupilRgb: [44, 62, 68],
  // 外圈：2 px 深棕量出来 2.5 px 暗带（判据 <= 2 px），所以只留 1 px，并把棕色调亮到 V≈0.55——
  // 「细而不黑」本来就是参考图的口径：边缘要能看见，但不该是一圈墨线。
  rimRgb: [140, 112, 96], rimPx: 1, rimFade: 14,
};

const rgbToHsv = (r, g, b) => {
  const rr = r / 255, gg = g / 255, bb = b / 255;
  const max = Math.max(rr, gg, bb), min = Math.min(rr, gg, bb), d = max - min;
  let h = 0;
  if (d > 1e-6) {
    if (max === rr) h = 60 * (((gg - bb) / d) % 6);
    else if (max === gg) h = 60 * (((bb - rr) / d) + 2);
    else h = 60 * (((rr - gg) / d) + 4);
  }
  if (h < 0) h += 360;
  return [h, max <= 0 ? 0 : d / max, max];
};

const hsvToRgb = (h, s, v) => {
  const c = v * s;
  const hp = (((h % 360) + 360) % 360) / 60;
  const x = c * (1 - Math.abs((hp % 2) - 1));
  let r = 0, g = 0, b = 0;
  if (hp < 1) { r = c; g = x; }
  else if (hp < 2) { r = x; g = c; }
  else if (hp < 3) { g = c; b = x; }
  else if (hp < 4) { g = x; b = c; }
  else if (hp < 5) { r = x; b = c; }
  else { r = c; b = x; }
  const m = v - c;
  return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
};

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

const main = async () => {
  const image = sharp(src);
  const meta = await image.metadata();
  const { data, info } = await image.raw().toBuffer({ resolveWithObject: true });
  const w = info.width, h = info.height, ch = info.channels;
  const idx = (x, y) => (y * w + x) * ch;

  const alpha = (x, y) => data[idx(x, y) + 3];
  const mask = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (alpha(x, y) > 8) mask[y * w + x] = 1;

  // 连通域：原图里左右两只眼并排放着，各自是一个不透明的虹膜块。
  const seen = new Uint8Array(w * h);
  const blobs = [];
  const stack = [];
  for (let start = 0; start < mask.length; start++) {
    if (!mask[start] || seen[start]) continue;
    const blob = { pixels: [], x0: w, y0: h, x1: -1, y1: -1 };
    stack.length = 0; stack.push(start); seen[start] = 1;
    while (stack.length) {
      const i = stack.pop();
      const x = i % w, y = (i - x) / w;
      blob.pixels.push(i);
      if (x < blob.x0) blob.x0 = x;
      if (x > blob.x1) blob.x1 = x;
      if (y < blob.y0) blob.y0 = y;
      if (y > blob.y1) blob.y1 = y;
      if (x > 0 && mask[i - 1] && !seen[i - 1]) { seen[i - 1] = 1; stack.push(i - 1); }
      if (x < w - 1 && mask[i + 1] && !seen[i + 1]) { seen[i + 1] = 1; stack.push(i + 1); }
      if (y > 0 && mask[i - w] && !seen[i - w]) { seen[i - w] = 1; stack.push(i - w); }
      if (y < h - 1 && mask[i + w] && !seen[i + w]) { seen[i + w] = 1; stack.push(i + w); }
    }
    if (blob.pixels.length > 200) blobs.push(blob);
  }
  if (!blobs.length) throw new Error("原图里没找到不透明的虹膜块");

  const outData = Buffer.from(data);
  for (const blob of blobs) {
    // 到边缘的距离（8 邻域 BFS，单位步长，够用来分「外圈 / 主体 / 瞳孔」）。
    const dist = new Int32Array(w * h).fill(-1);
    const queue = [];
    for (const i of blob.pixels) {
      const x = i % w, y = (i - x) / w;
      let edge = false;
      for (let dy = -1; dy <= 1 && !edge; dy++) {
        for (let dx = -1; dx <= 1; dx++) {
          const nx = x + dx, ny = y + dy;
          if (nx < 0 || ny < 0 || nx >= w || ny >= h || !mask[ny * w + nx]) { edge = true; break; }
        }
      }
      if (edge) { dist[i] = 1; queue.push(i); }
    }
    let head = 0;
    while (head < queue.length) {
      const i = queue[head++];
      const x = i % w, y = (i - x) / w;
      const d = dist[i];
      for (let dy = -1; dy <= 1; dy++) {
        for (let dx = -1; dx <= 1; dx++) {
          const nx = x + dx, ny = y + dy;
          if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
          const j = ny * w + nx;
          if (!mask[j] || dist[j] >= 0) continue;
          dist[j] = d + 1;
          queue.push(j);
        }
      }
    }
    let maxD = 1;
    for (const i of blob.pixels) if (dist[i] > maxD) maxD = dist[i];

    const halfW = (blob.x1 - blob.x0) / 2, halfH = (blob.y1 - blob.y0) / 2;
    const cx = (blob.x0 + blob.x1) / 2, cy = (blob.y0 + blob.y1) / 2;
    const pupilRx = halfW * 0.30, pupilRy = halfH * 0.36, pupilCy = cy - halfH * 0.04;

    for (const i of blob.pixels) {
      const x = i % w, y = (i - x) / w;
      const d = dist[i] < 0 ? 0 : dist[i];
      const vy = clamp01((y - blob.y0) / Math.max(1, blob.y1 - blob.y0));
      const src = idx(x, y);
      const [h0, s0, v0] = rgbToHsv(data[src], data[src + 1], data[src + 2]);

      // 瞳孔：原画中央那只青色椭圆。它本来就是最深的青，压暗一档更像瞳孔。
      const px = (x - cx) / pupilRx, py = (y - pupilCy) / pupilRy;
      const insidePupil = px * px + py * py <= 1;

      let rgb;
      if (d <= PALETTE.rimPx) {
        rgb = PALETTE.rimRgb;
      } else if (insidePupil && v0 < 0.72) {
        rgb = PALETTE.pupilRgb;
      } else if (v0 < 0.25 && d <= PALETTE.rimFade) {
        // 原来那圈 13 px 厚黑边：最外 2 px 已经是深棕，往里这一段改成虹膜本色，边就细了。
        rgb = hsvToRgb(PALETTE.hueBright + 14 * (1 - vy), 0.30, 0.30 + 0.26 * (1 - vy));
      } else {
        const hue = PALETTE.hueBright + (PALETTE.hueDark - PALETTE.hueBright) * clamp01(1 - v0);
        const sat = clamp01(PALETTE.satBase + PALETTE.satGain * s0);
        const val = clamp01(PALETTE.valBase + PALETTE.valGain * v0);
        rgb = hsvToRgb(hue, sat, val);
      }

      // 深棕外圈到虹膜之间给 2 px 过渡，免得出现一圈硬边。
      if (d > PALETTE.rimPx && d <= PALETTE.rimPx + 2) {
        const k = (d - PALETTE.rimPx) / 2;
        rgb = [
          Math.round(PALETTE.rimRgb[0] + (rgb[0] - PALETTE.rimRgb[0]) * k),
          Math.round(PALETTE.rimRgb[1] + (rgb[1] - PALETTE.rimRgb[1]) * k),
          Math.round(PALETTE.rimRgb[2] + (rgb[2] - PALETTE.rimRgb[2]) * k),
        ];
      }

      // 左上加一处柔白高光：核心全白，边缘 2 px 渐隐（判据要 >= 8 px 的高亮簇，且不能糊成大块）。
      const hx = cx - halfW * 0.46, hy = cy - halfH * 0.52, hr = maxD * 0.16;
      const hd = Math.hypot(x - hx, y - hy) / Math.max(1, hr);
      if (hd < 1) {
        const k = hd < 0.55 ? 1 : 1 - (hd - 0.55) / 0.45;
        rgb = [
          Math.round(rgb[0] + (255 - rgb[0]) * k),
          Math.round(rgb[1] + (255 - rgb[1]) * k),
          Math.round(rgb[2] + (255 - rgb[2]) * k),
        ];
      }

      const o = idx(x, y);
      outData[o] = rgb[0];
      outData[o + 1] = rgb[1];
      outData[o + 2] = rgb[2];
    }
  }

  fs.mkdirSync(path.dirname(out), { recursive: true });
  const shot = sharp(outData, { raw: { width: w, height: h, channels: ch } });
  await shot.png().toFile(out);

  // 看一眼用的预览：铺在两倍大的深色底上，避免透明区域看不见形状。
  const previewScale = 2;
  const big = await sharp(outData, { raw: { width: w, height: h, channels: ch } })
    .resize({ width: w * previewScale, height: h * previewScale, kernel: "nearest" })
    .png().toBuffer();
  await sharp({ create: { width: w * previewScale, height: h * previewScale, channels: 3, background: "#202024" } })
    .composite([{ input: big, left: 0, top: 0 }])
    .png().toFile(preview);

  console.log("eyed: blobs=" + blobs.length + " size=" + w + "x" + h);
  console.log("out: " + out);
  console.log("preview: " + preview);
};

main().catch((error) => {
  console.error("eyes-recolor 失败：" + (error && error.message ? error.message : error));
  process.exitCode = 1;
});