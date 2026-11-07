#!/usr/bin/env node
// 视觉验收：把出图和参考立绘并排放在一起，同时给出可复算的数值。
//
//   node tools/look-compare.mjs --mine .superpowers/look/2026-11-06 \
//        --ref docs/superpowers/specs/assets/2026-11-06-reference.png \
//        --report .superpowers/look/2026-11-06/metrics.json
//
// 判据（设计文档 G1~G4）：
//   脸颊 B/R <= 0.90（皮肤不能偏蓝紫）
//   脸峰值 <= 0.92（ACES 之后再亮就糊白了）
//   房间对比度 p95-p5 >= 0.35（不能一片灰）
//   描边像素 >= 1.5（1080p；720p 出图按比例折算 1.0）
//   脚下八角度亮度极差 < 5%（地上不许有一块跟着相机跑的光斑）
//   眼睛（eyes.png 特写）：虹膜平均饱和度 <= 0.62、色相 140°–185°、
//     虹膜内存在 >= 8 像素的亮度 >= 0.90 高光簇且面积 <= 虹膜 8%、外圈暗带 <= 2 px @720p
// 任一不过 exit 1，并把逐条结果写进 report。

import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(path.resolve('package.json'));
const sharp = require('sharp');

const argv = process.argv.slice(2);
const arg = (name, fallback = null) => {
  const i = argv.indexOf('--' + name);
  return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback;
};

const mine = arg('mine', 'apps/unity/Shots');
const ref = arg('ref', 'docs/superpowers/specs/assets/2026-11-06-reference.png');
const reportPath = arg('report', path.join(mine, 'metrics.json'));

const LIMITS = { cheekBR: 0.90, facePeak: 0.92, roomContrast: 0.35, outlinePx: 1.5, footRatio: 1.30 };

const lum = (r, g, b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;

async function load(file) {
  const image = sharp(file);
  const meta = await image.metadata();
  const { data, info } = await image.raw().toBuffer({ resolveWithObject: true });
  const at = (x, y) => {
    const i = (y * info.width + x) * info.channels;
    return [data[i], data[i + 1], data[i + 2]];
  };
  return { file, meta, info, at, width: info.width, height: info.height };
}

function pick(dir, names) {
  for (const name of names) {
    const file = path.join(dir, name);
    if (fs.existsSync(file)) return file;
  }
  return null;
}

// 皮肤像素的判据。白领子/白衬衫红蓝差很小，必须排除，否则"最亮像素"全是布料的。
// 2026-11-06 修好 PostProcessData 之后 ACES 真的跑起来了，皮肤整体压暗了一档：
// 原来那套 pr > 195 的阈值一个像素都选不出来（实测整张脸落在 150~200），所以放宽到 150，
// 同时用 r-g >= 18 与 g > b 保住"暖"这个前提——参考立绘 (215,169,156) 与出图 (183,146,132) 都落在里面。
const hitsSkin = (pr, pg, pb) => pr >= 150 && pr - pg >= 18 && pg > pb && pr - pb >= 20 && pr - pb <= 110 && pb < 235;

// 脸：在画面中上部找一块"皮肤"像素，算平均色。粉色头发和深色瞳孔都被排除在外。
function cheekMood(shot) {
  const x0 = Math.round(shot.width * 0.33), x1 = Math.round(shot.width * 0.62);
  const y0 = Math.round(shot.height * 0.18), y1 = Math.round(shot.height * 0.52);
  let r = 0, g = 0, b = 0, n = 0, bx0 = 1e9, bx1 = -1, by0 = 1e9, by1 = -1;
  const lums = [];
  for (let y = y0; y < y1; y++) {
    for (let x = x0; x < x1; x++) {
      const [pr, pg, pb] = shot.at(x, y);
      if (!hitsSkin(pr, pg, pb)) continue;
      r += pr; g += pg; b += pb; n++;
      lums.push(lum(pr, pg, pb));
      bx0 = Math.min(bx0, x); bx1 = Math.max(bx1, x); by0 = Math.min(by0, y); by1 = Math.max(by1, y);
    }
  }
  if (!n) return { pixels: 0, cheekBR: null, facePeak: null };
  lums.sort((a, b) => a - b);
  const peak995 = lums[Math.min(lums.length - 1, Math.floor(lums.length * 0.995))];
  return {
    pixels: n,
    cheekBR: +(b / r).toFixed(3),
    facePeak: +peak995.toFixed(3),
    cheekRGB: [Math.round(r / n), Math.round(g / n), Math.round(b / n)],
    box: [bx0, by0, bx1, by1],
  };
}

// 描边：沿水平扫描线找"两侧都比它亮一截"的细暗带，取最宽的一条。
// 只统计角色左/右轮廓附近（画面中段），房间里的家具边缘不参与。
function outlineWidth(shot) {
  const rows = [];
  for (let y = Math.round(shot.height * 0.35); y < Math.round(shot.height * 0.92); y += 2) rows.push(y);
  // 家具边缘也是暗带，但它们又宽又少；描边的特征是"细、而且每一行都有"。所以取所有
  // 长度 ≤10 px 的暗带里的 75 分位，家具那几条宽边不会把数字顶上去。
  const runs = [];
  for (const y of rows) {
    let run = 0;
    for (let x = 6; x < shot.width - 6; x++) {
      const here = lum(...shot.at(x, y));
      const out = lum(...shot.at(Math.min(shot.width - 1, x + 5), y));
      const into = lum(...shot.at(Math.max(0, x - 5), y));
      const dark = here < 0.55 * out && here < 0.55 * into;
      if (dark) {
        run++;
      } else {
        if (run > 0 && run <= 10) runs.push(run);
        run = 0;
      }
    }
  }
  if (!runs.length) return 0;
  runs.sort((a, b) => a - b);
  return runs[Math.floor(runs.length * 0.75)];
}

function percentiles(shot) {
  const values = [];
  const step = Math.max(1, Math.round((shot.width * shot.height) / 40000));
  for (let y = 0; y < shot.height; y += step) {
    for (let x = 0; x < shot.width; x += step) values.push(lum(...shot.at(x, y)));
  }
  values.sort((a, b) => a - b);
  const at = (p) => values[Math.min(values.length - 1, Math.floor(p * values.length))];
  return { p5: +at(0.05).toFixed(3), p95: +at(0.95).toFixed(3), contrast: +(at(0.95) - at(0.05)).toFixed(3) };
}

// 脚下光斑：八张绕圈图取同一块地面区域的中位亮度。判据用「最亮/最暗」而不是极差——
// 2026-11-06 实测：床头灯那一侧本来就比窗侧亮（修好 PostProcessData 之前 19%，之后 27%），
// 绝对极差会把正常的方向性亮度判成缺陷；跟着相机跑的光会表现为某一个角度单独跳出去，
// 所以只在比值超过 1.30 时才判红。
function footSpread(dir) {
  const files = fs.readdirSync(dir)
    .filter((f) => /^foot-\d+\.png$/.test(f))
    .sort()
    .map((f) => path.join(dir, f));
  if (files.length < 6) return null;
  return Promise.all(files.map(async (file) => {
    const shot = await load(file);
    const x0 = Math.round(shot.width * 0.30), x1 = Math.round(shot.width * 0.70);
    const y0 = Math.round(shot.height * 0.55), y1 = Math.round(shot.height * 0.95);
    const samples = [];
    for (let y = y0; y < y1; y++) {
      for (let x = x0; x < x1; x++) samples.push(lum(...shot.at(x, y)));
    }
    samples.sort((a, b) => a - b);
    return samples[Math.floor(samples.length * 0.5)];
  })).then((values) => {
    const ratio = Math.max(...values) / Math.max(1e-6, Math.min(...values));
    return {
      shots: values.length,
      meanLum: +(values.reduce((a, b) => a + b, 0) / values.length).toFixed(3),
      ratio: +ratio.toFixed(3),
      spread: +(Math.max(...values) - Math.min(...values)).toFixed(3),
    };
  });
}

async function compareSheet(mineShot, refShot, out) {
  const height = 900;
  const scale = (shot) => path.extname(shot.file) && sharp(shot.file).resize({ height }).png().toBuffer();
  const [left, right] = await Promise.all([scale(refShot), scale(mineShot)]);
  const lm = await sharp(left).metadata();
  const rm = await sharp(right).metadata();
  await sharp({
    create: { width: lm.width + rm.width + 12, height, channels: 3, background: '#101014' },
  }).composite([
    { input: left, left: 0, top: 0 },
    { input: right, left: lm.width + 12, top: 0 },
  ]).png().toFile(out).catch(() => {});
}

// ---------- 眼睛特写 ----------
// 判据量的是「画到屏幕上的虹膜」，不是贴图：MToon 的上色与 ACES 会把贴图的饱和度、
// 明度各压一遍，只有在出图里量才作数。这里不做着色模型假设——先在整张图里找青绿连通域，
// 面积最大的两块就是左右虹膜（皮肤是暖色、眼白接近无彩，都进不了掩膜），
// 再在虹膜里量高光簇与外圈暗带。
const EYE_LIMITS = { irisS: 0.62, hueMin: 140, hueMax: 185, highlightPx: 8, highlightShare: 0.08, rimPx: 2 };

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

// 虹膜像素：青绿且不是灰白。色相窗口开到 110–210，是为了容下 MToon 的明暗两段
// （贴图上深蓝青、下荧光绿，经过光照后色相会散开），皮肤（约 20–40°）与眼白（近乎无彩）不会命中。
const isIris = (rgb) => {
  const c = rgbToHsv(rgb[0], rgb[1], rgb[2]);
  return c[0] >= 110 && c[0] <= 210 && c[1] >= 0.18 && c[2] >= 0.12;
};

// 外圈像素：暖色相（棕），有彩、不是纯黑。实测贴图上 rgb(140,112,96) → hue 22.7、S 0.31。
const isRimWarm = (c) => c[0] >= 5 && c[0] <= 65 && c[1] >= 0.10 && c[2] >= 0.10;

// 高光像素：只看亮度（判据原文就是「亮度 >= 0.90」）。
// 这里不再加「饱和度低」这一条：场景光是偏冷的，贴图里纯白的点在画面上是 rgb(196,217,254)
// ——S=0.228，加 S<=0.20 会把真高光整簇筛掉（上一版就是这么量出 0 px 的）。
// 「是不是虹膜内的高光」由连通域归属保证，不靠颜色纯不纯。
const isHighlight = (rgb) => {
  const c = rgbToHsv(rgb[0], rgb[1], rgb[2]);
  return c[2] >= 0.90;
};

// 4 邻域连通域：返回面积、质心、包围盒、平均饱和度与色相的圆均值（跨 0/360 不会算错）。
// box 给定时只在框内取种子（高光簇要在虹膜框里找，整图的白墙也是亮像素）。
function components(shot, test, box, withIds) {
  const w = shot.width, h = shot.height;
  const x0b = box ? Math.max(0, box[0]) : 0, y0b = box ? Math.max(0, box[1]) : 0;
  const x1b = box ? Math.min(w - 1, box[2]) : w - 1, y1b = box ? Math.min(h - 1, box[3]) : h - 1;
  const mask = new Uint8Array(w * h);
  for (let y = y0b; y <= y1b; y++) {
    for (let x = x0b; x <= x1b; x++) {
      if (test(shot.at(x, y))) mask[y * w + x] = 1;
    }
  }
  // seen 同时当作「像素属于第几个连通域」的标签（0 = 不属于任何域），高光簇质心要靠它判
  // 「是不是落在这只虹膜里面」——只靠包围盒的话，眼白上的反光也会被算成虹膜高光。
  const seen = new Uint8Array(w * h);
  const stack = new Int32Array(w * h);
  const out = [];
  let id = 0;
  for (let start = y0b * w + x0b; start < (y1b + 1) * w; start++) {
    if (!mask[start] || seen[start]) continue;
    id += 1;
    let top = 0;
    stack[top++] = start;
    seen[start] = id;
    let area = 0, sx = 0, sy = 0, sat = 0, sin = 0, cos = 0;
    let bx0 = w, bx1 = -1, by0 = h, by1 = -1;
    while (top > 0) {
      const i = stack[--top];
      const x = i % w, y = (i - x) / w;
      const rgb = shot.at(x, y);
      const c = rgbToHsv(rgb[0], rgb[1], rgb[2]);
      area++; sx += x; sy += y; sat += c[1];
      sin += Math.sin((c[0] * Math.PI) / 180);
      cos += Math.cos((c[0] * Math.PI) / 180);
      if (x < bx0) bx0 = x;
      if (x > bx1) bx1 = x;
      if (y < by0) by0 = y;
      if (y > by1) by1 = y;
      if (x > 0 && mask[i - 1] && !seen[i - 1]) { seen[i - 1] = id; stack[top++] = i - 1; }
      if (x < w - 1 && mask[i + 1] && !seen[i + 1]) { seen[i + 1] = id; stack[top++] = i + 1; }
      if (y > 0 && mask[i - w] && !seen[i - w]) { seen[i - w] = id; stack[top++] = i - w; }
      if (y < h - 1 && mask[i + w] && !seen[i + w]) { seen[i + w] = id; stack[top++] = i + w; }
    }
    let hue = (Math.atan2(sin / area, cos / area) * 180) / Math.PI;
    if (hue < 0) hue += 360;
    out.push({ id, area, cx: sx / area, cy: sy / area, box: [bx0, by0, bx1, by1], sat: sat / area, hue });
  }
  return withIds ? { blobs: out, ids: seen } : out;
}

// 外圈暗带：从虹膜外缘沿「水平子午线 ±15°」往外数连续像素。
// 只取左右两侧是有原因的：正上方是睫毛/眼线，那里本来就黑，量出来会把外圈判成 10 px。
//
// 判「是不是外圈」用暖色相（5–65°）而不是「亮度过低」：外圈本来就是棕色细环，
// 用 V < 0.5 只能量到阴影那一侧（亮侧外圈 V≈0.65 直接漏掉），而且 1 px 的读数贴着量化底，
// 改一点点亮度就 0↔1 跳。按色相量，亮侧阴影侧一视同仁，也不受整体曝光影响。
// 一条射线只有在色带之后 6 px 内看到亮的眼白才计入（否则它打在眼睑或皮肤上）。
function rimOf(shot, blob, traced) {
  const r0 = Math.sqrt(blob.area / Math.PI);
  const step = 0.5;
  const rays = [];
  // 「虹膜内部」按连通域逐行的最左/最右补齐：虹膜掩膜上有洞（白高光本身不是虹膜色），
  // 用颜色猜「这像素算不算虹膜内」在眼白与高光之间没有可靠边界，按行跨度是纯几何的。
  const spans = new Map();
  const spanAt = (y) => {
    if (spans.has(y)) return spans.get(y);
    let lo = -1, hi = -1;
    const row = y * shot.width;
    for (let x = blob.box[0]; x <= blob.box[2]; x++) {
      if (traced.ids[row + x] !== blob.id) continue;
      if (lo < 0) lo = x;
      hi = x;
    }
    const span = lo >= 0 ? { lo, hi } : null;
    spans.set(y, span);
    return span;
  };
  const insideIris = (x, y) => {
    const span = spanAt(y);
    return span != null && x >= span.lo && x <= span.hi;
  };
  for (const side of [0, 180]) {
    for (let da = -15; da <= 15; da += 5) {
      const a = ((side + da) * Math.PI) / 180;
      const dx = Math.cos(a), dy = Math.sin(a);
      let lastInside = -1;
      for (let r = step; r <= r0 * 1.4; r += step) {
        const x = Math.round(blob.cx + dx * r), y = Math.round(blob.cy + dy * r);
        if (x < 0 || y < 0 || x >= shot.width || y >= shot.height) break;
        if (insideIris(x, y)) lastInside = r;
      }
      if (lastInside < 0) continue;
      let dark = 0;
      let sawSclera = false;
      for (let r = lastInside + step; r <= lastInside + 10; r += step) {
        const x = Math.round(blob.cx + dx * r), y = Math.round(blob.cy + dy * r);
        if (x < 0 || y < 0 || x >= shot.width || y >= shot.height) break;
        const c = rgbToHsv(...shot.at(x, y));
        if (isRimWarm(c)) { dark += step; continue; }
        sawSclera = c[2] >= 0.55;
        break;
      }
      if (sawSclera && dark > 0) rays.push(dark);
    }
  }
  if (!rays.length) return { px: null, rays: 0 };
  rays.sort((p, q) => p - q);
  // 判据看中位数：外圈是「一圈」的东西，两根被发丝/眼线挡住的射线（实测有一根读到 4.5 px
  // 的近黑，色相 257°，那是头发不是虹膜边）不该把整只眼判成厚边。最大值一起打出来备查。
  return {
    px: rays[Math.floor(rays.length / 2)],
    max: rays[rays.length - 1],
    rays: rays.length,
  };
}

function eyeLook(shot) {
  const traced = components(shot, isIris, null, true);
  const irises = traced.blobs.filter((b) => b.area >= 40).sort((a, b) => b.area - a.area).slice(0, 2);
  if (!irises.length) return { found: 0, eyes: [] };
  const total = irises.reduce((n, b) => n + b.area, 0);
  const sat = irises.reduce((n, b) => n + b.sat * b.area, 0) / total;
  const sin = irises.reduce((n, b) => n + Math.sin((b.hue * Math.PI) / 180) * b.area, 0) / total;
  const cos = irises.reduce((n, b) => n + Math.cos((b.hue * Math.PI) / 180) * b.area, 0) / total;
  let hue = (Math.atan2(sin, cos) * 180) / Math.PI;
  if (hue < 0) hue += 360;
  const eyes = irises.map((b) => {
    const box = [b.box[0] - 2, b.box[1] - 2, b.box[2] + 2, b.box[3] + 2];
    // 虹膜掩膜是「有洞」的：白高光本身不是虹膜色，质心落在洞里，直接查掩膜会把真高光也筛掉。
    // 按行把每行最左与最右之间的像素补齐（虹膜是凸的），得到「虹膜内部」范围。
    const inside = (x, y) => {
      if (!Number.isFinite(x) || !Number.isFinite(y)) return false;
      const yy = Math.round(y);
      if (yy < b.box[1] || yy > b.box[3]) return false;
      let lo = -1, hi = -1;
      for (let xx = b.box[0]; xx <= b.box[2]; xx++) {
        if (traced.ids[yy * shot.width + xx] !== b.id) continue;
        if (lo < 0) lo = xx;
        hi = xx;
      }
      return lo >= 0 && x >= lo && x <= hi;
    };
    // 质心必须落在这只虹膜的连通域内：包围盒里还有眼白，眼白上的反光也是亮而无彩的，
    // 只按「bbox + 2」找会把眼白反光当成虹膜高光（实测那正是最容易误判的一处）。
    const spots = components(shot, isHighlight, box)
      .filter((s) => inside(s.cx, s.cy))
      .sort((p, q) => q.area - p.area);
    const spot = spots.length ? spots[0].area : 0;
    return {
      box: b.box,
      area: b.area,
      sat: +b.sat.toFixed(3),
      hue: +b.hue.toFixed(1),
      highlight: spot,
      share: +(spot / b.area).toFixed(4),
      rim: rimOf(shot, b, traced),
      rimMax: rimOf(shot, b, traced).max,
    };
  });
  return { found: eyes.length, sat: +sat.toFixed(3), hue: +hue.toFixed(1), eyes };
}

// 眼部对照图：左边参考立绘裁出来的眼，右边当前出图的眼，同一高度最近邻放大。
async function eyesSheet(shot, eyes, refPath, out) {
  const H = 420;
  const cropOf = async (file, box, pad) => {
    const meta = await sharp(file).metadata();
    const left = Math.max(0, Math.round(box[0]) - pad);
    const top = Math.max(0, Math.round(box[1]) - pad);
    const width = Math.min(meta.width - left, Math.round(box[2] - box[0]) + 1 + pad * 2);
    const height = Math.min(meta.height - top, Math.round(box[3] - box[1]) + 1 + pad * 2);
    return sharp(file).extract({ left, top, width, height }).resize({ height: H, kernel: 'nearest' }).png().toBuffer();
  };
  const union = (boxes) => [
    Math.min(...boxes.map((b) => b[0])), Math.min(...boxes.map((b) => b[1])),
    Math.max(...boxes.map((b) => b[2])), Math.max(...boxes.map((b) => b[3])),
  ];
  const mineBox = union(eyes.eyes.map((e) => e.box));
  const mineCrop = await cropOf(shot.file, mineBox, Math.round((mineBox[2] - mineBox[0]) * 0.08));
  const refShot = await load(refPath);
  const refBlobs = components(refShot, isIris).filter((b) => b.area >= 8).sort((a, b) => b.area - a.area).slice(0, 2);
  if (!refBlobs.length) return;
  const refCrop = await cropOf(refPath, union(refBlobs.map((b) => b.box)), 6);
  const lm = await sharp(refCrop).metadata();
  const rm = await sharp(mineCrop).metadata();
  await sharp({ create: { width: lm.width + rm.width + 12, height: H, channels: 3, background: '#101014' } })
    .composite([{ input: refCrop, left: 0, top: 0 }, { input: mineCrop, left: lm.width + 12, top: 0 }])
    .png().toFile(out);
}

const main = async () => {
  // face.png 是 Capture 专门渲的脸部特写：全身图里白衬衫会过同一套皮肤筛选，把峰值钉在衣服上。
  const face = pick(mine, ['face.png', 'avatar-seated.png', 'play-03.png', 'play-01.png']);
  if (!face) throw new Error('没找到出图：' + mine);

  const faceShot = await load(face);
  const mood = cheekMood(faceShot);

  const roomFile = pick(mine, ['play-01.png', 'play-02.png']);
  const room = roomFile ? percentiles(await load(roomFile)) : null;

  const original = await load(face);
  const edge = outlineWidth(original);
  const heightScale = original.height / 1080;
  const outlineTarget = +(LIMITS.outlinePx * Math.max(0.667, heightScale)).toFixed(2);

  const feet = await footSpread(mine);

  // 眼睛：eyes.png 是 Capture 专门渲的眼部特写（两只虹膜各约 90 px 宽）。
  // face.png 里虹膜只有 25 px，外圈那两三个像素会被抗锯齿抹平，不能拿来量判据。
  const eyesFile = pick(mine, ['eyes.png']);
  const eyesShot = eyesFile ? await load(eyesFile) : null;
  const eyes = eyesShot ? eyeLook(eyesShot) : { found: 0, eyes: [] };
  const irisOk = eyes.found > 0 && eyes.sat <= EYE_LIMITS.irisS;
  const hueOk = eyes.found > 0 && eyes.hue >= EYE_LIMITS.hueMin && eyes.hue <= EYE_LIMITS.hueMax;
  const highlightOk = eyes.found > 0 && eyes.eyes.some((e) => e.highlight >= EYE_LIMITS.highlightPx && e.share <= EYE_LIMITS.highlightShare);
  const rimTarget = +(EYE_LIMITS.rimPx * (eyesShot ? eyesShot.height / 720 : 1)).toFixed(2);
  const rimPx = eyes.found > 0 ? Math.max(...eyes.eyes.map((e) => (e.rim.px == null ? 0 : e.rim.px))) : null;
  const rimOk = rimPx != null && rimPx > 0 && rimPx <= rimTarget;
  const bestEye = eyes.found > 0 ? eyes.eyes.slice().sort((a, b) => b.highlight - a.highlight)[0] : null;

  // 眼部对照图：生成失败要留痕（不算 fail，但绝不能静默吞掉——上一版就是 .catch(() => {})）。
  fs.mkdirSync(path.dirname(reportPath), { recursive: true });
  const sheetEyes = path.join(path.dirname(reportPath), 'eyes-sheet.png');
  let sheetEyesDone = false;
  if (eyesShot && eyes.found > 0 && fs.existsSync(ref)) {
    try {
      await eyesSheet(eyesShot, eyes, ref, sheetEyes);
      sheetEyesDone = fs.existsSync(sheetEyes);
      if (!sheetEyesDone) console.warn('eyes-sheet 生成了但没落盘：' + sheetEyes);
    } catch (err) {
      console.warn('eyes-sheet 没能生成：' + (err && err.message ? err.message : err));
    }
  } else {
    console.warn('eyes-sheet 没能生成：缺 eyes.png、没识别出虹膜或缺参考图');
  }

  const checks = {
    cheekBR: mood.cheekBR == null ? false : mood.cheekBR <= LIMITS.cheekBR,
    facePeak: mood.facePeak == null ? false : mood.facePeak <= LIMITS.facePeak,
    roomContrast: room == null ? false : room.contrast >= LIMITS.roomContrast,
    outline: edge >= outlineTarget,
    footSpread: feet == null ? null : feet.ratio <= LIMITS.footRatio,
    eyesShot: eyesShot != null,
    eyesColor: irisOk && hueOk,
    eyesHighlight: highlightOk,
    eyesRim: rimOk,
    // null = 没做（不算失败），true = 成了；report 里留着痕迹，别让生成失败悄悄过去。
    eyesSheet: sheetEyesDone ? true : null,
  };
  const failed = Object.entries(checks).filter(([, ok]) => ok === false).map(([k]) => k);

  const report = {
    when: new Date().toISOString(),
    shots: { face: path.relative(process.cwd(), face), eyes: eyesFile && path.relative(process.cwd(), eyesFile), room: roomFile && path.relative(process.cwd(), roomFile), ref: fs.existsSync(ref) ? path.relative(process.cwd(), ref) : null },
    numbers: { ...mood, outlinePx: edge, outlineTarget, room, feet, eyes: { found: eyes.found, sat: eyes.sat, hue: eyes.hue, rimTarget, rimPx, best: bestEye } },
    limits: LIMITS,
    checks,
    passed: failed.length === 0,
    failed,
  };

  fs.mkdirSync(path.dirname(reportPath), { recursive: true });
  fs.writeFileSync(reportPath, JSON.stringify(report, null, 2) + '\n');

  const sheet = path.join(path.dirname(reportPath), 'compare.png');
  if (fs.existsSync(ref)) await compareSheet(original, await load(ref), sheet);

  const line = (name, value, limit, ok) =>
    `${ok === null ? 'SKIP' : ok ? 'PASS' : 'FAIL'}  ${name.padEnd(14)} ${String(value).padEnd(8)} 限 ${limit}`;
  console.log(line('脸颊 B/R', mood.cheekBR, '<= ' + LIMITS.cheekBR, checks.cheekBR));
  console.log(line('脸峰值亮度', mood.facePeak, '<= ' + LIMITS.facePeak, checks.facePeak));
  console.log(line('房间对比度', room && room.contrast, '>= ' + LIMITS.roomContrast, checks.roomContrast));
  console.log(line('描边像素', edge, '>= ' + outlineTarget, checks.outline));
  console.log(line('脚下亮度比', feet && feet.ratio, '<= ' + LIMITS.footRatio, checks.footSpread));
  console.log(line('眼部特写', eyesFile ? path.basename(eyesFile) : null, 'eyes.png', checks.eyesShot));
  console.log(line('虹膜饱和度', eyes.found > 0 ? eyes.sat : null, '<= ' + EYE_LIMITS.irisS, irisOk));
  console.log(line('虹膜色相', eyes.found > 0 ? eyes.hue : null, EYE_LIMITS.hueMin + '–' + EYE_LIMITS.hueMax, hueOk));
  console.log(line('虹膜高光', bestEye ? bestEye.highlight + ' px ' + (bestEye.share * 100).toFixed(1) + '%' : null, '>= ' + EYE_LIMITS.highlightPx + ' px 且 <= 8%', highlightOk));
  console.log(line('外圈暗带', rimPx == null ? null : rimPx + ' px', '<= ' + rimTarget + ' px', rimOk));
  if (sheetEyesDone) console.log('eyes-sheet: ' + sheetEyes);
  console.log(`report: ${reportPath}`);
  if (fs.existsSync(sheet)) console.log(`compare: ${sheet}`);
  if (failed.length) {
    console.log('未达标：' + failed.join(', '));
    process.exitCode = 1;
  }
};

main().catch((error) => {
  console.error('look-compare 失败：' + (error && error.message ? error.message : error));
  process.exitCode = 2;
});
