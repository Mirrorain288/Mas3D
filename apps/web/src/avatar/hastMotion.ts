import type { MotionIntent, MotionPlan } from "@mas3d/protocol";

/** HAST-SEC 的 key 形如 "right-挥手打招呼" / "左臂上抬" / "right-arm-raise"，先按自然语言关键词归类。 */
export type ArmSide = "left" | "right" | "both";
export type ArmAction = "raise" | "lower" | "forward" | "backward" | "left" | "right" | "wave" | "hold" | "idle";

export interface ArmRotation {
  x: number;
  y: number;
  z: number;
}

export interface ArmTargets {
  upperArm: ArmRotation;
  forearm: ArmRotation;
}

export interface ParsedIntent {
  side: ArmSide;
  action: ArmAction;
  height: number;
  intent: MotionIntent;
}

export interface Segment {
  side: ArmSide;
  action: ArmAction;
  strength: number;
  startMs: number;
  durationMs: number;
  height: number;
}

/** 归一化骨骼的安全边界：再大就会穿到身体/画面外。 */
export const ROTATION_LIMIT = 2.2;
/** 单段最短时长：speed>1 时不至于瞬间弹到位。 */
export const MIN_DURATION_MS = 180;

const SIDE_WORDS: ReadonlyArray<[RegExp, ArmSide]> = [
  [/^(?:both|双臂|双手|两只手)/i, "both"],
  [/^(?:right|r-|右)/i, "right"],
  [/^(?:left|l-|左)/i, "left"],
];

const ACTION_WORDS: ReadonlyArray<[RegExp, ArmAction]> = [
  [/挥|摆|wave/i, "wave"],
  [/上抬|抬起|举起|举|raise|up/i, "raise"],
  [/下放|放下|垂下|lower|down/i, "lower"],
  [/前伸|向前|伸出|forward/i, "forward"],
  [/后移|向后|收回|backward|back/i, "backward"],
  [/左移|向左|left/i, "left"],
  [/右移|向右|right/i, "right"],
  [/保持|保持不动|hold/i, "hold"],
  [/自然活动|待机|idle/i, "idle"],
];

/** 把后端 key 拆成 侧别 / 动作 / 高度加成。认不出来的 key 归为 idle（不改姿态）。 */
export function parseIntentKey(key: string): { side: ArmSide; action: ArmAction; height: number } {
  const text = key.trim().toLowerCase();
  let side: ArmSide = "both";
  let rest = text;
  for (const [pattern, value] of SIDE_WORDS) {
    const match = text.match(pattern);
    if (match) {
      side = value;
      // 侧别词先摘掉，否则 "left-arm-hold" 会被当成向左、右臂右移会被当成右侧别
      rest = text.slice(match[0].length).replace(/^[-_\s]*(?:arm|臂)?[-_\s]*/, "");
      break;
    }
  }
  let action: ArmAction = "idle";
  for (const [pattern, value] of ACTION_WORDS) {
    if (pattern.test(rest)) {
      action = value;
      break;
    }
  }
  const height = /高于头顶|overhead|above/i.test(text) ? 0.45 : /齐平头部|height|head/i.test(text) ? 0.22 : 0;
  return { side, action, height };
}

function clamp(value: number): number {
  return Math.max(-ROTATION_LIMIT, Math.min(ROTATION_LIMIT, value));
}

/**
 * 动作 → 单臂旋转偏移。约定：左臂坐姿 z=+armDown、右臂 z=-armDown（见 SIT_POSE），
 * 因此"抬"对左臂是减小 z、对右臂是增大 z，offset 由 applyArmTargets 按 side 取号。
 */
export function armTargets(action: ArmAction, strength: number, height = 0, phase = 0): ArmTargets {
  const s = Math.max(0, Math.min(1, strength));
  const zero = { x: 0, y: 0, z: 0 };
  switch (action) {
    case "raise":
      return { upperArm: { x: 0, y: 0, z: clamp(-(0.9 * s + height)) }, forearm: { x: 0, y: clamp(-0.25 * s), z: 0 } };
    case "lower":
      return { upperArm: { x: 0, y: 0, z: clamp(0.18 * s) }, forearm: { x: 0, y: 0, z: 0 } };
    case "forward":
      return { upperArm: { x: clamp(-0.8 * s), y: 0, z: clamp(-0.35 * s) }, forearm: { x: clamp(-0.5 * s), y: 0, z: 0 } };
    case "backward":
      return { upperArm: { x: clamp(0.42 * s), y: 0, z: 0 }, forearm: { x: clamp(0.2 * s), y: 0, z: 0 } };
    case "left":
      return { upperArm: { x: 0, y: clamp(-0.7 * s), z: clamp(-0.4 * s) }, forearm: { x: 0, y: clamp(-0.3 * s), z: 0 } };
    case "right":
      return { upperArm: { x: 0, y: clamp(0.7 * s), z: clamp(-0.4 * s) }, forearm: { x: 0, y: clamp(0.3 * s), z: 0 } };
    case "wave":
      return {
        upperArm: { x: 0, y: clamp(0.2 * s * Math.sin(phase)), z: clamp(-(0.95 * s + height)) },
        forearm: { x: 0, y: 0, z: clamp(0.55 * s * Math.sin(phase * 2)) },
      };
    case "hold":
      return { upperArm: { x: 0, y: 0, z: clamp(-(0.45 * s + height)) }, forearm: { x: 0, y: 0, z: 0 } };
    default:
      return { upperArm: { ...zero }, forearm: { ...zero } };
  }
}

/** 计划 → 时间轴。同时：各自 delay 后并行；依次：按顺序累计（delay 仍然生效）。 */
export function planSegments(plan: MotionPlan): Segment[] {
  if (plan.operation !== "command" || plan.intents.length === 0) return [];
  const segments: Segment[] = [];
  let cursor = 0;
  for (const intent of plan.intents) {
    const parsed = parseIntentKey(intent.key);
    const speed = Math.max(0.4, Math.min(2, intent.speed));
    const durationMs = Math.max(MIN_DURATION_MS, (intent.hold * 1000) / speed);
    const startMs = plan.timing === "依次" ? cursor + intent.delay * 1000 : intent.delay * 1000;
    segments.push({
      side: parsed.side,
      action: parsed.action,
      strength: Math.max(0, Math.min(1, intent.strength)),
      startMs,
      durationMs,
      height: parsed.height,
    });
    cursor = startMs + durationMs;
  }
  return segments;
}

export interface ArmPose {
  left: ArmTargets;
  right: ArmTargets;
}

function restTargets(): ArmTargets {
  return { upperArm: { x: 0, y: 0, z: 0 }, forearm: { x: 0, y: 0, z: 0 } };
}

/** 右臂的 y/z 与左臂反号：肩关节镜像，抬起与内外摆方向相反。 */
function mirrorTargets(targets: ArmTargets): ArmTargets {
  return {
    upperArm: { x: targets.upperArm.x, y: -targets.upperArm.y, z: -targets.upperArm.z },
    forearm: { x: targets.forearm.x, y: -targets.forearm.y, z: -targets.forearm.z },
  };
}

/** 当前时刻该给双臂的偏移；没有计划或已结束返回 null（回到待机）。 */
export function sampleArmPose(segments: readonly Segment[], nowMs: number): ArmPose | null {
  let left: ArmTargets | null = null;
  let right: ArmTargets | null = null;
  for (const segment of segments) {
    const local = nowMs - segment.startMs;
    if (local < 0 || local > segment.durationMs) continue;
    const t = segment.durationMs === 0 ? 1 : local / segment.durationMs;
    // sin 包络：起手与收势都回到 0，整段结束后自然回待机
    const envelope = Math.sin(Math.PI * t);
    const phase = Math.PI * 2 * 1.6 * t;
    const targets = armTargets(segment.action, segment.strength * envelope, segment.height, phase);
    if (segment.side === "left" || segment.side === "both") left = targets;
    if (segment.side === "right" || segment.side === "both") right = targets;
  }
  if (!left && !right) return null;
  return {
    left: left ?? restTargets(),
    right: right ? mirrorTargets(right) : restTargets(),
  };
}

/** 记录一段计划的时间轴，由渲染循环按 nowMs 采样。 */
export class HastMotion {
  private segments: Segment[] = [];
  private startedAt = 0;

  get active(): boolean {
    return this.segments.length > 0;
  }

  start(plan: MotionPlan, nowMs: number): boolean {
    const segments = planSegments(plan);
    this.segments = segments;
    this.startedAt = nowMs;
    return segments.length > 0;
  }

  sample(nowMs: number): ArmPose | null {
    if (this.segments.length === 0) return null;
    const pose = sampleArmPose(this.segments, nowMs - this.startedAt);
    const end = this.segments.reduce((max, s) => Math.max(max, s.startMs + s.durationMs), 0);
    if (nowMs - this.startedAt > end) this.segments = [];
    return pose;
  }

  stop(): void {
    this.segments = [];
  }
}