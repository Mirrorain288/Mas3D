import { MAX_INPUT_CHARS } from "@mas3d/protocol";

export type Phase = "idle" | "sending" | "thinking" | "streaming" | "error";
export type TurnRole = "player" | "monika" | "backend" | "system";

export interface Turn {
  id: string;
  role: TurnRole;
  text: string;
  at: number;
}

export interface ChatError {
  code: string;
  message: string;
}

export interface ChatState {
  phase: Phase;
  turns: Turn[];
  error: ChatError | null;
}

/** UI 只渲染最近 50 回合，本地存储也只留 50 条（D13）。 */
export const MAX_TURNS = 50;
/** 游戏回合在共享会话里带的前缀，展示时要剥掉（D5）。 */
export const SCENE_PREFIX = "（3D教室）";
export const TRANSCRIPT_KEY = "mas3d.transcript";

/** 去掉注入的教室前缀，两种括号都认（模型有时会把全角转半角）。 */
export function stripScenePrefix(text: string): string {
  const head = text.trimStart();
  for (const prefix of [SCENE_PREFIX, "(3D教室)"]) {
    if (head.startsWith(prefix)) return head.slice(prefix.length).trimStart();
  }
  return text;
}

/** 单行化 + 截断到协议上限，空白输入返回空串。 */
export function normalizeInput(text: string): string {
  return text.replace(/[\r\n]+/g, " ").trim().slice(0, MAX_INPUT_CHARS);
}

export function emptyState(turns: Turn[] = []): ChatState {
  return { phase: "idle", turns: turns.slice(-MAX_TURNS), error: null };
}

export type ChatEvent =
  | { type: "send"; id: string; text: string; at: number }
  | { type: "resend" }
  | { type: "ack" }
  | { type: "thinking" }
  | { type: "reply"; id: string; text: string; at: number }
  | { type: "backend"; id: string; text: string; at: number }
  | { type: "fallback"; id: string; text: string; at: number }
  | { type: "error"; code: string; message: string }
  | { type: "reset" }
  | { type: "restore"; turns: Turn[] };

function append(turns: Turn[], turn: Turn): Turn[] {
  return [...turns, turn].slice(-MAX_TURNS);
}

const BUSY: readonly Phase[] = ["sending", "thinking", "streaming"];

/**
 * 对话状态机（纯函数）：idle → sending → thinking → idle，任一步可落到 error。
 * 等待期间拒绝新的发送，避免同一个回合被重复提交。
 */
export function reduce(state: ChatState, ev: ChatEvent): ChatState {
  switch (ev.type) {
    case "send": {
      if (BUSY.includes(state.phase)) return state;
      const text = normalizeInput(ev.text);
      if (!text) return state;
      return {
        phase: "sending",
        error: null,
        turns: append(state.turns, { id: ev.id, role: "player", text, at: ev.at }),
      };
    }
    case "resend":
      return state.phase === "error" ? { ...state, phase: "sending", error: null } : state;
    case "ack":
    case "thinking":
      return BUSY.includes(state.phase) ? { ...state, phase: "thinking" } : state;
    case "reply":
      return {
        phase: "idle",
        error: null,
        turns: append(state.turns, { id: ev.id, role: "monika", text: stripScenePrefix(ev.text), at: ev.at }),
      };
    case "backend":
      return { ...state, turns: append(state.turns, { id: ev.id, role: "backend", text: stripScenePrefix(ev.text), at: ev.at }) };
    case "fallback":
      // 本地兜底台词：对话已经结束了，错误状态保留在卡片上提示玩家
      return { ...state, turns: append(state.turns, { id: ev.id, role: "monika", text: ev.text, at: ev.at }) };
    case "error":
      return { ...state, phase: "error", error: { code: ev.code, message: ev.message } };
    case "reset":
      return emptyState();
    case "restore":
      return emptyState(ev.turns);
  }
}

const ROLES: readonly TurnRole[] = ["player", "monika", "backend", "system"];

function isTurn(value: unknown): value is Turn {
  if (!value || typeof value !== "object") return false;
  const v = value as Record<string, unknown>;
  return typeof v.id === "string" && typeof v.text === "string" && typeof v.at === "number" && ROLES.includes(v.role as TurnRole);
}

export function loadTurns(): Turn[] {
  try {
    const raw = globalThis.localStorage?.getItem(TRANSCRIPT_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter(isTurn).slice(-MAX_TURNS) : [];
  } catch {
    return [];
  }
}

export type PersistResult = { ok: true } | { ok: false; reason: "quota" | "unavailable" };

export function saveTurns(turns: Turn[]): PersistResult {
  try {
    globalThis.localStorage?.setItem(TRANSCRIPT_KEY, JSON.stringify(turns.slice(-MAX_TURNS)));
    return { ok: true };
  } catch (error) {
    const quota = error instanceof Error && (error.name === "QuotaExceededError" || error.name === "NS_ERROR_DOM_QUOTA_REACHED");
    return { ok: false, reason: quota ? "quota" : "unavailable" };
  }
}

export function clearTurns(): void {
  try {
    globalThis.localStorage?.removeItem(TRANSCRIPT_KEY);
  } catch {
    // 存储被禁用时本来就没有记录可清
  }
}
