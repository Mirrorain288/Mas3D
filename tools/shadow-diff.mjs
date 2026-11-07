#!/usr/bin/env node
// 阴影是不是真的落到了几何上：同一机位渲两张（开阴影 / 关阴影），逐像素算亮度差。
//
//   node tools/shadow-diff.mjs apps/unity/Shots/play-01.png apps/unity/Shots/no-shadow-01.png
//   node tools/shadow-diff.mjs room-shadow-01.png room-noshadow-01.png --min-lower 0.02 --min-peak 0.25
//
// 判据（R17）：有阴影的那张，至少 3% 的像素比无阴影那张暗 3% 以上。
// 只统计"变暗"的方向：阴影只会拿走光，不会加光，反方向的差是别的噪声。
//
// 两对图用不同阈值，所以两个门槛都可调（默认仍是 R17 的 0.03 / 0）：
//   play-01 那对里有一整个玩家身体在投影，3% 很容易就够；
//   room 那对把玩家藏起来了，只剩家具，要求的是「家具真的在地上落影」——
//   比例可以低到 2%，但必须有一块被压暗 25% 以上，否则只是整片地面被均匀压暗。
// --min-peak 是「最深压暗」的下限（0..1，0.25 = 25%）。
//
// 配对守卫：阴影只会拿走光，不会给光。所以参照图（无阴影那张）里不该有任何一块比有阴影那张
// 亮出 2% 以上——真出现了，说明两张图根本不是同一个场景（最典型的是「参照里把会投影的东西
// 藏起来了」，比如玩家身体只在一张里）。这种配对不管数值多好看都是假的，直接 FAIL。

import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(path.resolve('package.json'));
const sharp = require('sharp');

const argv = process.argv.slice(2);
const positional = [];
const opt = (name, fallback) => {
  // 两种写法都认：`--min-lower 0.02` 与 `--min-lower=0.02`。只认前一种的话，等号写法会
  // 落进 positional 被当成路径、然后静默用默认值盖章。
  const glued = argv.find((a) => a.startsWith('--' + name + '='));
  if (glued) return Number(glued.slice(name.length + 3));
  const i = argv.indexOf('--' + name);
  if (i < 0) return fallback;
  // 缺值静默回落到默认值，等于把判据悄悄放宽（--min-peak 的默认 0 就是「不看最深压暗」），
  // 所以宁可报错退出，也不要拿一个谁都没指定的门槛去盖章。
  const raw = argv[i + 1];
  if (raw === undefined || raw.startsWith('--') || raw.trim() === '' || !Number.isFinite(Number(raw))) {
    console.error('--' + name + ' 后面要跟一个 0..1 的数值，收到的是 "' + (raw === undefined ? '' : raw) + '"');
    process.exit(2);
  }
  return Number(raw);
};
for (let i = 0; i < argv.length; i++) {
  if (argv[i].startsWith('--min-lower=') || argv[i].startsWith('--min-peak=')) continue;
  if (argv[i] === '--min-lower' || argv[i] === '--min-peak') { i++; continue; }
  positional.push(argv[i]);
}
const [aPath, bPath] = positional;
const minLower = opt('min-lower', 0.03);
const minPeak = opt('min-peak', 0);
if (!aPath || !bPath) {
  console.error('用法：node tools/shadow-diff.mjs <with-shadows.png> <without-shadows.png> [--min-lower 0.03] [--min-peak 0]');
  process.exit(2);
}
if (!(minLower >= 0 && minLower <= 1) || !(minPeak >= 0 && minPeak <= 1)) {
  console.error('--min-lower / --min-peak 是 0..1 的比例');
  process.exit(2);
}

const raw = async (file) => {
  const { data, info } = await sharp(file).greyscale().raw().toBuffer({ resolveWithObject: true });
  return { data, width: info.width, height: info.height };
};

const a = await raw(aPath);
const b = await raw(bPath);
if (a.width !== b.width || a.height !== b.height) {
  console.error('两张图尺寸不一致，没法逐像素比：' + a.width + 'x' + a.height + ' vs ' + b.width + 'x' + b.height);
  process.exit(2);
}

// 影子只能落在能照到直射光的地方：这个房间里月光进窗后落在下半幅的地板与床，
// 上半幅是吊顶和墙（只有不投影的吊顶光）。所以判据算的是"下半幅里变暗的像素比例"，
// 全画面比例只作参考打印；用全画面当分母等于用吊顶的像素去摊薄地板的影子。
const W = a.width, H = a.height;
const splitRow = Math.floor(H * 0.5);

// 配对守卫的门槛是固定的：2% 的像素「比参照亮 2% 以上」就已经不是噪声能解释的了。
const RISE = 0.02;
// 0.5% 不是拍的：「参照里少了会投影的东西」实测是 1.65%（玩家只占 1~2% 像素），
// 而合法的一对是 0.00%。定 2% 时这条守卫抓不住那类回归，等于白装。
const RISE_SHARE = 0.005;
let darkerAll = 0, darkerLower = 0, totalLower = 0, maxDrop = 0, brighterAll = 0;
for (let y = 0; y < H; y++) {
  for (let x = 0; x < W; x++) {
    const i = y * W + x;
    const drop = (b.data[i] - a.data[i]) / 255;
    if (drop >= 0.03) darkerAll++;
    if (drop > maxDrop) maxDrop = drop;
    if (-drop >= RISE) brighterAll++;
    if (y >= splitRow) {
      totalLower++;
      if (drop >= 0.03) darkerLower++;
    }
  }
}

const all = darkerAll / (W * H);
const lower = darkerLower / totalLower;
const lit = brighterAll / (W * H);
const okLower = lower >= minLower;
const okPeak = maxDrop >= minPeak;
const okPair = lit <= RISE_SHARE;
const ok = okLower && okPeak && okPair;
console.log(
  `下半幅变暗 ${(lower * 100).toFixed(2)}%  全画面 ${(all * 100).toFixed(2)}%  最深 ${(maxDrop * 100).toFixed(1)}%  ` +
  `判据 下半幅 >=${(minLower * 100).toFixed(1)}% 且 最深 >=${(minPeak * 100).toFixed(0)}%`);
console.log(
  `参照里变亮 >=${(RISE * 100).toFixed(0)}% 的像素 ${(lit * 100).toFixed(2)}%（上限 ${(RISE_SHARE * 100).toFixed(0)}%：` +
  '阴影只拿光不给光，亮回去就说明两张图不是同一个场景）');
if (!okPair) console.error('配对失效（参照里少了会投影的东西？）');
console.log(ok ? 'PASS' : (!okPair ? 'FAIL (pair)' : (okLower ? 'FAIL (peak)' : 'FAIL (lower)')));
process.exitCode = ok ? 0 : 1;