import { validatePlan, type MotionPlan } from './plan';

export type Emotion = 'neutral' | 'joy' | 'fun' | 'sorrow' | 'angry' | 'surprised';
export type Gaze = 'player' | 'away' | 'down' | 'up' | 'left' | 'right';
export type Gesture = 'none' | 'nod' | 'shake' | 'tilt' | 'wave' | 'lean_in' | 'look_away';

export interface ExpressionCue {
  emotion: Emotion;
  intensity: number;
  gaze: Gaze;
  gesture: Gesture;
}

export interface VoiceRef {
  audioUrl: string;
  durationMs: number;
  sentenceStartsMs: number[];
  englishText: string;
}

export type ErrorCode = 'bridge_down' | 'timeout' | 'model_error' | 'bad_request' | 'rate_limited';

export type ServerFrame =
  | { type: 'ack'; turnId: string }
  | { type: 'thinking'; turnId: string }
  | { type: 'reply'; turnId: string; text: string; cue?: ExpressionCue; voice?: VoiceRef }
  | { type: 'intent'; turnId: string; plan: MotionPlan }
  | { type: 'error'; turnId?: string; code: ErrorCode; message: string };

export const MAX_INPUT_CHARS = 300;

const EMOTIONS: readonly Emotion[] = ['neutral', 'joy', 'fun', 'sorrow', 'angry', 'surprised'];
const GAZES: readonly Gaze[] = ['player', 'away', 'down', 'up', 'left', 'right'];
const GESTURES: readonly Gesture[] = ['none', 'nod', 'shake', 'tilt', 'wave', 'lean_in', 'look_away'];
const ERROR_CODES: readonly ErrorCode[] = ['bridge_down', 'timeout', 'model_error', 'bad_request', 'rate_limited'];

export class ProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ProtocolError';
  }
}

export function fail(message: string): never {
  throw new ProtocolError(message);
}

export function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function str(obj: Record<string, unknown>, key: string, where: string): string {
  const v = obj[key];
  if (typeof v !== 'string') fail(`${where}.${key} must be a string`);
  return v;
}

function oneOf<T extends string>(obj: Record<string, unknown>, key: string, allowed: readonly T[], where: string): T {
  const v = obj[key];
  if (typeof v !== 'string' || !allowed.includes(v as T)) fail(`${where}.${key} is not one of ${allowed.join('|')}`);
  return v as T;
}

function cue(raw: unknown): ExpressionCue {
  if (!isRecord(raw)) fail('cue must be an object');
  const intensity = raw['intensity'];
  if (typeof intensity !== 'number' || !Number.isFinite(intensity) || intensity < 0 || intensity > 1) {
    fail('cue.intensity must be within 0..1');
  }
  return {
    emotion: oneOf(raw, 'emotion', EMOTIONS, 'cue'),
    intensity,
    gaze: oneOf(raw, 'gaze', GAZES, 'cue'),
    gesture: oneOf(raw, 'gesture', GESTURES, 'cue'),
  };
}

function voice(raw: unknown): VoiceRef {
  if (!isRecord(raw)) fail('voice must be an object');
  const durationMs = raw['durationMs'];
  if (typeof durationMs !== 'number' || !Number.isFinite(durationMs) || durationMs < 0) fail('voice.durationMs must be a non-negative number');
  const starts = raw['sentenceStartsMs'];
  if (!Array.isArray(starts) || starts.some((n) => typeof n !== 'number' || !Number.isFinite(n) || n < 0)) {
    fail('voice.sentenceStartsMs must be an array of non-negative numbers');
  }
  return {
    audioUrl: str(raw, 'audioUrl', 'voice'),
    durationMs,
    sentenceStartsMs: starts as number[],
    englishText: str(raw, 'englishText', 'voice'),
  };
}

export function parseServerFrame(raw: unknown): ServerFrame {
  if (!isRecord(raw)) fail('frame must be an object');
  switch (raw['type']) {
    case 'ack':
      return { type: 'ack', turnId: str(raw, 'turnId', 'ack') };
    case 'thinking':
      return { type: 'thinking', turnId: str(raw, 'turnId', 'thinking') };
    case 'reply': {
      const frame: ServerFrame = {
        type: 'reply',
        turnId: str(raw, 'turnId', 'reply'),
        text: str(raw, 'text', 'reply'),
      };
      // cue 可以缺省：表情推导在前端 deriveCue 里做（design.md D6），
      // 桥接侧只负责把模型回复原样送回来，避免在 Python 里复制一份情绪词表。
      if (raw['cue'] !== undefined) frame.cue = cue(raw['cue']);
      if (raw['voice'] !== undefined) frame.voice = voice(raw['voice']);
      return frame;
    }
    case 'intent':
      return { type: 'intent', turnId: str(raw, 'turnId', 'intent'), plan: validatePlan(raw['plan']) };
    case 'error': {
      const frame: ServerFrame = {
        type: 'error',
        code: oneOf(raw, 'code', ERROR_CODES, 'error'),
        message: str(raw, 'message', 'error'),
      };
      if (raw['turnId'] !== undefined) frame.turnId = str(raw, 'turnId', 'error');
      return frame;
    }
    default:
      return fail(`unknown frame type: ${String(raw['type'])}`);
  }
}
