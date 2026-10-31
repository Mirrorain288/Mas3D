#!/usr/bin/env node
/**
 * 场景替换试跑：用 VITE_ROOM_GLB 换掉程序化教室，拍一张座位视角的截图。
 *
 *   node e2e/room-preview.mjs [--room URL] [--shot out.png] [--port 5187]
 */
import { fileURLToPath } from "node:url";
import path from "node:path";
import { writeFileSync, mkdirSync } from "node:fs";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, "..");
const repoRoot = path.resolve(webRoot, "..", "..");

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const ROOM = argOf("--room", "");
const VARIANT = argOf("--variant", "");
const PORT = Number(argOf("--port", "5187"));
const SHOT = path.resolve(argOf("--shot", path.join(repoRoot, ".superpowers", "shots", "room-preview.png")));

const vite = await import("vite");
const server = await vite.createServer({
  root: webRoot,
  configFile: path.join(webRoot, "vite.config.ts"),
  server: { port: PORT, host: "localhost", strictPort: true },
  define: {
    "import.meta.env.VITE_ROOM": JSON.stringify(VARIANT),
    "import.meta.env.VITE_ROOM_GLB": JSON.stringify(ROOM),
    // 探针不测对话：钉一个不会响应的地址，避免误打 8790 的生产桥接
    "import.meta.env.VITE_MAS3D_WS": JSON.stringify("ws://127.0.0.1:1/mas3d/ws"),
  },
  logLevel: "silent",
});
await server.listen();

const browser = await chromium.launch({
  channel: "msedge",
  args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"],
});

let exitCode = 1;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const pageErrors = [];
  const consoleErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });
  await page.goto("http://localhost:" + PORT + "/", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => Boolean(window.__mas3dAvatar && window.__mas3dAvatar.api), null, { timeout: 120000 });
  await page.waitForTimeout(1200);

  const report = await page.evaluate(() => {
    const hook = window.__mas3dAvatar;
    const room = window.__mas3dClassroom;
    const box = room ? new room.root.constructor().constructor === undefined : null;
    const meshes = [];
    let tris = 0;
    let materials = new Set();
    let textures = new Set();
    let minY = Infinity;
    let maxY = -Infinity;
    let box3 = null;
    if (room) {
      const THREEBox = window.__mas3dBox3;
      room.root.traverse((object) => {
        if (!object.isMesh) return;
        meshes.push(object.name || "(unnamed)");
        const geometry = object.geometry;
        const count = geometry.index ? geometry.index.count : geometry.attributes.position.count;
        tris += count / 3;
        for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
          if (!material) continue;
          materials.add(material.uuid);
          if (material.map) textures.add(material.map.uuid);
        }
      });
    }
    return {
      seat: hook.pose(),
      meshes: meshes.length,
      tris: Math.round(tris),
      materials: materials.size,
      textures: textures.size,
      sampleNames: meshes.slice(0, 6),
      camera: (() => {
        const camera = hook.stats ? null : null;
        return null;
      })(),
    };
  });

  mkdirSync(path.dirname(SHOT), { recursive: true });
  await page.screenshot({ path: SHOT });
  writeFileSync(SHOT.replace(/\.png$/, ".json"), JSON.stringify({ report, pageErrors, consoleErrors }, null, 2) + "\n", "utf8");
  process.stdout.write(JSON.stringify({ room: ROOM, variant: VARIANT, ...report, pageErrors, consoleErrors, shot: SHOT }, null, 2) + "\n");
  exitCode = pageErrors.length === 0 ? 0 : 1;
} catch (error) {
  process.stdout.write("preview failed: " + (error instanceof Error ? error.stack ?? error.message : String(error)) + "\n");
} finally {
  await browser.close().catch(() => {});
  await server.close().catch(() => {});
}

process.exit(exitCode);
