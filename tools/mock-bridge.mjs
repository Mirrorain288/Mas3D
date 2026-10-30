#!/usr/bin/env node
/**
 * 剧本化 mock 桥接服务：只用 WebSocket，按 packages/protocol 的 ServerFrame 发帧。
 * 这是 Task 9/10 的测试替身，真实桥接在 AstrBot 插件里（Task 10）。
 *
 *   node tools/mock-bridge.mjs --port 8787 --scenario happy|timeout|error|command [--token TOKEN]
 */
import { randomUUID } from "node:crypto";
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

const server = new WebSocketServer({ host: options.host, port: options.port, path: "/mas3d/ws" });

server.on("listening", () => {
  log("listening on ws://" + options.host + ":" + options.port + "/mas3d/ws", "scenario=" + options.scenario, options.token ? "token=on" : "token=off");
});

server.on("connection", (socket, request) => {
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

    later(760, () => socket.send(JSON.stringify({ type: "reply", turnId, text: replyFor(text), cue: cueFor(text) })));

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

server.on("error", (error) => {
  process.stderr.write("[mock-bridge] " + (error instanceof Error ? error.message : String(error)) + "\n");
  process.exit(1);
});

const shutdown = () => {
  log("shutting down");
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 300).unref();
};
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
