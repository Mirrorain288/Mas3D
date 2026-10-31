#!/usr/bin/env node
/** 打印导入房间在世界坐标下的网格包围盒，用来判断空墙在哪一面、往哪儿加家具。 */
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import * as vite from "vite";

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, "..");
const args = process.argv.slice(2);
const argOf = (n, d) => { const i = args.indexOf(n); return i >= 0 && args[i + 1] ? args[i + 1] : d; };
const ROOM = argOf("--room", "/assets/room/mita-room.glb");
const PORT = Number(argOf("--port", "5191"));

const server = await vite.createServer({
  root: webRoot,
  configFile: path.join(webRoot, "vite.config.ts"),
  server: { port: PORT, host: "localhost", strictPort: true },
  define: {
    "import.meta.env.VITE_ROOM_GLB": JSON.stringify(ROOM),
    "import.meta.env.VITE_MAS3D_WS": JSON.stringify("ws://127.0.0.1:1/mas3d/ws"),
  },
  logLevel: "silent",
});
await server.listen();
const browser = await chromium.launch({
  channel: "msedge",
  args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"],
});
try {
  const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
  await page.goto("http://localhost:" + PORT + "/", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => Boolean(window.__mas3dClassroom), null, { timeout: 120000 });
  await page.waitForTimeout(900);
  const meshes = await page.evaluate(() => {
    const build = window.__mas3dClassroom;
    const out = [];
    build.root.updateWorldMatrix(true, true);
    build.root.traverse((child) => {
      if (!child.isMesh) return;
      const g = child.geometry;
      if (!g.boundingBox) g.computeBoundingBox();
      const b = g.boundingBox.clone().applyMatrix4(child.matrixWorld);
      out.push({
        name: child.name,
        min: [b.min.x, b.min.y, b.min.z].map((v) => Math.round(v * 100) / 100),
        max: [b.max.x, b.max.y, b.max.z].map((v) => Math.round(v * 100) / 100),
      });
    });
    return out;
  });
  process.stdout.write(JSON.stringify(meshes, null, 0) + "\n");
} finally {
  await browser.close().catch(() => {});
  await server.close().catch(() => {});
}
