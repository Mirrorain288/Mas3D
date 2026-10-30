import { describe, expect, it } from "vitest";
import type { MotionIntent, MotionPlan } from "@mas3d/protocol";
import {
  HastMotion,
  MIN_DURATION_MS,
  ROTATION_LIMIT,
  armTargets,
  parseIntentKey,
  planSegments,
  sampleArmPose,
  type ArmAction,
} from "../hastMotion";

function intent(key: string, overrides: Partial<MotionIntent> = {}): MotionIntent {
  return { key, speed: 1, strength: 1, hold: 1, delay: 0, ...overrides };
}

function plan(overrides: Partial<MotionPlan> = {}): MotionPlan {
  return {
    base: { change: false, name: "坐姿待机", motion: 0, rest: true },
    operation: "command",
    timing: "同时",
    intents: [intent("right-挥手打招呼")],
    ...overrides,
  };
}

const ACTIONS: ArmAction[] = ["raise", "lower", "forward", "backward", "left", "right", "wave", "hold"];

describe("parseIntentKey", () => {
  it("识别八种动作", () => {
    const cases: Array<[string, ArmAction]> = [
      ["右臂上抬", "raise"],
      ["右臂下放", "lower"],
      ["右臂前伸", "forward"],
      ["右臂后移", "backward"],
      ["右臂左移", "left"],
      ["右臂右移", "right"],
      ["right-挥手打招呼", "wave"],
      ["left-arm-hold", "hold"],
    ];
    for (const [key, action] of cases) {
      expect(parseIntentKey(key).action, key).toBe(action);
    }
  });

  it("识别侧别与高度", () => {
    expect(parseIntentKey("left-arm-raise").side).toBe("left");
    expect(parseIntentKey("右臂上抬").side).toBe("right");
    expect(parseIntentKey("双臂上抬").side).toBe("both");
    expect(parseIntentKey("右臂上抬高于头顶").height).toBeCloseTo(0.45);
    expect(parseIntentKey("右臂上抬齐平头部").height).toBeCloseTo(0.22);
    expect(parseIntentKey("右臂上抬").height).toBe(0);
  });

  it("认不出的 key 归为 idle，不改姿态", () => {
    const parsed = parseIntentKey("unknown-xyz");
    expect(parsed.action).toBe("idle");
    expect(armTargets(parsed.action, 1).upperArm).toEqual({ x: 0, y: 0, z: 0 });
  });
});

describe("armTargets", () => {
  it("八种动作各自产生不同且有界的旋转", () => {
    const seen = new Set<string>();
    for (const action of ACTIONS) {
      const targets = armTargets(action, 1);
      for (const part of [targets.upperArm, targets.forearm]) {
        for (const axis of [part.x, part.y, part.z]) {
          expect(Math.abs(axis)).toBeLessThanOrEqual(ROTATION_LIMIT);
        }
      }
      const signature = JSON.stringify(targets);
      expect(seen.has(signature), action).toBe(false);
      seen.add(signature);
    }
  });

  it("上抬让左臂 z 变负，抬得越高越负", () => {
    const low = armTargets("raise", 0.5).upperArm.z;
    const high = armTargets("raise", 1).upperArm.z;
    expect(low).toBeLessThan(0);
    expect(high).toBeLessThan(low);
    expect(armTargets("raise", 1, 0.45).upperArm.z).toBeLessThan(high);
  });

  it("强度为 0 时回到零位", () => {
    for (const action of ACTIONS) {
      const targets = armTargets(action, 0);
      expect(targets.upperArm.z).toBeCloseTo(0);
    }
  });

  it("超界输入被夹到安全范围", () => {
    const targets = armTargets("raise", 9, 9);
    expect(targets.upperArm.z).toBe(-ROTATION_LIMIT);
  });
});

describe("planSegments", () => {
  it("非 command 计划不产生片段", () => {
    expect(planSegments(plan({ operation: "chat", intents: [] }))).toEqual([]);
    expect(planSegments(plan({ intents: [] }))).toEqual([]);
  });

  it("同时：各段同时从自己的 delay 起跑，速度缩放时长", () => {
    const segments = planSegments(
      plan({
        timing: "同时",
        intents: [intent("右臂上抬", { hold: 2, speed: 1 }), intent("左臂前伸", { hold: 1, speed: 2, delay: 0.5 })],
      }),
    );
    expect(segments).toHaveLength(2);
    expect(segments[0]?.startMs).toBe(0);
    expect(segments[0]?.durationMs).toBe(2000);
    expect(segments[1]?.startMs).toBe(500);
    expect(segments[1]?.durationMs).toBe(500);
  });

  it("依次：后一段在前一段结束后才开始，delay 照常生效", () => {
    const segments = planSegments(
      plan({
        timing: "依次",
        intents: [intent("右臂上抬", { hold: 1 }), intent("左臂前伸", { hold: 1, delay: 0.5 })],
      }),
    );
    expect(segments[0]?.startMs).toBe(0);
    expect(segments[1]?.startMs).toBe(1000 + 500);
  });

  it("时长不低于最短值", () => {
    const segments = planSegments(plan({ intents: [intent("右臂上抬", { hold: 0.01, speed: 2 })] }));
    expect(segments[0]?.durationMs).toBe(MIN_DURATION_MS);
  });
});

describe("sampleArmPose", () => {
  const segments = planSegments(
    plan({ intents: [intent("右臂上抬", { hold: 2, strength: 1, delay: 0 })] }),
  );

  it("起手与收势回到零位，中段最大", () => {
    const start = sampleArmPose(segments, 0);
    expect(start?.right.upperArm.z).toBeCloseTo(0, 5);
    const mid = sampleArmPose(segments, 1000);
    expect(mid?.right.upperArm.z).toBeCloseTo(0.9, 5);
    const end = sampleArmPose(segments, 2000);
    expect(end?.right.upperArm.z).toBeCloseTo(0, 5);
  });

  it("未开始与已结束返回 null", () => {
    expect(sampleArmPose(segments, -1)).toBeNull();
    expect(sampleArmPose(segments, 2001)).toBeNull();
  });

  it("右臂是左臂的镜像（z 反号）", () => {
    const both = sampleArmPose(
      planSegments(plan({ intents: [intent("双臂上抬", { hold: 2 })] })),
      1000,
    );
    expect(both?.left.upperArm.z).toBeLessThan(0);
    expect(both?.right.upperArm.z).toBeCloseTo(-(both?.left.upperArm.z ?? 0), 6);
  });

  it("挥手时前臂有摆动且方向随时间变化", () => {
    const waveSegments = planSegments(plan({ intents: [intent("right-挥手打招呼", { hold: 2 })] }));
    const samples = [200, 400, 600, 800].map((t) => sampleArmPose(waveSegments, t)?.right.forearm.z ?? 0);
    expect(Math.max(...samples)).toBeGreaterThan(0);
    expect(Math.min(...samples)).toBeLessThan(0);
  });

  it("只有单侧计划时另一侧保持零位", () => {
    const one = sampleArmPose(segments, 1000);
    expect(one?.left).toEqual({ upperArm: { x: 0, y: 0, z: 0 }, forearm: { x: 0, y: 0, z: 0 } });
  });
});

describe("HastMotion", () => {
  it("chat 计划不启动", () => {
    const motion = new HastMotion();
    expect(motion.start(plan({ operation: "chat", intents: [] }), 0)).toBe(false);
    expect(motion.active).toBe(false);
    expect(motion.sample(0)).toBeNull();
  });

  it("command 计划按时间轴采样并在结束后自动清空", () => {
    const motion = new HastMotion();
    expect(motion.start(plan({ intents: [intent("右臂上抬", { hold: 1 })] }), 1000)).toBe(true);
    expect(motion.active).toBe(true);
    expect(motion.sample(1500)?.right.upperArm.z).toBeGreaterThan(0);
    expect(motion.sample(2001)).toBeNull();
    expect(motion.active).toBe(false);
  });

  it("stop 立刻回到待机", () => {
    const motion = new HastMotion();
    motion.start(plan(), 0);
    motion.stop();
    expect(motion.active).toBe(false);
    expect(motion.sample(500)).toBeNull();
  });
});