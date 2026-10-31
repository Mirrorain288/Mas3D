#!/usr/bin/env node
/**
 * 剧本化 mock 桥接服务：只用 WebSocket，按 packages/protocol 的 ServerFrame 发帧。
 * 这是 Task 9/10 的测试替身，真实桥接在 AstrBot 插件里（Task 10）。
 *
 *   node tools/mock-bridge.mjs --port 8787 --scenario happy|timeout|error|command [--token TOKEN]
 */
import { createHash, randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { WebSocketServer } from "ws";

const SCENARIOS = new Set(["happy", "timeout", "error", "command"]);

function parseArgs(argv) {
  const options = { port: 8787, scenario: "happy", token: null, host: "127.0.0.1", quiet: false };
  for (let i = 0; i < argv.length; i += 1) {
    const flag = argv[i];
    const value = argv[i + 1];
    if (flag === "--port") {
      options.port = Number(value);
      i += 1;
    } else if (flag === "--scenario") {
      options.scenario = value;
      i += 1;
    } else if (flag === "--token") {
      options.token = value;
      i += 1;
    } else if (flag === "--host") {
      options.host = value;
      i += 1;
    } else if (flag === "--quiet") {
      options.quiet = true;
    } else if (flag === "--help" || flag === "-h") {
      process.stdout.write("usage: node tools/mock-bridge.mjs [--port 8787] [--scenario happy|timeout|error|command] [--token T] [--host 127.0.0.1]\n");
      process.exit(0);
    } else {
      process.stderr.write("unknown argument: " + flag + "\n");
      process.exit(2);
    }
  }
  if (!SCENARIOS.has(options.scenario)) {
    process.stderr.write("scenario must be one of " + [...SCENARIOS].join("|") + "\n");
    process.exit(2);
  }
  if (!Number.isInteger(options.port) || options.port <= 0 || options.port > 65535) {
    process.stderr.write("port must be a valid TCP port\n");
    process.exit(2);
  }
  return options;
}

const options = parseArgs(process.argv.slice(2));
const log = (...parts) => {
  if (!options.quiet) process.stdout.write("[mock-bridge] " + parts.join(" ") + "\n");
};

const PREFIX = "（3D教室）";

function cueFor(text) {
  const has = (...words) => words.some((word) => text.includes(word));
  if (has("谢谢", "开心", "喜欢", "太好了", "哈")) {
    return { emotion: "joy", intensity: 0.85, gaze: "player", gesture: has("谢谢") ? "nod" : "none" };
  }
  if (has("难过", "对不起", "抱歉", "累")) {
    return { emotion: "sorrow", intensity: 0.7, gaze: "down", gesture: "tilt" };
  }
  if (has("为什么", "?", "？")) {
    return { emotion: "surprised", intensity: 0.6, gaze: "player", gesture: "tilt" };
  }
  if (has("再见", "走了", "离开")) {
    return { emotion: "sorrow", intensity: 0.5, gaze: "away", gesture: "look_away" };
  }
  return { emotion: "neutral", intensity: 0.5, gaze: "player", gesture: "nod" };
}

function replyFor(text) {
  if (text.includes("在吗")) return PREFIX + "我在呀，一直坐在这儿等你。";
  if (text.includes("手")) return PREFIX + "手吗？我可以抬起来给你看看。";
  return PREFIX + "我听到你说的「" + text.slice(0, 40) + "」了。外面的星空很安静，我们可以慢慢聊。";
}

function commandPlan(text) {
  return {
    base: { change: false, name: "坐姿待机", motion: 0, rest: false },
    operation: "command",
    timing: "同时",
    intents: [
      { key: "右臂上抬", speed: 1, strength: Math.min(1, 0.6 + text.length / 100), hold: 2, delay: 0 },
    ],
    reply: "（她抬起右手，朝你晃了晃）",
  };
}

// --- 语音端点（Task 15：口型需要真实音频包络，脚本化 WAV 足够验证）-------------
const VOICE_RATE = 32000;
const VOICE_CACHE = new Map();
const VOICE_CACHE_LIMIT = 64;

function splitSentences(text) {
  const parts = [];
  let current = "";
  for (const char of text) {
    current += char;
    if ("。！？.!?".includes(char)) {
      if (current.trim()) parts.push(current.trim());
      current = "";
    }
  }
  if (current.trim()) parts.push(current.trim());
  return parts.length ? parts : [text];
}

/** 带音节振幅调制的单声道 WAV：AnalyserNode 能读到明显起伏，口型才不会平。 */
function makeWav(durationMs, seed) {
  const frames = Math.round((VOICE_RATE * durationMs) / 1000);
  const buffer = Buffer.alloc(44 + frames * 2);
  buffer.write("RIFF", 0, "ascii");
  buffer.writeUInt32LE(36 + frames * 2, 4);
  buffer.write("WAVEfmt ", 8, "ascii");
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(VOICE_RATE, 24);
  buffer.writeUInt32LE(VOICE_RATE * 2, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write("data", 36, "ascii");
  buffer.writeUInt32LE(frames * 2, 40);
  const base = 190 + (seed % 5) * 34;
  for (let index = 0; index < frames; index += 1) {
    const t = index / VOICE_RATE;
    const envelope = 0.55 + 0.45 * Math.sin(2 * Math.PI * 3.1 * t + seed);
    const syllable = 0.5 + 0.5 * Math.sin(2 * Math.PI * 4.4 * t);
    const sample = Math.sin(2 * Math.PI * base * t) * envelope * syllable * 0.42;
    buffer.writeInt16LE(Math.round(sample * 32767), 44 + index * 2);
  }
  return buffer;
}

function voicePayload(text, index) {
  const sentences = splitSentences(text);
  if (index < 0 || index >= sentences.length) return null;
  const sentence = sentences[index];
  const durationMs = Math.min(2600, Math.max(500, Math.round(sentence.length * 190)));
  const english = "mock sentence " + (index + 1) + " of " + sentences.length;
  const id = createHash("sha256").update(text + "|" + index + "|" + english).digest("hex").slice(0, 32);
  if (!VOICE_CACHE.has(id)) {
    VOICE_CACHE.set(id, makeWav(durationMs, index));
    while (VOICE_CACHE.size > VOICE_CACHE_LIMIT) {
      const oldest = VOICE_CACHE.keys().next().value;
      VOICE_CACHE.delete(oldest);
    }
    log("synth:", "sentence=" + index, "durationMs=" + durationMs);
  }
  return {
    id,
    audioUrl: "/mas3d/api/voice/" + id + ".wav",
    durationMs,
    sentenceStartsMs: [0],
    englishText: english,
    sentenceIndex: index,
    cached: false,
  };
}

function readBody(request, limitBytes = 64 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    request.on("data", (chunk) => {
      size += chunk.length;
      if (size > limitBytes) {
        reject(new Error("body too large"));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    request.on("error", reject);
  });
}

// --- 会话来源（风险 1：共享 / 专用，软上限只看不改历史）-----------------------
const SESSION = { mode: "shared", sharedTurns: 57, limit: 60 };

function sessionPayload() {
  return {
    mode: SESSION.mode,
    sharedTurns: SESSION.sharedTurns,
    limit: SESSION.limit,
    overLimit: SESSION.sharedTurns >= SESSION.limit,
    umo: SESSION.mode === "shared" ? "Monika:FriendMessage:2441388327" : "Monika:FriendMessage:mas3d-player",
  };
}

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "Authorization, Content-Type",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
};

function authorized(request, url) {
  if (!options.token) return true;
  const header = String(request.headers.authorization ?? "");
  const bearer = header.startsWith("Bearer ") ? header.slice(7) : null;
  return (bearer ?? url.searchParams.get("token")) === options.token;
}

const server = createServer((request, response) => {
  const url = new URL(request.url ?? "/", "http://" + options.host);
  const sendJson = (status, payload) => {
    response.writeHead(status, { ...CORS, "Content-Type": "application/json; charset=utf-8" });
    response.end(JSON.stringify(payload));
  };

  if (request.method === "OPTIONS") {
    response.writeHead(204, CORS);
    response.end();
    return;
  }
  if (url.pathname === "/mas3d/api/health") {
    sendJson(200, { ok: true, plugin: "mock", umo: "mock", degraded: false });
    return;
  }
  if (!authorized(request, url)) {
    sendJson(401, { ok: false, code: "bad_request", message: "缺少或错误的令牌" });
    return;
  }
  if (request.method === "GET" && url.pathname === "/mas3d/api/session") {
    sendJson(200, { ok: true, session: sessionPayload() });
    return;
  }
  if (request.method === "POST" && url.pathname === "/mas3d/api/session") {
    void readBody(request)
      .then((raw) => {
        let body;
        try {
          body = JSON.parse(raw);
        } catch {
          sendJson(400, { ok: false, code: "bad_request", message: "body 不是 JSON" });
          return;
        }
        const mode = typeof body?.mode === "string" ? body.mode : "";
        if (mode !== "shared" && mode !== "dedicated") {
          sendJson(400, { ok: false, code: "bad_request", message: "会话模式只能是 shared / dedicated" });
          return;
        }
        SESSION.mode = mode;
        log("session mode ->", mode);
        sendJson(200, { ok: true, session: sessionPayload() });
      })
      .catch(() => sendJson(400, { ok: false, code: "bad_request", message: "读取请求体失败" }));
    return;
  }
  if (request.method === "POST" && url.pathname === "/mas3d/api/voice/synth") {
    void readBody(request)
      .then((raw) => {
        let body;
        try {
          body = JSON.parse(raw);
        } catch {
          sendJson(400, { ok: false, code: "bad_request", message: "body 不是 JSON" });
          return;
        }
        const text = typeof body?.text === "string" ? body.text.trim() : "";
        if (!text || text.length > 600) {
          sendJson(400, { ok: false, code: "bad_request", message: "文本为空或超过 600 字" });
          return;
        }
        const index = body?.sentence === undefined ? 0 : Number(body.sentence);
        if (!Number.isInteger(index) || index < 0) {
          sendJson(400, { ok: false, code: "bad_request", message: "句子下标不合法" });
          return;
        }
        const voice = voicePayload(text, index);
        if (!voice) {
          sendJson(400, { ok: false, code: "bad_request", message: "句子下标越界" });
          return;
        }
        sendJson(200, { ok: true, voice });
      })
      .catch(() => sendJson(400, { ok: false, code: "bad_request", message: "读取请求体失败" }));
    return;
  }
  const audio = /^\/mas3d\/api\/voice\/([0-9a-f]{32})\.wav$/.exec(url.pathname);
  if (request.method === "GET" && audio) {
    const buffer = VOICE_CACHE.get(audio[1]);
    if (!buffer) {
      sendJson(404, { ok: false, code: "bad_request", message: "音频不存在" });
      return;
    }
    response.writeHead(200, { ...CORS, "Content-Type": "audio/wav", "Content-Length": String(buffer.length) });
    response.end(buffer);
    return;
  }
  sendJson(404, { ok: false, code: "bad_request", message: "未知端点" });
});

function noteTurn() {
  if (SESSION.mode === "shared") SESSION.sharedTurns += 1;
}

const wss = new WebSocketServer({ server, path: "/mas3d/ws" });

server.on("listening", () => {
  log("listening on ws://" + options.host + ":" + options.port + "/mas3d/ws", "scenario=" + options.scenario, options.token ? "token=on" : "token=off");
});

wss.on("connection", (socket, request) => {
  const url = new URL(request.url ?? "/mas3d/ws", "http://" + options.host);
  if (options.token) {
    const provided = url.searchParams.get("token");
    if (provided !== options.token) {
      log("reject: bad token");
      socket.close(1008, "bad token");
      return;
    }
  }
  log("client connected");

  const timers = new Set();
  const later = (delayMs, fn) => {
    const timer = setTimeout(() => {
      timers.delete(timer);
      if (socket.readyState === socket.OPEN) fn();
    }, delayMs);
    timers.add(timer);
  };

  socket.on("message", (raw) => {
    let frame;
    try {
      frame = JSON.parse(String(raw));
    } catch {
      socket.send(JSON.stringify({ type: "error", code: "bad_request", message: "frame is not JSON" }));
      return;
    }
    if (frame?.type !== "say" || typeof frame.text !== "string" || frame.text.trim() === "") {
      socket.send(JSON.stringify({ type: "error", code: "bad_request", message: "expected {type:'say',text}" }));
      return;
    }

    const text = frame.text.trim();
    const turnId = randomUUID();
    log("say:", text, "scenario=" + options.scenario);

    socket.send(JSON.stringify({ type: "ack", turnId }));
    later(320, () => socket.send(JSON.stringify({ type: "thinking", turnId })));

    if (options.scenario === "timeout") {
      log("scenario timeout: 不发 reply，让客户端 8 秒后自己判超时");
      return;
    }

    if (options.scenario === "error") {
      later(700, () => socket.send(JSON.stringify({ type: "error", turnId, code: "model_error", message: "模型后端暂时不可用（mock）" })));
      return;
    }

    later(760, () => {
      noteTurn();
      socket.send(JSON.stringify({ type: "reply", turnId, text: replyFor(text), cue: cueFor(text) }));
    });

    if (options.scenario === "command") {
      later(980, () => socket.send(JSON.stringify({ type: "intent", turnId, plan: commandPlan(text) })));
    }
  });

  socket.on("close", () => {
    for (const timer of timers) clearTimeout(timer);
    timers.clear();
    log("client disconnected");
  });
});

server.listen(options.port, options.host);

server.on("error", (error) => {
  process.stderr.write("[mock-bridge] " + (error instanceof Error ? error.message : String(error)) + "\n");
  process.exit(1);
});

const shutdown = () => {
  log("shutting down");
  for (const client of wss.clients) client.terminate();
  wss.close();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 300).unref();
};
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
