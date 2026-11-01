
import { chromium } from "playwright";
import fs from "node:fs";

const token = fs.readFileSync("D:/Mas3D/apps/bridge/data/mas3d/token", "utf8").trim();
const url = process.argv[2] ?? "http://localhost:5173/?token=" + token;

const browser = await chromium.launch({ channel: "msedge", args: ["--use-gl=swiftshader", "--enable-unsafe-swiftshader"] });
const page = await browser.newPage({ viewport: { width: 900, height: 640 } });
await page.goto(url, { waitUntil: "load" });
await page.waitForFunction(() => window.__mas3dClassroom?.root, null, { timeout: 90000 });
await page.waitForTimeout(2500);

const out = await page.evaluate(async () => {
  const THREE = await import("/node_modules/three/build/three.module.js");
  const hunt = (o) => {
    if (o.name === "Mesh_0116out") return o;
    for (const c of o.children) { const hit = hunt(c); if (hit) return hit; }
    return null;
  };
  const roof = hunt(window.__mas3dClassroom.root);
  if (!roof) return { error: "no roof mesh" };
  roof.updateWorldMatrix(true, false);
  const g = roof.geometry;
  const pos = g.getAttribute("position");
  const idx = g.index;
  const count = idx ? idx.count : pos.count;
  const v = new THREE.Vector3();
  const tri = [];
  for (let i = 0; i < count; i += 3) {
    const ys = [];
    for (let k = 0; k < 3; k += 1) {
      const vi = idx ? idx.getX(i + k) : i + k;
      v.fromBufferAttribute(pos, vi).applyMatrix4(roof.matrixWorld);
      ys.push(v.y);
    }
    tri.push({ y: Math.max(...ys), ymin: Math.min(...ys) });
  }
  const low = tri.filter((t) => t.y <= 2.9);
  const high = tri.filter((t) => t.y > 2.9);
  const bbox = (arr) => {
    const xs = arr.map((t) => t.ymin);
    return { min: Math.min(...xs), max: Math.max(...arr.map((t) => t.y)) };
  };
  return {
    total: tri.length,
    lowCount: low.length,
    highCount: high.length,
    lowY: low.length ? bbox(low) : null,
    highY: high.length ? bbox(high) : null,
    attrs: Object.keys(g.attributes),
    visibleNow: roof.visible,
  };
});
console.log(JSON.stringify(out));
await browser.close();
