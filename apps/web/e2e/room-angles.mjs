#!/usr/bin/env node
/**
 * 座位弧四机位：默认 / 左转 45° / 右转 45° / 拉远抬高。
 * 相机是受限轨道相机（yaw ±45、pitch -10..25、distance 1.6..3.2），
 * 这里用真实指针输入去拖，走的就是玩家自己的那条路径，不碰内部状态。
 *
 *   node e2e/room-angles.mjs [--room URL | --variant bedroom] [--dir 输出目录]
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
const VARIANT = argOf("--variant", "");
const PORT = Number(argOf("--port", "5188"));
const DIR = path.resolve(argOf("--dir", path.join(repoRoot, ".superpowers", "shots", "angles")));

// 相机初始值，和 apps/web/src/scene/cameraRig.ts 里的 orbit 初值一致
const START = { yaw: 0, pitch: 6, distance: 2.95 };
const SHOTS = [
  { name: "01-default", yaw: 0, pitch: 6, distance: 2.95 },
  { name: "02-left45", yaw: -45, pitch: 6, distance: 2.95 },
  { name: "03-right45", yaw: 45, pitch: 6, distance: 2.95 },
  { name: "04-wide", yaw: 0, pitch: 20, distance: 3.2 },
];

const vite = await import("vite");
const server = await vite.createServer({
  root: webRoot,
  configFile: path.join(webRoot, "vite.config.ts"),
  server: { port: PORT, host: "localhost", strictPort: true },
  define: {
    "import.meta.env.VITE_ROOM": JSON.stringify(VARIANT),
    "import.meta.env.VITE_ROOM_GLB": JSON.stringify(ROOM),
    "import.meta.env.VITE_ROOM_DECOR": JSON.stringify(process.env.VITE_ROOM_DECOR ?? ""),
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
  await page.waitForTimeout(1200);

  const box = await page.locator("canvas").first().boundingBox();
  if (!box) throw new Error("找不到 canvas");
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  let current = { ...START };
  mkdirSync(DIR, { recursive: true });

  const written = [];
  for (const shot of SHOTS) {
    // 拖动换算：yaw -= dx * 0.18，pitch += dy * 0.12（见 cameraRig.ts）
    const dx = (current.yaw - shot.yaw) / 0.18;
    const dy = (shot.pitch - current.pitch) / 0.12;
    if (Math.abs(dx) > 0.5 || Math.abs(dy) > 0.5) {
      await page.mouse.move(cx, cy);
      await page.mouse.down();
      await page.mouse.move(cx + dx, cy + dy, { steps: 14 });
      await page.mouse.up();
    }
    // 滚轮换算：distance += deltaY * 0.0012
    const delta = shot.distance - current.distance;
    if (Math.abs(delta) > 0.001) {
      await page.mouse.move(cx, cy);
      await page.mouse.wheel(0, delta / 0.0012);
    }
    await page.waitForTimeout(350);
    const file = path.join(DIR, shot.name + ".png");
    await page.screenshot({ path: file });
    written.push(file);
    current = { yaw: shot.yaw, pitch: shot.pitch, distance: shot.distance };
  }

  process.stdout.write(JSON.stringify({ room: ROOM, variant: VARIANT, shots: written, pageErrors }, null, 2) + "\n");
  exitCode = pageErrors.length === 0 ? 0 : 1;
} catch (error) {
  process.stdout.write("angles failed: " + (error instanceof Error ? (error.stack ?? error.message) : String(error)) + "\n");
} finally {
  await browser.close().catch(() => {});
  await server.close().catch(() => {});
}

process.exit(exitCode);