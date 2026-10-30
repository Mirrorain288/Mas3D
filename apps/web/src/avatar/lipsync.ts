import type { VoiceRef } from '@mas3d/protocol';
import type { Vowel } from './faceDriver';
import { visemeAt } from './visemes';

/** 低于这个 RMS 视为静音：口型必须归零，不能停在半张。 */
export const MOUTH_THRESHOLD = 0.02;
/** 上升每帧最多 0.9、下降每帧最多 0.25：说话时嘴要跟得上，收尾要稍微拖一点。 */
export const MOUTH_RISE_PER_FRAME = 0.9;
export const MOUTH_FALL_PER_FRAME = 0.25;
/** 句间强制闭唇的窗口长度。 */
export const SENTENCE_GAP_MS = 120;
/** 说完后多久解除嘴部通道抑制，把嘴还给表情层。 */
export const MOUTH_RECOVER_MS = 200;
const ANALYSER_FFT = 1024;

export function envelopeToMouth(rms: number, prev: number): number {
  const current = Number.isFinite(prev) ? Math.min(1, Math.max(0, prev)) : 0;
  const level = Number.isFinite(rms) ? Math.max(0, rms) : 0;
  if (level <= MOUTH_THRESHOLD) return Math.max(0, current - MOUTH_FALL_PER_FRAME);
  const target = Math.min(1, (level - MOUTH_THRESHOLD) * 6 + 0.15);
  const next = target > current
    ? Math.min(target, current + MOUTH_RISE_PER_FRAME)
    : Math.max(target, current - MOUTH_FALL_PER_FRAME);
  return next;
}

export interface MouthSink {
  setVisemes(visemes: Partial<Record<Vowel, number>>): void;
  setMouthSuppressed(on: boolean): void;
}

export interface AnalyserLike {
  fftSize: number;
  getFloatTimeDomainData(target: Float32Array): void;
  connect(target: unknown): unknown;
  disconnect(): void;
}

export interface SourceLike {
  buffer: unknown;
  onended: (() => void) | null;
  connect(target: unknown): unknown;
  start(when?: number): void;
  stop(): void;
  disconnect(): void;
}

export interface GainLike {
  gain: { value: number };
  connect(target: unknown): unknown;
  disconnect?(): void;
}

/** AudioContext 的最小结构，测试里可以塞假实现。 */
export interface AudioContextLike {
  destination: unknown;
  decodeAudioData(data: ArrayBuffer): Promise<{ duration: number }>;
  createBufferSource(): SourceLike;
  createAnalyser(): AnalyserLike;
  createGain?(): GainLike;
  resume?(): Promise<void>;
}

export type FetchLike = (input: string, init?: { headers?: Record<string, string> }) => Promise<{
  ok: boolean;
  status: number;
  arrayBuffer(): Promise<ArrayBuffer>;
}>;

export interface PlayOptions {
  headers?: Record<string, string>;
  volume?: number;
  fetchImpl?: FetchLike;
}

const clamp01 = (value: number): number => (Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0);

/**
 * 语音播放 + 口型（design D18/D19）。
 *
 * L0：AnalyserNode 的时域 RMS → envelopeToMouth → Fcl_MTH_A（外加 visemeAt 选的元音）。
 * L1：按 sentenceStartsMs 在句间强制闭唇 SENTENCE_GAP_MS。
 * 播放期间对 FaceDriver 打开嘴部通道抑制，结束后 200 ms 恢复。
 */
export class LipSync {
  speaking = false;
  /** 最近一帧的口型权重，DEV 探针用。 */
  lastMouth = 0;

  private source: SourceLike | null = null;
  private analyser: AnalyserLike | null = null;
  private gain: GainLike | null = null;
  private samples = new Float32Array(ANALYSER_FFT);
  private words: string[] = [];
  private sentenceStarts: number[] = [];
  private durationMs = 1;
  private startedAt = 0;
  private mouth = 0;
  private raf: number | null = null;
  private recoverTimer: ReturnType<typeof setTimeout> | null = null;
  private onEnded: (() => void) | null = null;

  constructor(
    private readonly ctx: AudioContextLike,
    private readonly sink: MouthSink,
    private readonly now: () => number = () => performance.now(),
  ) {}

  async play(ref: VoiceRef, url: string, options: PlayOptions = {}): Promise<void> {
    this.stop();
    const fetchImpl = options.fetchImpl ?? (fetch as unknown as FetchLike);
    const response = await (options.headers
      ? fetchImpl(url, { headers: options.headers })
      : fetchImpl(url, {}));
    if (!response.ok) throw new Error('voice http ' + response.status);
    const data = await response.arrayBuffer();
    const buffer = await this.ctx.decodeAudioData(data);
    const source = this.ctx.createBufferSource();
    source.buffer = buffer;
    const analyser = this.ctx.createAnalyser();
    analyser.fftSize = ANALYSER_FFT;
    source.connect(analyser);
    const gain = this.ctx.createGain?.();
    if (gain) {
      gain.gain.value = clamp01(options.volume ?? 1);
      analyser.connect(gain);
      gain.connect(this.ctx.destination);
    } else {
      analyser.connect(this.ctx.destination);
    }

    this.source = source;
    this.analyser = analyser;
    this.gain = gain ?? null;
    this.words = ref.englishText.split(/\s+/).filter((word) => word.length > 0);
    this.sentenceStarts = ref.sentenceStartsMs.slice();
    this.durationMs = Math.max(1, ref.durationMs || Math.round((buffer.duration ?? 0) * 1000));
    this.samples = new Float32Array(ANALYSER_FFT);
    this.mouth = 0;
    this.lastMouth = 0;
    this.startedAt = this.now();
    this.speaking = true;
    this.clearRecover();
    this.sink.setMouthSuppressed(true);

    const ended = new Promise<void>((resolve) => {
      this.onEnded = resolve;
    });
    source.onended = () => this.finish();
    source.start();
    this.loop();
    return ended;
  }

  /** 供测试手动推进一帧（真实运行时由 requestAnimationFrame 驱动）。 */
  advance(nowMs: number): void {
    const analyser = this.analyser;
    if (!analyser) return;
    const elapsed = Math.max(0, nowMs - this.startedAt);
    if (this.inGap(elapsed)) {
      this.mouth = 0;
      this.lastMouth = 0;
      this.sink.setVisemes({});
      return;
    }
    analyser.getFloatTimeDomainData(this.samples);
    let sum = 0;
    for (let i = 0; i < this.samples.length; i += 1) {
      const sample = this.samples[i] ?? 0;
      sum += sample * sample;
    }
    const rms = Math.sqrt(sum / Math.max(1, this.samples.length));
    const mouth = envelopeToMouth(rms, this.mouth);
    this.mouth = mouth;
    this.lastMouth = mouth;
    if (mouth < MOUTH_THRESHOLD) {
      this.sink.setVisemes({});
      return;
    }
    const vowel = visemeAt(this.words, elapsed / this.durationMs);
    this.sink.setVisemes(vowel ? { [vowel]: mouth } : { A: mouth });
  }

  stop(): void {
    this.finish();
  }

  dispose(): void {
    this.finish();
    this.clearRecover();
  }

  private inGap(elapsed: number): boolean {
    for (const start of this.sentenceStarts) {
      if (start <= 0) continue;
      if (elapsed >= start && elapsed < start + SENTENCE_GAP_MS) return true;
    }
    return false;
  }

  private loop(): void {
    if (!this.speaking) return;
    this.advance(this.now());
    if (typeof requestAnimationFrame === 'function') {
      this.raf = requestAnimationFrame(() => this.loop());
    }
  }

  private finish(): void {
    const source = this.source;
    const hadState = this.speaking || source !== null;
    this.source = null;
    this.speaking = false;
    this.lastMouth = 0;
    this.mouth = 0;
    if (typeof cancelAnimationFrame === 'function' && this.raf !== null) cancelAnimationFrame(this.raf);
    this.raf = null;
    if (source) {
      source.onended = null;
      try {
        source.stop();
      } catch {
        /* 已经播完的 source 再 stop 会抛，忽略 */
      }
      try {
        source.disconnect();
      } catch {
        /* 已被浏览器回收 */
      }
    }
    this.releaseNodes();
    // 先兑现 play() 的 promise，再安排抑制恢复：没有状态时也不能留下悬挂的 await
    const done = this.onEnded;
    this.onEnded = null;
    if (done) done();
    if (!hadState) return;
    this.sink.setVisemes({});
    this.clearRecover();
    this.recoverTimer = setTimeout(() => {
      this.recoverTimer = null;
      this.sink.setMouthSuppressed(false);
    }, MOUTH_RECOVER_MS);
  }

  /** 把音频节点从图上摘下来：analyser 与 gain 每句都新建，不摘会一直挂在 AudioContext 上。 */
  private releaseNodes(): void {
    const analyser = this.analyser;
    this.analyser = null;
    if (analyser) {
      try {
        analyser.disconnect();
      } catch {
        /* 已被浏览器回收 */
      }
    }
    const gain = this.gain;
    this.gain = null;
    if (gain?.disconnect) {
      try {
        gain.disconnect();
      } catch {
        /* 同上 */
      }
    }
  }

  private clearRecover(): void {
    if (this.recoverTimer !== null) {
      clearTimeout(this.recoverTimer);
      this.recoverTimer = null;
    }
  }
}