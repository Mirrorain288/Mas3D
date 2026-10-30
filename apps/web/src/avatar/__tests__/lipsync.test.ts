import { afterEach, describe, expect, it, vi } from 'vitest';
import type { VoiceRef } from '@mas3d/protocol';
import {
  MOUTH_FALL_PER_FRAME,
  MOUTH_RECOVER_MS,
  MOUTH_THRESHOLD,
  LipSync,
  envelopeToMouth,
  type AudioContextLike,
  type MouthSink,
} from '../lipsync';
import { splitWords, visemeAt, wordToVisemes } from '../visemes';

function fakeSink(): MouthSink & { visemes: Array<Partial<Record<string, number>>>; suppressed: boolean[] } {
  const sink = {
    visemes: [] as Array<Partial<Record<string, number>>>,
    suppressed: [] as boolean[],
    setVisemes(visemes: Partial<Record<string, number>>) {
      sink.visemes.push(visemes);
    },
    setMouthSuppressed(on: boolean) {
      sink.suppressed.push(on);
    },
  };
  return sink;
}

function fakeContext(level: number): AudioContextLike {
  return {
    destination: {},
    decodeAudioData: async () => ({ duration: 1 }),
    createBufferSource: () => ({
      buffer: null,
      onended: null,
      connect: () => undefined,
      start: () => undefined,
      stop: () => undefined,
      disconnect: () => undefined,
    }),
    createAnalyser: () => ({
      fftSize: 0,
      getFloatTimeDomainData: (target: Float32Array) => target.fill(level),
      connect: () => undefined,
      disconnect: () => undefined,
    }),
  };
}

const REF: VoiceRef = {
  audioUrl: '/mas3d/api/voice/x.wav',
  durationMs: 1000,
  sentenceStartsMs: [0, 500],
  englishText: 'hello world',
};

function playOnce(lip: LipSync): Promise<void> {
  return lip.play(REF, REF.audioUrl, {
    fetchImpl: async () => ({ ok: true, status: 200, arrayBuffer: async () => new ArrayBuffer(8) }),
  });
}

afterEach(() => {
  vi.useRealTimers();
});

describe('envelopeToMouth', () => {
  it('falls back to zero on silence and never goes negative', () => {
    expect(envelopeToMouth(0, 0.5)).toBeCloseTo(0.5 - MOUTH_FALL_PER_FRAME, 5);
    expect(envelopeToMouth(0, 0.05)).toBe(0);
    expect(envelopeToMouth(-1, 0.02)).toBe(0);
    expect(envelopeToMouth(Number.NaN, 0.1)).toBe(0);
  });

  it('rises faster than it falls', () => {
    const rise = envelopeToMouth(0.6, 0);
    const fall = 1 - envelopeToMouth(0, 1);
    expect(rise).toBeCloseTo(0.9, 5);
    expect(fall).toBeCloseTo(MOUTH_FALL_PER_FRAME, 5);
    expect(rise).toBeGreaterThan(fall);
  });

  it('treats sub-threshold noise as silence', () => {
    expect(envelopeToMouth(MOUTH_THRESHOLD, 0.4)).toBeLessThan(0.4);
  });
});

describe('wordToVisemes', () => {
  it('starts with O for monika', () => {
    expect(wordToVisemes('monika')[0]?.v).toBe('O');
  });

  it('contains U for wow', () => {
    expect(wordToVisemes('wow').some((share) => share.v === 'U')).toBe(true);
  });

  it('returns nothing for a word without vowels', () => {
    expect(wordToVisemes('shh')).toEqual([]);
  });

  it('picks the viseme by played ratio', () => {
    const words = splitWords('wow monika');
    expect(visemeAt(words, 0)).toBe('U');
    expect(visemeAt(words, 1)).toBe('O');
    expect(visemeAt(['shh'], 0.5)).toBeNull();
    expect(visemeAt([], 0.5)).toBeNull();
  });
});

describe('LipSync', () => {
  it('drives the mouth from the envelope and releases suppression after the tail', async () => {
    vi.useFakeTimers();
    const sink = fakeSink();
    const lip = new LipSync(fakeContext(0.3), sink, () => 0);
    const done = playOnce(lip);
    await vi.waitFor(() => expect(lip.speaking).toBe(true));
    expect(sink.suppressed).toEqual([true]);

    lip.advance(100);
    const last = sink.visemes.at(-1) ?? {};
    expect(Object.keys(last).length).toBe(1);
    expect(lip.lastMouth).toBeGreaterThan(MOUTH_THRESHOLD);

    lip.advance(520);
    expect(sink.visemes.at(-1)).toEqual({});

    lip.stop();
    expect(lip.speaking).toBe(false);
    expect(sink.suppressed).toEqual([true]);
    vi.advanceTimersByTime(MOUTH_RECOVER_MS - 1);
    expect(sink.suppressed).toEqual([true]);
    vi.advanceTimersByTime(1);
    expect(sink.suppressed).toEqual([true, false]);
    await done;
  });

  it('closes the mouth when the analyser hears nothing', async () => {
    const sink = fakeSink();
    const lip = new LipSync(fakeContext(0), sink, () => 0);
    void playOnce(lip);
    await vi.waitFor(() => expect(lip.speaking).toBe(true));
    lip.advance(10);
    expect(sink.visemes.at(-1)).toEqual({});
    lip.stop();
  });
});
