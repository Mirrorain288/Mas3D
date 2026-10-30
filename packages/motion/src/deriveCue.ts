import type { Emotion, ExpressionCue, Gaze, Gesture } from '@mas3d/protocol';
import { EMOTION_LEXICON, EMOTION_PRIORITY, GESTURE_LEXICON } from './lexicon';

const QUOTED = /["“](.*?)["”]|'(.*?)'|「(.*?)」|『(.*?)』/g;
const REPORTED = /(?:说|表示)[：:]?\s*(?=["“「『'])/g;
const REPEAT = /(.)\1{2,}/g;

const BASE_INTENSITY = 0.5;
const EXCLAMATION_BONUS = 0.15;
const REPEAT_BONUS = 0.1;

export interface DeriveCueOptions {
  questionToPlayer?: boolean;
}

/** 剥离引号内的转述与「说：…」的引子，只让叙述部分参与情绪判断。 */
export function stripQuoted(text: string): string {
  return text.replace(QUOTED, ' ').replace(REPORTED, ' ');
}

function count(text: string, needle: string): number {
  if (needle.length === 0) return 0;
  let hits = 0;
  let from = 0;
  for (;;) {
    const at = text.indexOf(needle, from);
    if (at < 0) return hits;
    hits += 1;
    from = at + needle.length;
  }
}

function scoreEmotions(narrative: string): { emotion: Emotion; score: number } {
  let best: { emotion: Emotion; score: number } = { emotion: 'neutral', score: 0 };
  for (const emotion of EMOTION_PRIORITY) {
    let score = 0;
    for (const entry of EMOTION_LEXICON[emotion]) {
      for (const word of entry.words) {
        if (count(narrative, word) > 0) score = Math.max(score, entry.weight);
      }
    }
    if (score > 0 && score >= best.score) best = { emotion, score };
  }
  return best;
}

function pickGesture(narrative: string): Gesture {
  for (const { gesture, pattern } of GESTURE_LEXICON) {
    if (pattern.test(narrative)) return gesture;
  }
  return 'none';
}

function pickGaze(emotion: Emotion, gesture: Gesture, question: boolean): Gaze {
  if (question) return 'player';
  if (gesture === 'look_away') return 'away';
  if (emotion === 'sorrow') return 'down';
  if (emotion === 'surprised') return 'up';
  return 'player';
}

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value));
}

export function deriveCue(text: string, opts: DeriveCueOptions = {}): ExpressionCue {
  const narrative = stripQuoted(text ?? '');
  const trimmed = narrative.trim();
  const { emotion, score } = scoreEmotions(trimmed);
  const exclamations = count(trimmed, '!') + count(trimmed, '！');
  const repeats = trimmed.match(REPEAT)?.length ?? 0;
  const question = /[?？]\s*$/.test(trimmed) || opts.questionToPlayer === true;
  const gesture = pickGesture(trimmed);
  const intensity = clamp01(BASE_INTENSITY + (score > 0 ? (score - 0.5) * 0.4 : 0) + exclamations * EXCLAMATION_BONUS + repeats * REPEAT_BONUS);
  return {
    emotion,
    intensity,
    gaze: pickGaze(emotion, gesture, question),
    gesture,
  };
}
