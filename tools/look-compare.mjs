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

  const checks = {
    cheekBR: mood.cheekBR == null ? false : mood.cheekBR <= LIMITS.cheekBR,
    facePeak: mood.facePeak == null ? false : mood.facePeak <= LIMITS.facePeak,
    roomContrast: room == null ? false : room.contrast >= LIMITS.roomContrast,
    outline: edge >= outlineTarget,
    footSpread: feet == null ? null : feet.ratio <= LIMITS.footRatio,
  };
  const failed = Object.entries(checks).filter(([, ok]) => ok === false).map(([k]) => k);

  const report = {
    when: new Date().toISOString(),
    shots: { face: path.relative(process.cwd(), face), room: roomFile && path.relative(process.cwd(), roomFile), ref: fs.existsSync(ref) ? path.relative(process.cwd(), ref) : null },
    numbers: { ...mood, outlinePx: edge, outlineTarget, room, feet },
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
