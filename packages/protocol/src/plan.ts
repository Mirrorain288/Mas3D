import { fail, isRecord } from './frames';

export interface MotionIntent {
  key: string;
  speed: number;
  strength: number;
  hold: number;
  delay: number;
}

export type Operation = 'chat' | 'command' | 'release' | 'unsupported';
export type Timing = '同时' | '依次';

export interface MotionBase {
  change: boolean;
  name: string;
  motion: number;
  rest: boolean;
}

export interface MotionPlan {
  base: MotionBase;
  reply?: string;
  operation: Operation;
  timing: Timing;
  intents: MotionIntent[];
}

const OPERATIONS: readonly Operation[] = ['chat', 'command', 'release', 'unsupported'];
const TIMINGS: readonly Timing[] = ['同时', '依次'];

export const MAX_INTENTS = 4;
export const MAX_BASE_NAME = 40;
export const MAX_REPLY = 600;
const SPEED_RANGE = [0.4, 2] as const;
const STRENGTH_RANGE = [0, 1] as const;
const HOLD_RANGE = [0, 20] as const;
const DELAY_RANGE = [0, 30] as const;

function num(obj: Record<string, unknown>, key: string, min: number, max: number, where: string): number {
  const v = obj[key];
  if (typeof v !== 'number' || !Number.isFinite(v)) fail(`${where}.${key} must be a finite number`);
  if (v < min || v > max) fail(`${where}.${key} must be within ${min}..${max}`);
  return v;
}

function bool(obj: Record<string, unknown>, key: string, where: string): boolean {
  const v = obj[key];
  if (typeof v !== 'boolean') fail(`${where}.${key} must be a boolean`);
  return v;
}

function base(raw: unknown): MotionBase {
  if (!isRecord(raw)) fail('base must be an object');
  const name = raw['name'];
  if (typeof name !== 'string' || name.length === 0) fail('base.name must be a non-empty string');
  if (name.length > MAX_BASE_NAME) fail(`base.name must be at most ${MAX_BASE_NAME} characters`);
  return {
    change: bool(raw, 'change', 'base'),
    name,
    motion: num(raw, 'motion', 0, 1, 'base'),
    rest: bool(raw, 'rest', 'base'),
  };
}

function intent(raw: unknown, i: number): MotionIntent {
  const where = `intents[${i}]`;
  if (!isRecord(raw)) fail(`${where} must be an object`);
  const key = raw['key'];
  if (typeof key !== 'string' || key.length === 0) fail(`${where}.key must be a non-empty string`);
  return {
    key,
    speed: num(raw, 'speed', SPEED_RANGE[0], SPEED_RANGE[1], where),
    strength: num(raw, 'strength', STRENGTH_RANGE[0], STRENGTH_RANGE[1], where),
    hold: num(raw, 'hold', HOLD_RANGE[0], HOLD_RANGE[1], where),
    delay: num(raw, 'delay', DELAY_RANGE[0], DELAY_RANGE[1], where),
  };
}

export function validatePlan(raw: unknown): MotionPlan {
  if (!isRecord(raw)) fail('plan must be an object');
  const operation = raw['operation'];
  if (typeof operation !== 'string' || !OPERATIONS.includes(operation as Operation)) {
    fail(`plan.operation is not one of ${OPERATIONS.join('|')}`);
  }
  const timing = raw['timing'];
  if (typeof timing !== 'string' || !TIMINGS.includes(timing as Timing)) fail('plan.timing must be 同时 or 依次');
  const intentsRaw = raw['intents'];
  if (!Array.isArray(intentsRaw)) fail('plan.intents must be an array');
  if (intentsRaw.length > MAX_INTENTS) fail(`plan.intents must have at most ${MAX_INTENTS} entries`);
  const planBase = base(raw['base']);
  const intents = intentsRaw.map(intent);
  const op = operation as Operation;
  if (op === 'command') {
    if (intents.length === 0 && !planBase.change) fail('a command plan needs at least one intent or a base change');
  } else if (intents.length > 0) {
    fail(`${op} plan must not carry intents`);
  }
  const plan: MotionPlan = { base: planBase, operation: op, timing: timing as Timing, intents };
  const reply = raw['reply'];
  if (reply !== undefined) {
    if (typeof reply !== 'string') fail('plan.reply must be a string');
    if (reply.length > MAX_REPLY) fail(`plan.reply must be at most ${MAX_REPLY} characters`);
    plan.reply = reply;
  }
  return plan;
}
