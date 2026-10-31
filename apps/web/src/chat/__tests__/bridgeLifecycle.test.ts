// @vitest-environment jsdom
import { act, createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useBridge, type BridgeApi, type BridgeHandlers } from "../useBridge";

(globalThis as unknown as { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

/** 假 WebSocket：只记录 on* 回调与发出的帧，测试自己决定什么时候算「连上」、什么时候送帧。 */
class FakeSocket {
  static instances: FakeSocket[] = [];

  readonly url: string;
  readyState = 0;
  readonly sent: string[] = [];
  onopen: ((event: unknown) => void) | null = null;
  onclose: ((event: unknown) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    FakeSocket.instances.push(this);
  }

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.onclose?.({});
  }

  /** 握手完成。 */
  accept(): void {
    this.readyState = 1;
    this.onopen?.({});
  }

  /** 服务端消失（插件重载、进程被杀）。 */
  drop(): void {
    this.readyState = 3;
    this.onclose?.({});
  }

  /** 送一帧进客户端。 */
  deliver(frame: unknown): void {
    this.onmessage?.({ data: JSON.stringify(frame) });
  }

  frames(): unknown[] {
    return this.sent.map((raw) => JSON.parse(raw) as unknown);
  }
}

interface Harness {
  api(): BridgeApi;
  root: Root;
  host: HTMLDivElement;
}

let harness: Harness | null = null;

function renderBridge(handlers: BridgeHandlers = {}): Harness {
  let latest: BridgeApi | null = null;
  function Probe(): null {
    latest = useBridge("ws://test.local/mas3d/ws", "tok", handlers);
    return null;
  }
  const host = document.createElement("div");
  const root = createRoot(host);
  act(() => {
    root.render(createElement(Probe));
  });
  const built: Harness = {
    api() {
      if (!latest) throw new Error("hook 没有渲染");
      return latest;
    },
    root,
    host,
  };
  harness = built;
  return built;
}

function socket(): FakeSocket {
  const last = FakeSocket.instances[FakeSocket.instances.length - 1];
  if (!last) throw new Error("没有建立连接");
  return last;
}

beforeEach(() => {
  FakeSocket.instances = [];
  vi.stubGlobal("WebSocket", FakeSocket as unknown as typeof WebSocket);
});

afterEach(() => {
  if (harness) {
    act(() => {
      harness?.root.unmount();
    });
    harness = null;
  }
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("retry()", () => {
  it("does not start a second turn while the first one is still waiting", () => {
    const h = renderBridge();
    const sock = socket();
    act(() => sock.accept());
    act(() => h.api().send("第一句"));
    expect(sock.frames()).toHaveLength(1);

    act(() => h.api().retry());

    expect(sock.frames()).toHaveLength(1);
  });
});
describe("reset()", () => {
  it("ignores a reply that arrives after the transcript was cleared", () => {
    const h = renderBridge();
    const sock = socket();
    act(() => sock.accept());
    act(() => h.api().send("第一句"));
    const sent = sock.frames()[0] as { turnId?: string };

    act(() => h.api().reset());
    expect(h.api().turns).toHaveLength(0);

    act(() => sock.deliver({ type: "reply", turnId: sent.turnId, text: "迟到的回复" }));

    expect(h.api().turns).toHaveLength(0);
  });
});
describe("send()", () => {
  it("accepts the reply even when the ack frame never arrives", () => {
    const h = renderBridge();
    const sock = socket();
    act(() => sock.accept());
    act(() => h.api().send("第一句"));

    const frame = sock.frames()[0] as { turnId?: string };
    expect(frame.turnId).toBeTruthy();

    // 服务端只回了 reply，没有 ack：客户端必须靠自己在 say 帧里带的 turnId 认出它
    act(() => sock.deliver({ type: "reply", turnId: frame.turnId, text: "在的。" }));

    expect(h.api().phase).toBe("idle");
    expect(h.api().turns.at(-1)?.text).toBe("在的。");
  });
});
const COMMAND_PLAN = {
  base: { change: false, name: "保持", motion: 0, rest: true },
  operation: "command",
  timing: "同时",
  intents: [{ key: "right-挥手打招呼", speed: 1, strength: 1, hold: 2, delay: 0 }],
  reply: "",
} as const;

describe("intent frames", () => {
  it("applies the plan of the turn that just answered", () => {
    const keys: string[] = [];
    const h = renderBridge({ onPlan: (plan) => keys.push(...plan.intents.map((i) => i.key)) });
    const sock = socket();
    act(() => sock.accept());
    act(() => h.api().send("打个招呼"));
    const frame = sock.frames()[0] as { turnId?: string };
    act(() => sock.deliver({ type: "reply", turnId: frame.turnId, text: "好呀。" }));
    act(() => sock.deliver({ type: "intent", turnId: frame.turnId, plan: COMMAND_PLAN }));

    expect(keys).toEqual(["right-挥手打招呼"]);
  });

  it("drops the plan that arrives after the player has already asked again", () => {
    const keys: string[] = [];
    const h = renderBridge({ onPlan: (plan) => keys.push(...plan.intents.map((i) => i.key)) });
    const sock = socket();
    act(() => sock.accept());
    act(() => h.api().send("打个招呼"));
    const first = sock.frames()[0] as { turnId?: string };
    act(() => sock.deliver({ type: "reply", turnId: first.turnId, text: "好呀。" }));
    expect(keys).toEqual([]);

    // 玩家立刻问了下一句：上一回合迟到的计划不该让手臂突然动起来
    act(() => h.api().send("第二个问题"));
    act(() => sock.deliver({ type: "intent", turnId: first.turnId, plan: COMMAND_PLAN }));

    expect(keys).toEqual([]);
  });
});
describe("reconnect", () => {
  it("comes back on its own after the socket drops", () => {
    vi.useFakeTimers();
    const h = renderBridge();
    const first = socket();
    act(() => first.accept());
    expect(h.api().connected).toBe(true);

    act(() => first.drop());
    expect(h.api().connected).toBe(false);
    expect(FakeSocket.instances).toHaveLength(1);

    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(FakeSocket.instances).toHaveLength(2);

    const second = socket();
    act(() => second.accept());
    expect(h.api().connected).toBe(true);
  });
});
describe("retry() after an error", () => {
  it("re-sends the last question under a fresh turn id", () => {
    const h = renderBridge();
    const sock = socket();
    act(() => sock.accept());
    act(() => h.api().send("第一句"));
    const first = sock.frames()[0] as { turnId?: string };
    act(() => sock.deliver({ type: "error", code: "model_error", message: "模型挂了" }));
    expect(h.api().phase).toBe("error");

    act(() => h.api().retry());

    expect(sock.frames()).toHaveLength(2);
    const again = sock.frames()[1] as { type: string; text: string; turnId: string };
    expect(again.type).toBe("say");
    expect(again.text).toBe("第一句");
    expect(again.turnId).not.toBe(first.turnId);
  });
});
