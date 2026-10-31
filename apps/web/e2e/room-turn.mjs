#!/usr/bin/env node
/**
 * 诊断用：把房间本身绕 Y 转 deg 度，让某一面墙正对相机（相机是受限轨道，转不过去）。
 * 不改产品代码，只在页面里改 __mas3dClassroom.root 的旋转。
 *
 *   node e2e/room-turn.mjs [--room URL] [--deg 90] [--shot out.png] [--port 5189]
 */
import { fileURLToPath } from "node:url";
import path from "node:path";
import { mkdirSync } from "node:fs";
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
const DEG = Number(argOf("--deg", "90"));
const PORT = Number(argOf("--port", "5189"));
const SHOT = path.resolve(argOf("--shot", path.join(repoRoot, ".superpowers", "shots", "room-turn.png")));

const vite = await import("vite");
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

let exitCode = 1;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  await page.goto("http://localhost:" + PORT + "/", { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => Boolean(window.__mas3dAvatar && window.__mas3dAvatar.api), null, { timeout: 120000 });
  await page.waitForTimeout(1500);
  const applied = await page.evaluate((deg) => {
    const room = window.__mas3dClassroom;
    if (!room) return null;
    room.root.rotation.y += (deg * Math.PI) / 180;
    return room.root.rotation.y;
  }, DEG);
  await page.waitForTimeout(900);
  mkdirSync(path.dirname(SHOT), { recursive: true });
  await page.screenshot({ path: SHOT });
  process.stdout.write(JSON.stringify({ shot: SHOT, deg: DEG, rotationY: applied, pageErrors }, null, 2) + "\n");
  exitCode = pageErrors.length === 0 ? 0 : 1;
} finally {
  await browser.close();
  await server.close();
}
process.exit(exitCode);
