import type { Vowel } from './faceDriver';

/**
 * 英文 grapheme → VRoid 元音口型（design D18 的 L2 层）。
 *
 * 这个 VRM 没有音素级时间轴，只有整段英文台词与总时长，所以口型只能按
 * 「当前播到第几个词」近似选元音；真正的开合幅度来自 L0 的音频包络，
 * 这里只决定用哪个 Fcl_MTH_A/I/U/E/O。
 */
export const VOWEL_OF: Record<string, Vowel> = {
  a: 'A',
  'ɑ': 'A',
  i: 'I',
  'ɪ': 'I',
  y: 'I',
  u: 'U',
  'ʊ': 'U',
  w: 'U',
  e: 'E',
  'ɛ': 'E',
  o: 'O',
  'ɔ': 'O',
};

export interface VisemeShare {
  v: Vowel;
  w: number;
}

/** 把一个词里出现的元音按占比排开；没有元音返回空数组（闭唇/停顿）。 */
export function wordToVisemes(word: string): VisemeShare[] {
  const counts = new Map<Vowel, number>();
  for (const ch of word.toLowerCase()) {
    const vowel = VOWEL_OF[ch];
    if (!vowel) continue;
    counts.set(vowel, (counts.get(vowel) ?? 0) + 1);
  }
  const total = [...counts.values()].reduce((sum, n) => sum + n, 0);
  if (total === 0) return [];
  // 稳定排序：同权重保留首次出现顺序，'monika' 的 o/i/a 才会以 O 开头。
  return [...counts.entries()].map(([v, n]) => ({ v, w: n / total })).sort((a, b) => b.w - a.w);
}

export function splitWords(text: string): string[] {
  return text.split(/\s+/).filter((word) => word.length > 0);
}

/** 按「已播比例」挑当前词的主元音；没有可用元音返回 null。 */
export function visemeAt(words: readonly string[], ratio: number): Vowel | null {
  if (words.length === 0) return null;
  const cleaned = words.map((word) => word.replace(/[^A-Za-z'ɑɪʊɛɔ]/g, ''));
  const total = cleaned.reduce((sum, word) => sum + word.length, 0);
  if (total === 0) return null;
  const target = Math.min(1, Math.max(0, Number.isFinite(ratio) ? ratio : 0)) * total;
  let cursor = 0;
  for (const word of cleaned) {
    cursor += word.length;
    if (target <= cursor || word === cleaned[cleaned.length - 1]) {
      const shares = wordToVisemes(word);
      const first = shares[0];
      return first ? first.v : null;
    }
  }
  return null;
}
