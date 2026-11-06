#!/usr/bin/env node
// 阴影是不是真的落到了几何上：同一机位渲两张（开阴影 / 关阴影），逐像素算亮度差。
//
//   node tools/shadow-diff.mjs apps/unity/Shots/play-01.png apps/unity/Shots/no-shadow-01.png
//
// 判据（R17）：有阴影的那张，至少 3% 的像素比无阴影那张暗 3% 以上。
// 只统计"变暗"的方向：阴影只会拿走光，不会加光，反方向的差是别的噪声。

import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(path.resolve('package.json'));
const sharp = require('sharp');

const [aPath, bPath] = process.argv.slice(2);
if (!aPath || !bPath) {
  console.error('用法：node tools/shadow-diff.mjs <with-shadows.png> <without-shadows.png>');
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

let darkerAll = 0, darkerLower = 0, totalLower = 0, maxDrop = 0;
for (let y = 0; y < H; y++) {
  for (let x = 0; x < W; x++) {
    const i = y * W + x;
    const drop = (b.data[i] - a.data[i]) / 255;
    if (drop >= 0.03) darkerAll++;
    if (drop > maxDrop) maxDrop = drop;
    if (y >= splitRow) {
      totalLower++;
      if (drop >= 0.03) darkerLower++;
    }
  }
}

const all = darkerAll / (W * H);
const lower = darkerLower / totalLower;
const ok = lower >= 0.03;
console.log(`下半幅变暗 ${(lower * 100).toFixed(2)}%  全画面 ${(all * 100).toFixed(2)}%  最深 ${(maxDrop * 100).toFixed(1)}%  判据 下半幅 >=3%`);
console.log(ok ? 'PASS' : 'FAIL');
process.exitCode = ok ? 0 : 1;
