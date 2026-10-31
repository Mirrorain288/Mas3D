import { describe, expect, it } from "vitest";
import { SESSION_LABEL, fetchSession, normalizeSession, sessionHint, switchSession } from "../session";

describe("normalizeSession", () => {
  it("接受合法快照", () => {
    expect(normalizeSession({ mode: "shared", sharedTurns: 12, limit: 60, overLimit: false, umo: "a:b:c" })).toEqual({
      mode: "shared",
      sharedTurns: 12,
      limit: 60,
      overLimit: false,
      umo: "a:b:c",
    });
  });

  it("拒绝未知模式与非对象", () => {
    expect(normalizeSession(null)).toBeNull();
    expect(normalizeSession({ mode: "cloud" })).toBeNull();
    expect(normalizeSession("shared")).toBeNull();
  });

  it("字段缺失时给出安全默认，并按 reached 推断 overLimit", () => {
    expect(normalizeSession({ mode: "shared", sharedTurns: 60, limit: 60 })).toMatchObject({ overLimit: true });
    expect(normalizeSession({ mode: "shared" })).toMatchObject({ sharedTurns: 0, limit: 0, overLimit: false });
  });
});

describe("sessionHint", () => {
  it("共用且超线才提示", () => {
    expect(sessionHint({ mode: "shared", sharedTurns: 61, limit: 60, overLimit: true })).toContain("61");
    expect(sessionHint({ mode: "shared", sharedTurns: 3, limit: 60, overLimit: false })).toBeNull();
    expect(sessionHint({ mode: "dedicated", sharedTurns: 99, limit: 60, overLimit: true })).toBeNull();
    expect(sessionHint(null)).toBeNull();
  });
});

describe("会话端点", () => {
  const ok = (session: unknown) => ({
    ok: true,
    json: async () => ({ ok: true, session }),
  }) as unknown as Response;

  it("GET 带令牌并解析快照", async () => {
    const calls: Array<{ url: string; init: RequestInit }> = [];
    const fetchImpl = (async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return ok({ mode: "shared", sharedTurns: 2, limit: 60, overLimit: false });
    }) as unknown as typeof fetch;

    const info = await fetchSession("http://127.0.0.1:8790", "tok", fetchImpl);

    expect(info?.sharedTurns).toBe(2);
    expect(calls[0]?.url).toBe("http://127.0.0.1:8790/mas3d/api/session");
    expect((calls[0]?.init.headers as Record<string, string>).Authorization).toBe("Bearer tok");
    expect(calls[0]?.init.method).toBe("GET");
  });

  it("POST 切模式，没有令牌时不带 Authorization", async () => {
    let body = "";
    const fetchImpl = (async (_url: string, init: RequestInit) => {
      body = String(init.body);
      return ok({ mode: "dedicated", sharedTurns: 2, limit: 60, overLimit: false });
    }) as unknown as typeof fetch;

    const info = await switchSession("http://127.0.0.1:8790", null, "dedicated", fetchImpl);

    expect(info?.mode).toBe("dedicated");
    expect(JSON.parse(body)).toEqual({ mode: "dedicated" });
  });

  it("桥接不在线时返回 null 而不是抛错", async () => {
    const fetchImpl = (async () => {
      throw new TypeError("fetch failed");
    }) as unknown as typeof fetch;
    await expect(fetchSession("http://127.0.0.1:9", null, fetchImpl)).resolves.toBeNull();
  });

  it("标签文案齐全", () => {
    expect(SESSION_LABEL.shared).toBe("与 QQ 共用");
    expect(SESSION_LABEL.dedicated).toBe("游戏专用");
  });
});
