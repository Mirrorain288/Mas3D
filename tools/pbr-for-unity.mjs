#!/usr/bin/env node
// Copy the WebP PBR sets the web room dressing uses into PNG files Unity can import.
// Unity's texture importer has no WebP decoder, so the Unity side needs its own copy.
//
//   node tools/pbr-for-unity.mjs
//
// Web keeps the WebP originals; this derivative only exists for apps/unity.

import fs from "node:fs";
import path from "node:path";
import sharp from "sharp";

const SOURCE = path.resolve("apps/web/public/assets/pbr");
const TARGET = path.resolve("apps/unity/Assets/_Local/pbr");
// The imported room keeps its own textures for everything else; these two sets are
// what roomDressing.ts paints over the bare white meshes and the roof.
const BASES = ["ceiling", "clay"];
const MAPS = ["color", "normal", "roughness"];

fs.mkdirSync(TARGET, { recursive: true });

let bytes = 0;
for (const base of BASES) {
  for (const map of MAPS) {
    const name = base + "-" + map + "@1k.webp";
    const from = path.join(SOURCE, name);
    const to = path.join(TARGET, base + "-" + map + "@1k.png");
    if (!fs.existsSync(from)) {
      console.error("missing " + from);
      process.exitCode = 1;
      continue;
    }
    const image = sharp(from).png({ compressionLevel: 9 });
    await image.toFile(to);
    const size = fs.statSync(to).size;
    bytes += size;
    console.log(name + " -> " + path.relative(process.cwd(), to) + " (" + size + " B)");
  }
}
console.log("total " + bytes + " B");
