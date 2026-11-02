import fs from 'node:fs';
import { createRequire } from 'node:module';
const sharp = createRequire('D:/Mas3D/package.json')('sharp');

const data = JSON.parse(fs.readFileSync('D:/Mas3D/apps/unity/Anchors/anchors.json', 'utf8'));
const pad = 80;
const W = 1200;
const H = 1080;
const ix0 = data.interior.x0, ix1 = data.interior.x1, iz0 = data.interior.z0, iz1 = data.interior.z1;
const spanX = ix1 - ix0, spanZ = iz1 - iz0;
const scale = Math.min((W - pad * 2) / spanX, (H - pad * 2) / spanZ);
const sx = (x) => pad + (x - ix0) * scale;
const sy = (z) => H - pad - (z - iz0) * scale;
const box = (b) => ({ x: sx(b.min[0]), y: sy(b.max[2]), w: (b.max[0] - b.min[0]) * scale, h: (b.max[2] - b.min[2]) * scale });

const NAMES = {
  Mesh_0121out: 'bed', Mesh_0001out: 'bench', Mesh_0146out: 'desk', Mesh_0025out: 'desk kit',
  Mesh_0138out: 'drawer', Mesh_0014out: 'mirror', Mesh_0009out: 'door', Mesh_0106out: 'window',
  Mesh_0200out: 'shelf', Mesh_0126out: 'rug',
};
const parts = [];
parts.push('<rect width="100%" height="100%" fill="#14161c"/>');
parts.push(`<rect x="${sx(ix0)}" y="${sy(iz1)}" width="${spanX * scale}" height="${spanZ * scale}" fill="#1b1e26" stroke="#5b6478" stroke-width="2"/>`);
for (let g = Math.ceil(iz0); g <= Math.floor(iz1); g++)
  parts.push(`<line x1="${sx(ix0)}" y1="${sy(g)}" x2="${sx(ix1)}" y2="${sy(g)}" stroke="#262b37" stroke-width="1"/>`);
for (let g = Math.ceil(ix0); g <= Math.floor(ix1); g++)
  parts.push(`<line x1="${sx(g)}" y1="${sy(iz0)}" x2="${sx(g)}" y2="${sy(iz1)}" stroke="#262b37" stroke-width="1"/>`);

for (const o of data.obstacles) {
  const r = box(o);
  const tall = o.max[1] > 1.2;
  const low = o.max[1] <= 0.35;
  const fill = low ? '#2b2f3d' : tall ? '#3d4357' : '#323748';
  parts.push(`<rect x="${r.x}" y="${r.y}" width="${Math.max(r.w, 2)}" height="${Math.max(r.h, 2)}" fill="${fill}" stroke="#6b7590" stroke-width="1"/>`);
  const label = NAMES[o.name];
  if (label && r.w > 34 && r.h > 26) {
    parts.push(`<text x="${r.x + r.w / 2}" y="${r.y + r.h / 2 + 5}" fill="#aab3c6" font-family="Consolas,monospace" font-size="14" text-anchor="middle">${label}</text>`);
  }
}
for (const a of data.anchors)
  parts.push(`<circle cx="${sx(a.x)}" cy="${sy(a.z)}" r="${0.45 * scale}" fill="none" stroke="#3f7f5f" stroke-dasharray="6 6" stroke-width="1.5"/>`);

const palette = ['#ffd166', '#7ee0a8', '#7cc4ff', '#ff9ec4', '#f6a35c'];
data.anchors.forEach((a, i) => {
  const x = sx(a.x), y = sy(a.z);
  const colour = palette[i % palette.length];
  const rad = (a.facingY * Math.PI) / 180;
  parts.push(`<line x1="${x}" y1="${y}" x2="${x + Math.sin(rad) * 52}" y2="${y - Math.cos(rad) * 52}" stroke="${colour}" stroke-width="3"/>`);
  parts.push(`<circle cx="${x}" cy="${y}" r="9" fill="${colour}"/>`);

  // stack labels upward when the point sits low on the sheet, downward otherwise
  const below = y < H * 0.55;
  const dy = below ? 30 + (i % 3) * 20 : -18 - (i % 3) * 20;
  const tx = Math.min(x + 16, W - 360);
  parts.push(`<rect x="${tx - 6}" y="${y + dy - 16}" width="345" height="40" fill="#12141a" opacity="0.82"/>`);
  parts.push(`<text x="${tx}" y="${y + dy}" fill="${colour}" font-family="Consolas,monospace" font-size="17">${a.id} ${a.role} (${a.x.toFixed(2)}, ${a.z.toFixed(2)})</text>`);
  parts.push(`<text x="${tx}" y="${y + dy + 17}" fill="#9aa4b8" font-family="Consolas,monospace" font-size="13">wall ${a.wallClear.toFixed(2)} / ${a.nearest} ${a.nearestClear.toFixed(2)}</text>`);
});

data.seats.forEach((s) => {
  const x = sx(s.x), y = sy(s.z);
  parts.push(`<rect x="${x - 8}" y="${y - 8}" width="16" height="16" fill="#ff6b6b" transform="rotate(45 ${x} ${y})"/>`);
  parts.push(`<text x="${x + 14}" y="${y + 24}" fill="#ff8f8f" font-family="Consolas,monospace" font-size="15">${s.id} y=${s.y.toFixed(2)}</text>`);
});

parts.push(`<text x="${pad}" y="46" fill="#e6e9f0" font-family="Consolas,monospace" font-size="24">Unity anchors - plan view (+z up), 1 m grid, dashed = 0.45 m clearance` + '</text>');
parts.push(`<text x="${pad}" y="${H - 24}" fill="#7c869c" font-family="Consolas,monospace" font-size="14">interior ${spanX.toFixed(2)} x ${spanZ.toFixed(2)} m  |  capsule r=${data.thresholds.capsuleRadius} h=${data.thresholds.playerHeight}  |  wall >= ${data.thresholds.wall}  furniture >= ${data.thresholds.furniture}` + '</text>');

const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}">${parts.join('')}</svg>`;
fs.writeFileSync('D:/Mas3D/apps/unity/Anchors/plan.svg', svg);
await sharp(Buffer.from(svg)).png().toFile('D:/Mas3D/apps/unity/Anchors/plan.png');
console.log('plan written');
