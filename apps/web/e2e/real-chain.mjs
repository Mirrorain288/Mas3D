#!/usr/bin/env node
/**
 * 真实链路验收（需要 AstrBot + 桥接插件 + GPT-SoVITS 都在本机跑着）：
 * 打开页面 → 发一句 → 等到 Monika 回复 → 检查语音合成与口型 → 截图。
 *
 *   pnpm --filter @mas3d/web dev                 # 另开一个终端
 *   pnpm --filter @mas3d/web e2e:real -- --out ../../.superpowers/shots/real.png
 *
 * 令牌从 apps/bridge/data/mas3d/token 读（插件首次启动时生成）。
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../..");
const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const i = args.indexOf("--" + name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const out = argOf("out", path.join(repoRoot, ".superpowers/shots/real-chain.png"));
const userText = argOf("text", "我一直没睡，是因为有点害怕，你能陪我说说话吗？");
const tokenFile = path.join(repoRoot, "apps/bridge/data/mas3d/token");
const token = fs.readFileSync(tokenFile, "utf8").trim();
const pageUrl = argOf("url", "http://localhost:5173/");

const browser = await chromium.launch({
  channel: "msedge",
  args: [
    "--use-angle=swiftshader",
    "--enable-unsafe-swiftshader",
    "--ignore-gpu-blocklist",
    "--autoplay-policy=no-user-gesture-required",
  ],
});
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
const voiceCalls = [];
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => {
  if (m.type() === "error") errors.push("console: " + m.text());
});
page.on("response", (r) => {
  if (r.url().includes("/mas3d/api/voice/synth")) voiceCalls.push(r.status());
});

try {
  await page.goto(pageUrl + "?token=" + encodeURIComponent(token), { waitUntil: "load" });
  await page.waitForFunction(() => document.body.dataset.scene === "ready", null, { timeout: 90000 });
  await page.waitForFunction(() => Boolean(window.__mas3dAvatar && window.__mas3dAvatar.api), null, {
    timeout: 90000,
  });

  await page.fill(".chat-input textarea", userText);
  await page.press(".chat-input textarea", "Enter");
  const t0 = Date.now();
  await page.waitForFunction(
    () => document.querySelectorAll(".turn.monika").length >= 1 && document.body.dataset.phase === "idle",
    null,
    { timeout: 240000 },
  );
  const replyMs = Date.now() - t0;
  const replyText = await page.locator(".turn.monika").last().innerText();

  let maxMouth = 0;
  let seenSpeaking = false;
  const deadline = Date.now() + 40000;
  while (Date.now() < deadline) {
    const state = await page.evaluate(() => {
      const lip = window.__mas3dAvatar?.lip;
      return lip ? { speaking: Boolean(lip.speaking), mouth: Number(lip.lastMouth) || 0 } : null;
    });
    if (state?.speaking) seenSpeaking = true;
    if (state?.mouth) maxMouth = Math.max(maxMouth, state.mouth);
    if (seenSpeaking && maxMouth > 0.2) break;
    await page.waitForTimeout(120);
  }

  fs.mkdirSync(path.dirname(out), { recursive: true });
  await page.screenshot({ path: out });
  const transcript = await page.evaluate(() => localStorage.getItem("mas3d.transcript")?.length ?? 0);
  const result = { replyMs, voiceCalls, seenSpeaking, maxMouth, replyText, transcriptLength: transcript, errors };
  console.log(JSON.stringify(result, null, 2));
  const ok = voiceCalls.some((s) => s === 200) && seenSpeaking && maxMouth > 0.2 && errors.length === 0;
  if (!ok) process.exitCode = 1;
} catch (err) {
  const diag = await page
    .evaluate(() => ({
      phase: document.body.dataset.phase,
      bridge: document.body.dataset.bridge,
      monika: document.querySelectorAll(".turn.monika").length,
      text: document.querySelector(".chat-body")?.innerText?.slice(-600) ?? "",
    }))
    .catch(() => null);
  console.log(JSON.stringify({ failed: String(err).slice(0, 200), diag, errors, voiceCalls }, null, 2));
  await page.screenshot({ path: out }).catch(() => {});
  process.exitCode = 2;
} finally {
  await browser.close();
}
