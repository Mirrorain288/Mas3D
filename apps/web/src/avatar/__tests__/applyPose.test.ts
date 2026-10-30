import { describe, expect, it } from "vitest";
import { ZERO_DELTA, type PoseDelta } from "@mas3d/motion";
import type { MotionIntent } from "@mas3d/protocol";
import { applyPose, armHoldEndMs, type PoseBones } from "../applyPose";

function bones(): PoseBones {
  return {
    head: { rotation: { x: 0, y: 0, z: 0 } },
    neck: { rotation: { x: 0, y: 0, z: 0 } },
    spine: { rotation: { x: 0, y: 0, z: 0 } },
    upperArmL: { rotation: { x: 0, y: 0, z: 0 } },
    upperArmR: { rotation: { x: 0, y: 0, z: 0 } },
  };
}

const delta: PoseDelta = { headPitch: 0.2, headYaw: -0.1, headRoll: 0.3, spineBend: 0.4, armRaiseL: 0.5, armRaiseR: 0.6 };

const intent = (delay: number, hold: number): MotionIntent => ({ key: "右臂上抬", speed: 1, strength: 0.5, hold, delay });

describe("applyPose", () => {
  it("weight=0 时不改变骨骼", () => {
    const b = bones();
    applyPose(b, delta, 0);
    expect(b.head.rotation).toEqual({ x: 0, y: 0, z: 0 });
    expect(b.upperArmR.rotation.z).toBe(0);
  });

  it("weight=1 时按分量叠加，颈与脊柱按比例分担", () => {
    const b = bones();
    applyPose(b, delta, 1);
    expect(b.head.rotation.x).toBeCloseTo(delta.headPitch, 6);
    expect(b.head.rotation.y).toBeCloseTo(delta.headYaw, 6);
    expect(b.head.rotation.z).toBeCloseTo(delta.headRoll, 6);
    expect(b.neck.rotation.x).toBeCloseTo(delta.headPitch * 0.35, 6);
    expect(b.spine.rotation.x).toBeCloseTo(delta.spineBend * 0.5, 6);
    expect(b.upperArmL.rotation.z).toBeCloseTo(-delta.armRaiseL, 6);
    expect(b.upperArmR.rotation.z).toBeCloseTo(delta.armRaiseR, 6);
  });

  it("weight 按比例缩放，超过 1 被夹住", () => {
    const half = bones();
    applyPose(half, delta, 0.5);
    expect(half.head.rotation.x).toBeCloseTo(delta.headPitch * 0.5, 6);
    const over = bones();
    applyPose(over, delta, 3);
    expect(over.head.rotation.x).toBeCloseTo(delta.headPitch, 6);
  });

  it("静止手势不产生任何偏移", () => {
    const b = bones();
    applyPose(b, ZERO_DELTA, 1);
    expect(b.head.rotation).toEqual({ x: 0, y: 0, z: 0 });
  });
});

describe("armHoldEndMs", () => {
  it("同时执行取最长的一段", () => {
    expect(armHoldEndMs([intent(0, 1), intent(0.5, 2)], "同时")).toBeCloseTo(2700, 6); // 2.5s + 200ms 尾巴
  });

  it("依次执行累加", () => {
    expect(armHoldEndMs([intent(0, 1), intent(0.5, 2)], "依次")).toBeCloseTo(3700, 6); // 3.5s + 200ms 尾巴
  });

  it("没有意图时不占用手臂", () => {
    expect(armHoldEndMs([], "同时")).toBe(0);
  });
});
