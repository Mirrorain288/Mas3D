import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import { deriveCue } from "@mas3d/motion";
import { parseServerFrame, type ExpressionCue, type MotionPlan } from "@mas3d/protocol";
import { clearTurns, emptyState, loadTurns, normalizeInput, reduce, saveTurns, type ChatError, type Phase, type Turn } from "./store";

/** 一次回合的等待上限：超时后结束等待、给兜底台词并允许重试（D9）。 */
export const REPLY_TIMEOUT_MS = 8000;
/** 连接断了多久才显示横幅：短暂抖动不打扰玩家。 */
export const OFFLINE_BANNER_MS = 3000;

/** 本地兜底台词：桥接不可达时由她「说」一句，保证对话不中断。 */
export const FALLBACK_LINES = [
  "（她侧过头，像是在等信号回来）……我还在教室里，刚才那句好像没送出去。",
  "（教室的灯轻轻闪了一下）连接断了，但我没走开。要不要再试一次？",
  "（她把手放回桌面，安静了一会儿）听不到外面的声音了……你再发一次好吗？",
];

export interface BridgeHandlers {
  onCue?(cue: ExpressionCue): void;
  onPlan?(plan: MotionPlan): void;
  onPersist?(ok: boolean, reason?: "quota" | "unavailable"): void;
}

export interface BridgeApi {
  phase: Phase;
  turns: Turn[];
  error: ChatError | null;
  connected: boolean;
  offline: boolean;
  send(text: string): void;
  retry(): void;
  reset(): void;
}

let counter = 0;
function nextId(prefix: string): string {
  counter += 1;
  return prefix + "-" + Date.now().toString(36) + "-" + counter;
}

const BUSY: readonly Phase[] = ["sending", "thinking", "streaming"];
const OPEN = 1;

/**
 * 与桥接服务的 WebSocket 会话：负责连接、帧解析、8 秒超时、断线横幅与本地兜底。
 * 状态机本身是纯函数（store.ts），这里只管副作用。
 */
export function useBridge(url: string, token: string | null | undefined, handlers: BridgeHandlers = {}): BridgeApi {
  const [state, dispatch] = useReducer(reduce, undefined, () => emptyState(loadTurns()));
  const [connected, setConnected] = useState(false);
  const [offline, setOffline] = useState(false);

  const wsRef = useRef<WebSocket | null>(null);
  const stateRef = useRef(state);
  const handlersRef = useRef(handlers);
  const replyTimer = useRef<number | null>(null);
  const offlineTimer = useRef<number | null>(null);
  const fallbackIndex = useRef(0);
  const closedByUs = useRef(false);
  const notifiedQuota = useRef(false);

  stateRef.current = state;
  handlersRef.current = handlers;

  const clearReplyTimer = useCallback((): void => {
    if (replyTimer.current !== null) {
      window.clearTimeout(replyTimer.current);
      replyTimer.current = null;
    }
  }, []);

  const pushFallback = useCallback((): void => {
    const line = FALLBACK_LINES[fallbackIndex.current % FALLBACK_LINES.length] ?? FALLBACK_LINES[0] ?? "";
    fallbackIndex.current += 1;
    dispatch({ type: "fallback", id: nextId("f"), text: line, at: Date.now() });
  }, []);

  const fail = useCallback(
    (code: string, message: string): void => {
      clearReplyTimer();
      dispatch({ type: "error", code, message });
      pushFallback();
    },
    [clearReplyTimer, pushFallback],
  );

  const armReplyTimer = useCallback((): void => {
    clearReplyTimer();
    replyTimer.current = window.setTimeout(() => {
      replyTimer.current = null;
      fail("timeout", "没有及时收到回复（超过 8 秒），可以先继续，或点重试再发一次。");
    }, REPLY_TIMEOUT_MS);
  }, [clearReplyTimer, fail]);

  const connect = useCallback((): void => {
    closedByUs.current = true;
    wsRef.current?.close();
    closedByUs.current = false;

    const target = token ? url + (url.includes("?") ? "&" : "?") + "token=" + encodeURIComponent(token) : url;
    let socket: WebSocket;
    try {
      socket = new WebSocket(target);
    } catch {
      setConnected(false);
      setOffline(true);
      return;
    }
    wsRef.current = socket;

    socket.onopen = (): void => {
      if (wsRef.current !== socket) return;
      setConnected(true);
      setOffline(false);
      if (offlineTimer.current !== null) {
        window.clearTimeout(offlineTimer.current);
        offlineTimer.current = null;
      }
    };

    socket.onclose = (): void => {
      if (wsRef.current !== socket) return;
      setConnected(false);
      if (closedByUs.current) return;
      if (offlineTimer.current === null) {
        offlineTimer.current = window.setTimeout(() => {
          offlineTimer.current = null;
          setOffline(true);
        }, OFFLINE_BANNER_MS);
      }
    };

    socket.onerror = (): void => {
      // 具体原因由 onclose 统一处理，避免重复提示
    };

    socket.onmessage = (event: MessageEvent): void => {
      let frame;
      try {
        frame = parseServerFrame(JSON.parse(String(event.data)));
      } catch {
        return; // 不认识的帧直接忽略，界面不受影响
      }
      switch (frame.type) {
        case "ack":
          dispatch({ type: "ack" });
          break;
        case "thinking":
          dispatch({ type: "thinking" });
          break;
        case "reply":
          clearReplyTimer();
          dispatch({ type: "reply", id: frame.turnId, text: frame.text, at: Date.now() });
          // 桥接侧可以不带 cue：缺省时用前端同一份词表推导，保持单一推导源
          handlersRef.current.onCue?.(frame.cue ?? deriveCue(frame.text));
          break;
        case "intent": {
          const plan = frame.plan;
          handlersRef.current.onPlan?.(plan);
          if (plan.reply) {
            const last = [...stateRef.current.turns].reverse().find((turn) => turn.role === "monika");
            if (!last || last.text !== plan.reply.trim()) {
              dispatch({ type: "backend", id: frame.turnId + "-plan", text: plan.reply, at: Date.now() });
            }
          }
          break;
        }
        case "error":
          fail(frame.code, frame.message);
          break;
      }
    };
  }, [clearReplyTimer, fail, token, url]);

  useEffect(() => {
    connect();
    return () => {
      closedByUs.current = true;
      if (offlineTimer.current !== null) window.clearTimeout(offlineTimer.current);
      clearReplyTimer();
      wsRef.current?.close();
      wsRef.current = null;
    };
  }, [clearReplyTimer, connect]);

  // 会话记录落盘：失败只降级为「本次不保存」，不影响继续对话
  useEffect(() => {
    const result = saveTurns(state.turns);
    if (!result.ok) {
      handlersRef.current.onPersist?.(false, result.reason);
      notifiedQuota.current = true;
    } else if (notifiedQuota.current) {
      handlersRef.current.onPersist?.(true);
      notifiedQuota.current = false;
    }
  }, [state.turns]);

  const send = useCallback(
    (text: string): void => {
      if (BUSY.includes(stateRef.current.phase)) return;
      const normalized = normalizeInput(text);
      if (!normalized) return;
      dispatch({ type: "send", id: nextId("p"), text: normalized, at: Date.now() });
      const socket = wsRef.current;
      if (!socket || socket.readyState !== OPEN) {
        fail("bridge_down", "还没有连上桥接服务，下面是本地兜底的一句话。");
        return;
      }
      socket.send(JSON.stringify({ type: "say", text: normalized }));
      armReplyTimer();
    },
    [armReplyTimer, fail],
  );

  const retry = useCallback((): void => {
    const lastPlayer = [...stateRef.current.turns].reverse().find((turn) => turn.role === "player");
    if (!lastPlayer) return;
    dispatch({ type: "resend" });
    const socket = wsRef.current;
    if (socket && socket.readyState === OPEN) {
      socket.send(JSON.stringify({ type: "say", text: lastPlayer.text }));
      armReplyTimer();
      return;
    }
    connect();
    fail("bridge_down", "桥接服务还是没连上，先用本地兜底回你一句。");
  }, [armReplyTimer, connect, fail]);

  const reset = useCallback((): void => {
    dispatch({ type: "reset" });
    clearTurns();
  }, []);

  return { phase: state.phase, turns: state.turns, error: state.error, connected, offline, send, retry, reset };
}
