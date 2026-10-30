import type { MotionIntent, Timing } from "@mas3d/protocol";
import type { PoseDelta } from "@mas3d/motion";

/** 归一化骨骼节点只需要 rotation 三个分量，便于单测用普通对象替代。 */
export interface PoseBone {
  rotation: { x: number; y: number; z: number };
}

export type PoseBones = Record<"head" | "neck" | "spine" | "upperArmL" | "upperArmR", PoseBone>;

/** 头部的俯仰/偏航/侧倾由颈、脊柱分担一点，避免只有头在动。 */
const NECK_SHARE = 0.35;
const SPINE_SHARE = 0.5;
/** Tier2 动作结束后再多占一小会儿手臂，避免和 Tier1 抢同一帧。 */
const ARM_TAIL_MS = 200;

/**
 * 把 Tier1 手势的骨骼偏移叠加到当前帧的骨骼旋转上。
 * 调用顺序很关键：每帧先由待机层重置基础旋转，再叠加手势。
 * 归一化骨骼里左臂 z 增大是「放下」，抬手要减；右臂方向相反。
 */
export function applyPose(bones: PoseBones, delta: PoseDelta, weight = 1): void {
  const w = weight <= 0 ? 0 : weight >= 1 ? 1 : weight;
  if (w === 0) return;
  const head = bones.head.rotation;
  head.x += delta.headPitch * w;
  head.y += delta.headYaw * w;
  head.z += delta.headRoll * w;
  bones.neck.rotation.x += delta.headPitch * NECK_SHARE * w;
  bones.spine.rotation.x += delta.spineBend * SPINE_SHARE * w;
  bones.upperArmL.rotation.z -= delta.armRaiseL * w;
  bones.upperArmR.rotation.z += delta.armRaiseR * w;
}

/**
 * Tier2 占用手臂的时间窗：同时执行取最长，依次执行累加，再补一点尾巴。
 * HAST-SEC 的 hold/delay 单位是秒。
 */
export function armHoldEndMs(intents: readonly MotionIntent[], timing: Timing): number {
  if (intents.length === 0) return 0;
  const spans = intents.map((intent) => (Math.max(0, intent.delay) + Math.max(0, intent.hold)) * 1000);
  const span = timing === "依次" ? spans.reduce((sum, v) => sum + v, 0) : Math.max(...spans);
  return span + ARM_TAIL_MS;
}
