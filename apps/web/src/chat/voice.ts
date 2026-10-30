import { useEffect, useMemo, useRef } from "react";
import type { VoiceRef } from "@mas3d/protocol";
import type { SceneApi } from "../scene/Scene";

/** 语音后端不可用后冷却多久再试（design D19 的降级链）。 */
export const VOICE_COOLDOWN_MS = 60_000;
/** 一轮最多合成多少句，避免异常回复把队列拖长。 */
export const VOICE_MAX_SENTENCES = 40;

export interface VoiceSpeakOptions {
  baseUrl: string;
  token: string | null;
  volume: number;
  subtitlesOnly: boolean;
}

export interface VoiceApi {
  speak(text: string): void;
  stop(): void;
}

type SynthResult =
  | { kind: "voice"; voice: VoiceRef }
  | { kind: "end" }
  | { kind: "unavailable"; message: string };

/**
 * 逐句配音流水线（design D16/D18）：先请求第 0 句，播放它的同时请求第 1 句，
 * 客户端队列深度 ≤ 2；后端 400（下标越界）表示这一轮没有更多句子。
 */
export class VoicePlayer {
  private turn = 0;
  private coolingUntil = 0;
  private notified = false;

  constructor(
    private readonly getApi: () => SceneApi | null,
    private readonly onNotice: (message: string) => void,
    private readonly now: () => number = () => Date.now(),
  ) {}

  stop(): void {
    this.turn += 1;
    this.getApi()?.stopVoice();
  }

  get cooling(): boolean {
    return this.now() < this.coolingUntil;
  }

  speak(text: string, options: VoiceSpeakOptions): void {
    if (options.subtitlesOnly) return;
    if (this.cooling) return;
    const clean = text.trim();
    if (!clean) return;
    this.turn += 1;
    void this.run(clean, this.turn, options);
  }

  private async run(text: string, mine: number, options: VoiceSpeakOptions): Promise<void> {
    let next: Promise<SynthResult> | null = null;
    for (let index = 0; index < VOICE_MAX_SENTENCES; index += 1) {
      const result = await (next ?? this.synth(text, index, options));
      next = null;
      if (mine !== this.turn) return;
      if (result.kind === "end") return;
      if (result.kind === "unavailable") {
        this.cool(result.message);
        return;
      }
      // 边播边合成下一句：队列里最多两个未完成请求
      next = this.synth(text, index + 1, options);
      await this.play(result.voice, options, mine);
      if (mine !== this.turn) return;
    }
  }

  private async play(voice: VoiceRef, options: VoiceSpeakOptions, mine: number): Promise<void> {
    const api = this.getApi();
    if (!api) return;
    const headers: Record<string, string> | undefined = options.token
      ? { Authorization: "Bearer " + options.token }
      : undefined;
    try {
      await api.speakVoice(
        voice,
        options.baseUrl + voice.audioUrl,
        headers ? { headers, volume: options.volume } : { volume: options.volume },
      );
    } catch {
      if (mine === this.turn) this.cool("语音播放失败，先只看字幕。");
    }
  }

  private async synth(text: string, index: number, options: VoiceSpeakOptions): Promise<SynthResult> {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (options.token) headers.Authorization = "Bearer " + options.token;
    let response: Response;
    try {
      response = await fetch(options.baseUrl + "/mas3d/api/voice/synth", {
        method: "POST",
        headers,
        body: JSON.stringify({ text, sentence: index }),
      });
    } catch {
      return { kind: "unavailable", message: "语音服务没有响应，先只看字幕。" };
    }
    if (response.status === 400) return { kind: "end" };
    if (!response.ok) {
      const detail = await response.json().catch(() => null);
      const message = typeof detail?.message === "string" ? detail.message : "语音暂时不可用，先只看字幕。";
      return { kind: "unavailable", message };
    }
    const payload = (await response.json().catch(() => null)) as { ok?: boolean; voice?: VoiceRef } | null;
    if (!payload?.voice) return { kind: "unavailable", message: "语音返回格式不对，先只看字幕。" };
    return { kind: "voice", voice: payload.voice };
  }

  private cool(message: string): void {
    this.coolingUntil = this.now() + VOICE_COOLDOWN_MS;
    if (this.notified) return;
    this.notified = true;
    this.onNotice(message);
  }
}

interface UseVoiceInput {
  baseUrl: string;
  token: string | null;
  api: SceneApi | null;
  volume: number;
  subtitlesOnly: boolean;
  onNotice: (message: string) => void;
}

/** 把 VoicePlayer 绑到 React 生命周期上；api/音量/开关变化不需要重建播放器。 */
export function useVoice(input: UseVoiceInput): VoiceApi {
  const apiRef = useRef(input.api);
  const inputRef = useRef(input);
  apiRef.current = input.api;
  inputRef.current = input;

  const playerRef = useRef<VoicePlayer | null>(null);
  if (!playerRef.current) {
    playerRef.current = new VoicePlayer(
      () => apiRef.current,
      (message) => inputRef.current.onNotice(message),
    );
  }
  const player = playerRef.current;

  useEffect(() => () => player.stop(), [player]);

  return useMemo(
    () => ({
      speak(text: string): void {
        player.speak(text, {
          baseUrl: inputRef.current.baseUrl,
          token: inputRef.current.token,
          volume: inputRef.current.volume,
          subtitlesOnly: inputRef.current.subtitlesOnly,
        });
      },
      stop(): void {
        player.stop();
      },
    }),
    [player],
  );
}
