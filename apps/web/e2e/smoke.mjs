#!/usr/bin/env node
/**
 * Playwright 冒烟（tasks.md 10.2）：Vite 开发服务器 + 剧本化 mock 桥接。
 * 断言链路：场景挂载 → 发送一句 → 收到回复 → 表情形变发生变化 → 断开桥接出现横幅。
 *
 *   node e2e/smoke.mjs
 */
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, "..");
const repoRoot = path.resolve(webRoot, "..", "..");
const PORT = 5183;
const BRIDGE_PORT = 8787; // 与 ChatPanel 默认 BRIDGE_URL 一致

const results = [];
let failures = 0;

function check(name, ok, detail) {
  results.push({ name, ok, detail });
  if (!ok) failures += 1;
  process.stdout.write((ok ? "  ok   " : "  FAIL ") + name + (detail ? "  (" + detail + ")" : "") + "\n");
}

async function waitForPort(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) return true;
    } catch {
      /* 还没起来 */
    }
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  return false;
}

const children = [];
function stopChildren() {
  for (const child of children) {
    if (!child.killed) child.kill();
  }
}

const vite = await import("vite");
const server = await vite.createServer({
  root: webRoot,
  configFile: path.join(webRoot, "vite.config.ts"),
  server: { port: PORT, host: "localhost", strictPort: true },
  // 产品默认连 8790 的真实桥接；冒烟测试把地址钉到 mock
  define: {
    "import.meta.env.VITE_MAS3D_WS": JSON.stringify("ws://localhost:" + BRIDGE_PORT + "/mas3d/ws"),
  },
  logLevel: "silent",
});
await server.listen();
const pageUrl = "http://localhost:" + PORT + "/";

const bridge = spawn(
  process.execPath,
  [path.join(repoRoot, "tools", "mock-bridge.mjs"), "--port", String(BRIDGE_PORT), "--scenario", "happy", "--quiet"],
  { cwd: repoRoot, stdio: "ignore" },
);
children.push(bridge);

const bridgeAlive = await waitForPort("http://127.0.0.1:" + BRIDGE_PORT + "/mas3d/api/health", 15000);
check("mock 桥接启动", bridgeAlive, "port " + BRIDGE_PORT);

const browser = await chromium.launch({
  channel: "msedge",
  args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist", "--autoplay-policy=no-user-gesture-required"],
});

let exitCode = 1;
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  const pageErrors = [];
  page.on("pageerror", (error) => pageErrors.push(String(error)));

  await page.addInitScript(() => {
    window.localStorage.clear();
  });
  await page.goto(pageUrl, { waitUntil: "domcontentloaded" });

  await page.waitForFunction(() => document.body.dataset.scene === "ready", null, { timeout: 90000 });
  check("场景挂载", true, "data-scene=ready");

  await page.waitForFunction(() => Boolean(window.__mas3dAvatar && window.__mas3dAvatar.api), null, { timeout: 90000 });
  check("角色与表情驱动就绪", true, "window.__mas3dAvatar.api");

  await page.waitForFunction(() => document.body.dataset.bridge === "online", null, { timeout: 20000 });
  check("桥接在线", true, "data-bridge=online");

  await page.click("textarea");
  await page.type("textarea", "谢谢你陪着我");
  await page.keyboard.press("Enter");

  await page.waitForFunction(
    () => document.body.dataset.phase === "idle" && document.querySelectorAll(".turn.monika").length > 0,
    null,
    { timeout: 20000 },
  );
  const reply = await page.evaluate(() => {
    const node = document.querySelector(".turn.monika .turn-text");
    return node ? node.textContent : null;
  });
  check("收到回复且已剥离场景前缀", Boolean(reply) && !String(reply).includes("（3D教室）"), String(reply).slice(0, 24));

  const joy = await page.evaluate(() => {
    const meshes = [];
    window.__mas3dAvatar.vrm.scene.traverse((object) => {
      if (object.isMesh && object.morphTargetInfluences) meshes.push(object);
    });
    return meshes.reduce((max, mesh) => Math.max(max, mesh.morphTargetInfluences?.[3] ?? 0), 0);
  });
  check("表情形变被驱动（Fcl_ALL_Joy）", joy > 0.2, "weight=" + joy.toFixed(3));

  bridge.kill();
  await page.waitForSelector(".chat-offline", { state: "visible", timeout: 8000 });
  check("断开桥接出现横幅", true, ".chat-offline");

  check("无未捕获异常", pageErrors.length === 0, pageErrors.join(" | ").slice(0, 120));
  exitCode = failures === 0 ? 0 : 1;
} catch (error) {
  check("冒烟执行", false, error instanceof Error ? error.message : String(error));
  exitCode = 1;
} finally {
  await browser.close().catch(() => {});
  stopChildren();
  await server.close().catch(() => {});
}

process.stdout.write("\n" + (exitCode === 0 ? "smoke ok (" + results.length + " checks)" : "smoke failed (" + failures + "/" + results.length + ")") + "\n");
process.exit(exitCode);
