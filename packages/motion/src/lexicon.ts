import type { Emotion, Gesture } from '@mas3d/protocol';

export interface EmotionEntry {
  words: string[];
  weight: number;
}

/** 越靠后的情绪在权重持平时优先，与 design D6 的 `sorrow<angry<surprised<joy<fun` 次序一致。 */
export const EMOTION_PRIORITY: readonly Emotion[] = ['neutral', 'sorrow', 'angry', 'surprised', 'joy', 'fun'];

export const EMOTION_LEXICON: Record<Emotion, EmotionEntry[]> = {
  neutral: [],
  sorrow: [
    { words: ['难过', '伤心', '遗憾', '失落', '哭'], weight: 0.9 },
    { words: ['唉', '可惜', '对不起'], weight: 0.6 },
  ],
  angry: [
    { words: ['生气', '愤怒', '讨厌', '烦'], weight: 0.9 },
    { words: ['哼', '别这样'], weight: 0.5 },
  ],
  surprised: [
    { words: ['惊讶', '震惊', '竟然', '居然', '没想到'], weight: 0.85 },
    { words: ['哇', '咦', '真的吗'], weight: 0.6 },
  ],
  joy: [
    { words: ['开心', '高兴', '喜欢', '笑', '太好了'], weight: 0.8 },
    { words: ['谢谢', '好呀'], weight: 0.5 },
  ],
  fun: [
    { words: ['有趣', '好玩', '逗', '嘻嘻'], weight: 0.8 },
    { words: ['哈哈', '嘿'], weight: 0.7 },
  ],
};

/** 顺序即优先级：更具体的说法排在前面，避免「嗯？」被「嗯」抢走。 */
export const GESTURE_LEXICON: { gesture: Gesture; pattern: RegExp }[] = [
  { gesture: 'tilt', pattern: /嗯？|歪头/ },
  { gesture: 'nod', pattern: /点头|嗯|好的/ },
  { gesture: 'shake', pattern: /摇头|不/ },
  { gesture: 'wave', pattern: /挥手|拜拜|再见/ },
  { gesture: 'lean_in', pattern: /凑近|靠近/ },
  { gesture: 'look_away', pattern: /移开|别看我/ },
];
