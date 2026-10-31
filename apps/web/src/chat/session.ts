/** 会话来源：与 QQ 共用同一条历史，还是游戏独占一条。
 *
 * 共用是默认也是这个项目的重点（design.md D1）：游戏里的每句话都真的写进 QQ
 * 那份上下文，于是记忆、人设、工具全部一致。代价是那条会话会越来越长——实测
 * 358 回合已 1.4 MB——所以桥接侧记了个软上限，这里只负责把它变成人能读的提示，
 * 以及一键切到专用会话。
 */

export type SessionMode = "shared" | "dedicated";

export interface SessionInfo {
  mode: SessionMode;
  sharedTurns: number;
  limit: number;
  overLimit: boolean;
  umo?: string;
}

export const SESSION_LABEL: Record<SessionMode, string> = {
  shared: "与 QQ 共用",
  dedicated: "游戏专用",
};

export const SESSION_NOTE: Record<SessionMode, string> = {
  shared: "她说的话会和 QQ 上的记录连在一起，两边记得同一段往事。",
  dedicated: "从这条会话开始重新认识她，QQ 那边不受影响。",
};

function asMode(value: unknown): SessionMode | null {
  return value === "shared" || value === "dedicated" ? value : null;
}

/** 容错解析：字段缺失或类型不对就当没拿到，界面回到「来源未知」。 */
export function normalizeSession(raw: unknown): SessionInfo | null {
  if (typeof raw !== "object" || raw === null) return null;
  const value = raw as Record<string, unknown>;
  const mode = asMode(value.mode);
  if (!mode) return null;
  const sharedTurns = typeof value.sharedTurns === "number" && value.sharedTurns >= 0 ? Math.floor(value.sharedTurns) : 0;
  const limit = typeof value.limit === "number" && value.limit >= 0 ? Math.floor(value.limit) : 0;
  const info: SessionInfo = {
    mode,
    sharedTurns,
    limit,
    overLimit: value.overLimit === true || (limit > 0 && sharedTurns >= limit),
  };
  if (typeof value.umo === "string" && value.umo) info.umo = value.umo;
  return info;
}

/** 只有「共用且超过软上限」才需要打扰玩家；其余情况返回 null。 */
export function sessionHint(info: SessionInfo | null): string | null {
  if (!info || info.mode !== "shared" || !info.overLimit) return null;
  const limit = info.limit > 0 ? info.limit : info.sharedTurns;
  return `这段对话已经往 QQ 那条记录写了 ${info.sharedTurns} 句（提醒线 ${limit} 句）。她还会记得，只是每轮要带上的上下文会越来越长。可以换一条专用会话。`;
}

async function request(
  baseUrl: string,
  token: string | null,
  init: { method: "GET" | "POST"; body?: string },
  fetchImpl: typeof fetch,
): Promise<SessionInfo | null> {
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (init.body !== undefined) headers["Content-Type"] = "application/json";
  // exactOptionalPropertyTypes 下不能给 body 显式传 undefined
  const requestInit: RequestInit = { method: init.method, headers };
  if (init.body !== undefined) requestInit.body = init.body;
  try {
    const response = await fetchImpl(baseUrl + "/mas3d/api/session", requestInit);
    if (!response.ok) return null;
    const payload = (await response.json()) as { session?: unknown };
    return normalizeSession(payload.session);
  } catch {
    // 桥接没起来时不必吵闹：会话来源只是提示信息
    return null;
  }
}

export function fetchSession(
  baseUrl: string,
  token: string | null,
  fetchImpl: typeof fetch = fetch,
): Promise<SessionInfo | null> {
  return request(baseUrl, token, { method: "GET" }, fetchImpl);
}

export function switchSession(
  baseUrl: string,
  token: string | null,
  mode: SessionMode,
  fetchImpl: typeof fetch = fetch,
): Promise<SessionInfo | null> {
  return request(baseUrl, token, { method: "POST", body: JSON.stringify({ mode }) }, fetchImpl);
}
