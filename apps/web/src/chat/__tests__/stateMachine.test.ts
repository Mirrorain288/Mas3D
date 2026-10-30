import { describe, expect, it } from "vitest";
import { emptyState, MAX_TURNS, normalizeInput, reduce, stripScenePrefix, type ChatState, type Turn } from "../store";

const at = 1_700_000_000_000;

function send(state: ChatState, text: string, id = "p1"): ChatState {
  return reduce(state, { type: "send", id, text, at });
}

describe("stripScenePrefix", () => {
  it("剥掉全角与半角两种教室前缀", () => {
    expect(stripScenePrefix("（3D教室）你好")).toBe("你好");
    expect(stripScenePrefix("（3D教室）  你好")).toBe("你好");
    expect(stripScenePrefix("(3D教室)你好")).toBe("你好");
  });

  it("没有前缀时原样返回", () => {
    expect(stripScenePrefix("你好")).toBe("你好");
    expect(stripScenePrefix("我说（3D教室）不算前缀")).toBe("我说（3D教室）不算前缀");
  });
});

describe("normalizeInput", () => {
  it("多行折成一行并截断到 300 字", () => {
    expect(normalizeInput("  a\nb  ")).toBe("a b");
    expect(normalizeInput("字".repeat(400))).toHaveLength(300);
  });

  it("只有空白时返回空串", () => {
    expect(normalizeInput("   \n ")).toBe("");
  });
});

describe("对话状态机", () => {
  it("send → ack → thinking → reply → idle 的完整回合", () => {
    let state = send(emptyState(), "你好");
    expect(state.phase).toBe("sending");
    expect(state.turns).toHaveLength(1);
    expect(state.turns[0]).toMatchObject({ role: "player", text: "你好" });

    state = reduce(state, { type: "ack" });
    expect(state.phase).toBe("thinking");

    state = reduce(state, { type: "thinking" });
    expect(state.phase).toBe("thinking");

    state = reduce(state, { type: "reply", id: "m1", text: "（3D教室）你好呀", at });
    expect(state.phase).toBe("idle");
    expect(state.turns).toHaveLength(2);
    expect(state.turns[1]).toMatchObject({ role: "monika", text: "你好呀" });
  });

  it("等待回复期间拒绝第二次发送", () => {
    const state = send(send(emptyState(), "第一句"), "第二句", "p2");
    expect(state.turns).toHaveLength(1);
    expect(state.turns[0]?.text).toBe("第一句");
  });

  it("空白输入不产生消息也不进入等待", () => {
    const state = send(emptyState(), "   ");
    expect(state.phase).toBe("idle");
    expect(state.turns).toHaveLength(0);
  });

  it("error 只改状态、不动消息，并可用 resend 重试", () => {
    const waiting = send(emptyState(), "在吗");
    const failed = reduce(waiting, { type: "error", code: "timeout", message: "没有及时收到回复" });
    expect(failed.phase).toBe("error");
    expect(failed.error).toEqual({ code: "timeout", message: "没有及时收到回复" });
    expect(failed.turns).toHaveLength(1);

    const retried = reduce(failed, { type: "resend" });
    expect(retried.phase).toBe("sending");
    expect(retried.error).toBeNull();
    expect(retried.turns).toHaveLength(1);
  });

  it("动作后端的独立回复不覆盖主回复、也不改状态机", () => {
    let state = reduce(send(emptyState(), "举手"), { type: "reply", id: "m1", text: "好呀", at });
    state = reduce(state, { type: "backend", id: "b1", text: "我抬起了右手。", at });
    expect(state.phase).toBe("idle");
    expect(state.turns.map((t) => t.role)).toEqual(["player", "monika", "backend"]);
    expect(state.turns[2]?.text).toBe("我抬起了右手。");
  });

  it("兜底台词追加为 Monika 消息但保留错误状态", () => {
    const failed = reduce(send(emptyState(), "在吗"), { type: "error", code: "bridge_down", message: "连接断开了" });
    const withFallback = reduce(failed, { type: "fallback", id: "f1", text: "（她侧过头）……", at });
    expect(withFallback.phase).toBe("error");
    expect(withFallback.turns.at(-1)).toMatchObject({ role: "monika", text: "（她侧过头）……" });
  });

  it("只保留最近 50 回合", () => {
    const turns: Turn[] = Array.from({ length: 60 }, (_, i) => ({ id: `t${i}`, role: "player", text: `第 ${i} 条`, at }));
    const restored = reduce(emptyState(), { type: "restore", turns });
    expect(restored.turns).toHaveLength(MAX_TURNS);
    expect(restored.turns[0]?.text).toBe("第 10 条");
  });

  it("reset 清空消息与错误", () => {
    const dirty = reduce(send(emptyState(), "你好"), { type: "error", code: "model_error", message: "模型不可用" });
    const cleared = reduce(dirty, { type: "reset" });
    expect(cleared.phase).toBe("idle");
    expect(cleared.turns).toHaveLength(0);
    expect(cleared.error).toBeNull();
  });
});
