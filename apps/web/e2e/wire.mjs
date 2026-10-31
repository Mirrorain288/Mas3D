#!/usr/bin/env node
/**
 * 帧契约回归（不需要桥接、不需要模型）：用假 WebSocket 接住页面真正的收发帧，
 * 断言「客户端带 turnId」「ack 丢了也认得出回复」「别的回合的回复仍然被丢弃」。
 *
 *   node e2e/wire.mjs
 */
import { fileURLToPath } from "node:url";
import path from "node:path";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, "..");
const PORT = 5185;

const consoleErrors = [];
const results = [];
let failures = 0;
function check(name, ok, detail) {
  results.push({ name, ok });
  if (!ok) failures += 1;
  process.stdout.write((ok ? "  ok   " : "  FAIL ") + name + (detail ? "  (" + detail + ")" : "") + "\n");
}

// 契约测试不该碰真桥接（默认 8790），否则语音请求会打到生产端口上，还可能写 QQ
process.env.VITE_MAS3D_WS = "ws://localhost:" + PORT + "/mas3d/ws";
process.env.VITE_MAS3D_TOKEN = "wire-test";

const vite = await import("vite");
const server = await vite.createServer({
  root: webRoot,
  configFile: path.join(webRoot, "vite.config.ts"),
  server: { port: PORT, host: "localhost", strictPort: true },
  logLevel: "silent",
});
await server.listen();
const pageUrl = "http://localhost:" + PORT + "/";

/** 假 WebSocket：只记录发出的帧，并允许测试把服务端帧推进来。 */
const shim = () => {
  const sent = [];
  let latest = null;
  class FakeWS {
    static CONNECTING = 0;
    static OPEN = 1;
    static CLOSING = 2;
    static CLOSED = 3;
    constructor() {
      this.readyState = 1;
      this.onopen = null;
      this.onclose = null;
      this.onmessage = null;
      this.onerror = null;
      latest = this;
      setTimeout(() => { if (this.onopen) this.onopen({}); }, 0);
    }
    send(data) { sent.push(JSON.parse(data)); }
    close() { this.readyState = 3; }
    addEventListener() {}
  }
  window.__fake = {
    sent,
    push: (frame) => { if (latest && latest.onmessage) latest.onmessage({ data: JSON.stringify(frame) }); },
    get latest() { return latest; },
  };
  window.WebSocket = FakeWS;
};

const browser = await chromium.launch({
  channel: "msedge",
  args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"],
});

let exitCode = 1;
try {
  async function fresh(options = {}) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    if (options.sessionRoute) await page.route("**/mas3d/api/session", options.sessionRoute);
    // 回合 id 与消息 id 曾经撞在一起（两条 Turn 同一个 key），React 只报 console 错误，
    // 界面上看起来只是"少了一条" —— 所以这类错误必须当断言，不能只靠肉眼看截图
    page.on("console", (msg) => {
      if (msg.type() !== "error") return;
      // 这个页面没有真桥接，"Failed to load resource: 401/404" 是预期噪声；
      // 但 React 的 key 冲突、未捕获异常都走 console.error，必须抓
      if (msg.text().startsWith("Failed to load resource")) return;
      const where = msg.location();
      consoleErrors.push(msg.text() + (where && where.url ? " @ " + where.url : ""));
    });
    page.on("pageerror", (err) => consoleErrors.push("pageerror: " + err.message));
    await page.addInitScript(() => window.localStorage.clear());
    await page.addInitScript(shim);
    await page.goto(pageUrl, { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".chat-input textarea", { timeout: 60000 });
    await page.waitForFunction(
      () => Boolean(window.__fake && window.__fake.latest && window.__fake.latest.onmessage),
      null,
      { timeout: 30000 },
    );
    await page.waitForFunction(() => document.body.dataset.bridge === "online", null, { timeout: 20000 });
    return page;
  }

  async function say(page, text) {
    await page.fill(".chat-input textarea", text);
    await page.press(".chat-input textarea", "Enter");
    await page.waitForTimeout(200);
  }

  // 1) 发出的 say 帧必须带 turnId —— 服务端要回显它，客户端才能认领回复
  {
    const page = await fresh();
    await say(page, "帧契约一");
    const sent = await page.evaluate(() => window.__fake.sent);
    const frame = sent[0] ?? {};
    check("say 帧带 turnId", typeof frame.turnId === "string" && frame.turnId.length > 0, JSON.stringify(frame.turnId ?? null));
    check("say 帧类型与文本正确", frame.type === "say" && frame.text === "帧契约一", frame.type);
    await page.close();
  }

  // 2) 只有 reply、没有 ack：乐观登记的 id 仍然认领得到（ack 丢包不再等于回复丢失）
  {
    const page = await fresh();
    await say(page, "帧契约二");
    const turnId = await page.evaluate(() => window.__fake.sent[0].turnId);
    await page.evaluate((id) => window.__fake.push({ type: "reply", turnId: id, text: "没有 ack 的回复" }), turnId);
    // 首屏 vite 还在冷编译时，固定 sleep 会偶发读到旧 phase（曾复现一次）
    await page.waitForFunction(() => document.body.dataset.phase === "idle", null, { timeout: 5000 }).catch(() => {});
    const dom = await page.evaluate(() => ({ phase: document.body.dataset.phase, text: document.querySelector(".turn.monika .turn-text")?.textContent ?? null }));
    check("ack 丢了也收得下回复", dom.phase === "idle" && dom.text === "没有 ack 的回复", JSON.stringify(dom));
    await page.close();
  }

  // 3) 别的回合的回复仍然被丢弃（一问两答的防护不能因为乐观登记而失效）
  {
    const page = await fresh();
    await say(page, "帧契约三");
    const turnId = await page.evaluate(() => window.__fake.sent[0].turnId);
    await page.evaluate((id) => {
      window.__fake.push({ type: "ack", turnId: id });
      window.__fake.push({ type: "reply", turnId: "someone-else", text: "别的回合的回复" });
    }, turnId);
    await page.waitForTimeout(400);
    const dom = await page.evaluate(() => ({ phase: document.body.dataset.phase, text: document.querySelector(".turn.monika .turn-text")?.textContent ?? null }));
    check("别的回合的回复被丢弃", dom.phase === "thinking" && dom.text === null, JSON.stringify(dom));
    await page.close();
  }

  // 4) error 之后的迟到回复不再落到界面上
  {
    const page = await fresh();
    await say(page, "帧契约四");
    const turnId = await page.evaluate(() => window.__fake.sent[0].turnId);
    await page.evaluate((id) => {
      window.__fake.push({ type: "ack", turnId: id });
      window.__fake.push({ type: "error", code: "timeout", message: "模型这次没有回应", turnId: id });
    }, turnId);
    await page.waitForTimeout(300);
    await page.evaluate((id) => window.__fake.push({ type: "reply", turnId: id, text: "迟到的回复" }), turnId);
    await page.waitForTimeout(400);
    const dom = await page.evaluate(() => ({ phase: document.body.dataset.phase, text: document.querySelector(".turn.monika .turn-text")?.textContent ?? null }));
    check("error 后的迟到回复不显示", dom.phase === "error" && dom.text === null, JSON.stringify(dom));
    await page.close();
  }

  // 5) 迟到的动作计划：回复之后才到的 intent 只认「刚回复的那回合」，
  //    玩家已经问下一句时必须丢弃（否则上一回合的手臂会突然抬起来）
  {
    const page = await fresh();
    const plan = {
      base: { change: false, name: "待机", motion: 0, rest: true },
      operation: "command",
      timing: "同时",
      intents: [{ key: "右臂上抬", speed: 1, strength: 1, hold: 2, delay: 0 }],
      reply: "",
    };
    await say(page, "帧契约五");
    const turnId = await page.evaluate(() => window.__fake.sent[0].turnId);
    await page.evaluate((args) => {
      window.__fake.push({ type: "ack", turnId: args.id });
      window.__fake.push({ type: "reply", turnId: args.id, text: "这一回合的回复" });
    }, { id: turnId });
    await page.waitForTimeout(300);
    await page.evaluate((args) => window.__fake.push({ type: "intent", turnId: args.id, plan: args.plan }), { id: turnId, plan });
    await page.waitForTimeout(200);
    const accepted = await page.evaluate(() => window.__mas3dIntent);

    // 玩家追问下一句 → 上一回合的计划即使到了也不该动
    await say(page, "帧契约五之后");
    await page.evaluate((args) => window.__fake.push({ type: "intent", turnId: args.id, plan: args.plan }), { id: turnId, plan });
    await page.waitForTimeout(200);
    const after = await page.evaluate(() => window.__mas3dIntent);
    check("本回合的 intent 被应用", accepted.applied === 1 && accepted.dropped === 0, JSON.stringify(accepted));
    check("下一句之后的迟到 intent 被丢弃", after.applied === 1 && after.dropped === 1, JSON.stringify(after));
    await page.close();
  }

  // 6) 会话来源：超过软上限时提示，并可一键切到专用会话
  {
    const posts = [];
    let mode = "shared";
    const route = async (route) => {
      const method = route.request().method();
      if (method === "POST") {
        posts.push(route.request().postDataJSON());
        mode = posts[posts.length - 1].mode;
      }
      const payload = {
        ok: true,
        session: {
          mode,
          sharedTurns: 61,
          limit: 60,
          overLimit: mode === "shared",
          umo: mode === "shared" ? "Monika:FriendMessage:2441388327" : "Monika:FriendMessage:mas3d-player",
        },
      };
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(payload) });
    };
    const page = await fresh({ sessionRoute: route });
    await page.waitForSelector(".chat-cap", { timeout: 10000 });
    const hint = await page.textContent(".chat-cap span");
    await page.click(".chat-cap button");
    await page.waitForFunction(() => document.body.dataset.session === "dedicated", null, { timeout: 10000 });
    const banner = await page.locator(".chat-cap").count();
    check("超过软上限时给出提示", typeof hint === "string" && hint.includes("61"), hint ?? "");
    check("一键切到专用会话", posts.length === 1 && posts[0].mode === "dedicated" && banner === 0, JSON.stringify(posts));
    await page.close();
  }

  check("运行时没有 console 错误", consoleErrors.length === 0, consoleErrors.slice(0, 2).join(" | "));

  exitCode = failures === 0 ? 0 : 1;
} finally {
  await browser.close();
  await server.close();
}

process.stdout.write((failures === 0 ? "wire ok (" : "wire FAILED (") + results.length + " checks)\n");
process.exitCode = exitCode;