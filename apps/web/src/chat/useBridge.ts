import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import { deriveCue } from "@mas3d/motion";
import { parseServerFrame, type ExpressionCue, type MotionPlan } from "@mas3d/protocol";
import { acceptsReply, clearTurns, emptyState, loadTurns, normalizeInput, reduce, saveTurns, type ChatError, type Phase, type Turn } from "./store";

/**
 * 一次回合的等待上限，必须长于桥接一侧的 TURN_TIMEOUT_S（120 s），
 * 否则玩家会先看到「超时 + 兜底台词」再看到她的真回复（验收实测一回合 27.7 s）。
 * 可以用 VITE_MAS3D_REPLY_TIMEOUT_MS 覆写（mock 场景调试时调小）。
 */
const ENV_TIMEOUT = Number(import.meta.env.VITE_MAS3D_REPLY_TIMEOUT_MS);
export const REPLY_TIMEOUT_MS =
  Number.isFinite(ENV_TIMEOUT) && ENV_TIMEOUT >= 1000 ? ENV_TIMEOUT : 150_000;
/** 连接断了多久才显示横幅：短暂抖动不打扰玩家。 */
export const OFFLINE_BANNER_MS = 3000;
/** 自动重连的退避区间：插件重载（TECH.md 明说的升级风险）后不需要玩家刷新页面。 */
export const RECONNECT_BASE_MS = 1000;
export const RECONNECT_MAX_MS = 8000;

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
  /** 手动重连（不刷新页面）；自动重连已经在后台退避尝试。 */
  reconnect(): void;
}

let counter = 0;
/** DEV 下把「动作计划被应用/被丢弃」两件事记在 window 上，供 e2e 断言。 */
function countIntent(applied: boolean): void {
  if (!import.meta.env.DEV) return;
  const scope = window as unknown as { __mas3dIntent?: { applied: number; dropped: number } };
  const counter = (scope.__mas3dIntent ??= { applied: 0, dropped: 0 });
  if (applied) counter.applied += 1;
  else counter.dropped += 1;
}

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
  /** 当前在等哪一回合（来自 ack/thinking 帧），回复必须对上号才算数。 */
  const pendingTurn = useRef<string | null>(null);
  // 最近一条已经认领的回合：迟到的 intent 帧要按它对号入座（见 D21/风险 3）
  const lastTurn = useRef<string | null>(null);
  const offlineTimer = useRef<number | null>(null);
  const fallbackIndex = useRef(0);
  const closedByUs = useRef(false);
  const notifiedQuota = useRef(false);
  const reconnectTimer = useRef<number | null>(null);
  const attempts = useRef(0);

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

  /**
   * 只有「请求根本没进管线」（bridge_down）才补一句本地台词；
   * 超时/模型报错时她其实可能已经回答过了，编一句「没送出去」会与随后到达的真回复自相矛盾。
   */
  const fail = useCallback(
    (code: string, message: string, withFallback = false): void => {
      clearReplyTimer();
      dispatch({ type: "error", code, message });
      if (withFallback) pushFallback();
    },
    [clearReplyTimer, pushFallback],
  );

  const armReplyTimer = useCallback((): void => {
    clearReplyTimer();
    replyTimer.current = window.setTimeout(() => {
      replyTimer.current = null;
      fail("timeout", "没有及时收到回复，桥接可能卡住了；可以点重试再问一次。");
    }, REPLY_TIMEOUT_MS);
  }, [clearReplyTimer, fail]);

  const clearReconnectTimer = useCallback((): void => {
    if (reconnectTimer.current !== null) {
      window.clearTimeout(reconnectTimer.current);
      reconnectTimer.current = null;
    }
  }, []);

  const connect = useCallback((): void => {
    clearReconnectTimer();
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
      attempts.current = 0;
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
      // 这一回合的回复随连接一起丢了：等满 150 s 超时没有意义，直接告诉她没收到
      if (pendingTurn.current !== null) {
        pendingTurn.current = null;
        fail("bridge_down", "连接断了，这次回复没有回来；可以点重试再问一次。");
      }
      if (offlineTimer.current === null) {
        offlineTimer.current = window.setTimeout(() => {
          offlineTimer.current = null;
          setOffline(true);
        }, OFFLINE_BANNER_MS);
      }
      if (reconnectTimer.current === null) {
        const delay = Math.min(RECONNECT_BASE_MS * 2 ** attempts.current, RECONNECT_MAX_MS);
        attempts.current += 1;
        reconnectTimer.current = window.setTimeout(() => {
          reconnectTimer.current = null;
          connect();
        }, delay);
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
          pendingTurn.current = frame.turnId;
          armReplyTimer();
          dispatch({ type: "ack" });
          break;
        case "thinking":
          pendingTurn.current = frame.turnId;
          armReplyTimer();
          dispatch({ type: "thinking" });
          break;
        case "reply":
          // 迟到的回复属于上一回合：长度上限 120 s 的服务端回合可能刚好在玩家再问一句之后才结束
          if (!acceptsReply(pendingTurn.current, frame.turnId)) break;
          pendingTurn.current = null;
          clearReplyTimer();
          // 回合 id 现在与玩家那一句的 id 相同（turnId 就是它），消息条目要分命名空间，
          // 否则两条 Turn 撞 React key，列表会吞掉或重复渲染
          lastTurn.current = frame.turnId;
          dispatch({ type: "reply", id: "m-" + frame.turnId, text: frame.text, at: Date.now() });
          // 桥接侧可以不带 cue：缺省时用前端同一份词表推导，保持单一推导源
          handlersRef.current.onCue?.(frame.cue ?? deriveCue(frame.text));
          break;
        case "intent": {
          // 动作计划是回复之后才问 HAST-SEC 的，可能晚于下一条消息到达：
          // 只认「刚回复过的那一回合」或「正在等的回合」，否则玩家刚说完新话，
          // 上一回合的手臂动作会突然抬起来（风险 3）
          // 已经有新问题在等回复时，上一回合的迟到计划一律丢（pending 非空即失效）
          const pending = pendingTurn.current;
          const accepted =
            frame.turnId !== "" &&
            (pending === null ? frame.turnId === lastTurn.current : frame.turnId === pending);
          countIntent(accepted);
          if (!accepted) break;
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
          pendingTurn.current = null;
          fail(frame.code, frame.message);
          break;
      }
    };
  }, [armReplyTimer, clearReconnectTimer, clearReplyTimer, fail, token, url]);

  useEffect(() => {
    connect();
    return () => {
      closedByUs.current = true;
      clearReconnectTimer();
      if (offlineTimer.current !== null) window.clearTimeout(offlineTimer.current);
      clearReplyTimer();
      wsRef.current?.close();
      wsRef.current = null;
    };
  }, [clearReconnectTimer, clearReplyTimer, connect]);

  /** 手动重连：重置退避计数，让「重新连接」按钮立刻生效而不是等下一轮退避。 */
  const reconnect = useCallback((): void => {
    attempts.current = 0;
    setOffline(false);
    connect();
  }, [connect]);

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
      const turnId = nextId("p");
      dispatch({ type: "send", id: turnId, text: normalized, at: Date.now() });
      const socket = wsRef.current;
      if (!socket || socket.readyState !== OPEN) {
        fail("bridge_down", "还没有连上桥接服务，下面是本地兜底的一句话。", true);
        return;
      }
      // 先乐观登记这一回合的 id：ack 迟到或丢掉时，回复仍然认得出属于谁。
      // 服务端会原样回显，所以新旧插件都能对齐（旧插件自造 id，ack 会把它覆盖回来）。
      pendingTurn.current = turnId;
      socket.send(JSON.stringify({ type: "say", text: normalized, turnId }));
      armReplyTimer();
    },
    [armReplyTimer, fail],
  );

  const retry = useCallback((): void => {
    // 连点「重试」或点击时上一回合还没结束：再发一次就是两个真回合（两次模型调用、
    // 两次写入 QQ 历史），而先到的那条回复会因为 pendingTurn 被覆盖而丢掉
    if (BUSY.includes(stateRef.current.phase)) return;
    const lastPlayer = [...stateRef.current.turns].reverse().find((turn) => turn.role === "player");
    if (!lastPlayer) return;
    dispatch({ type: "resend" });
    // 重试是新的一回合（会真的再跑一次模型），因此换一个新的 id
    const turnId = nextId("p");
    pendingTurn.current = turnId;
    const socket = wsRef.current;
    if (socket && socket.readyState === OPEN) {
      socket.send(JSON.stringify({ type: "say", text: lastPlayer.text, turnId }));
      armReplyTimer();
      return;
    }
    connect();
    fail("bridge_down", "桥接服务还是没连上，先用本地兜底回你一句。", true);
  }, [armReplyTimer, connect, fail]);

  const reset = useCallback((): void => {
    // 清空之后仍在等的那一回合要一起作废：否则它的回复、或者 150 秒后的超时提示，
    // 会落在空白对话里，看起来像她凭空插话
    clearReplyTimer();
    pendingTurn.current = null;
    lastTurn.current = null;
    dispatch({ type: "reset" });
    clearTurns();
  }, [clearReplyTimer]);

  return { phase: state.phase, turns: state.turns, error: state.error, connected, offline, send, retry, reset, reconnect };
}