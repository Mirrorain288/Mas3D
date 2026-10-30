import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { MAX_INPUT_CHARS } from "@mas3d/protocol";
import type { ExpressionCue, MotionPlan } from "@mas3d/protocol";
import { loadSettings, normalizeSettings, saveSettings, type Settings } from "../settings";
import type { SceneApi } from "../scene/Scene";
import { MAX_TURNS, type Turn } from "./store";
import { useBridge } from "./useBridge";
import { useVoice } from "./voice";

const clamp01 = (value: number): number => Math.min(1, Math.max(0, value));

const ENV = import.meta.env as unknown as Record<string, string | undefined>;
// 默认指向 AstrBot 里的本机桥接（8790）；脚本化测试用 VITE_MAS3D_WS 改成 mock。
const BRIDGE_URL = ENV.VITE_MAS3D_WS ?? "ws://127.0.0.1:8790/mas3d/ws";
const ENV_TOKEN = ENV.VITE_MAS3D_TOKEN ?? null;

function queryToken(): string | null {
  if (typeof window === "undefined") return ENV_TOKEN;
  try {
    const fromUrl = new URLSearchParams(window.location.search).get("token");
    return fromUrl ?? ENV_TOKEN;
  } catch {
    return ENV_TOKEN;
  }
}

/** 语音端点与 WS 同源：把 ws:// 换成 http://（design D16：只走本机桥接）。 */
function httpBase(wsUrl: string): string {
  try {
    const url = new URL(wsUrl);
    url.protocol = url.protocol === "wss:" ? "https:" : "http:";
    return url.origin;
  } catch {
    return "http://127.0.0.1:8790";
  }
}

const VOICE_BASE = httpBase(BRIDGE_URL);

const PHASE_LABEL: Record<string, string> = {
  idle: "待机",
  sending: "发送中",
  thinking: "她正在想…",
  streaming: "说话中",
  error: "需要重试",
};

interface ChatPanelProps {
  api: SceneApi | null;
}

const ROLE_LABEL: Record<Turn["role"], string> = {
  player: "你",
  monika: "Monika",
  backend: "动作后端",
  system: "系统",
};

/** 对话面板：输入、消息流、状态机可视化、设置与导出。 */
export function ChatPanel({ api }: ChatPanelProps) {
  const [settings, setSettings] = useState<Settings>(() => loadSettings());
  const [draft, setDraft] = useState("");
  const [showSettings, setShowSettings] = useState(false);
  const [confirmClear, setConfirmClear] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const logRef = useRef<HTMLDivElement | null>(null);
  const apiRef = useRef<SceneApi | null>(api);
  const settingsRef = useRef(settings);
  apiRef.current = api;
  settingsRef.current = settings;

  const handlers = useMemo(
    () => ({
      onCue(cue: ExpressionCue): void {
        const sceneApi = apiRef.current;
        if (!sceneApi) return;
        sceneApi.setEmotion(cue.emotion, clamp01(cue.intensity));
        sceneApi.setGaze(cue.gaze);
        if (cue.gesture !== "none") {
          sceneApi.playGesture(cue.gesture, clamp01(cue.intensity * settingsRef.current.gestureBoost));
        }
      },
      onPlan(plan: MotionPlan): void {
        apiRef.current?.holdArmsFor(plan);
      },
      onPersist(ok: boolean, reason?: "quota" | "unavailable"): void {
        setNotice(
          ok
            ? null
            : reason === "quota"
              ? "浏览器存储已满，这一段对话不会被保存。"
              : "浏览器禁用了本地存储，这一段对话不会被保存。",
        );
      },
    }),
    [],
  );

  const token = useMemo(() => queryToken(), []);
  const bridge = useBridge(BRIDGE_URL, token, handlers);
  const busy = bridge.phase === "sending" || bridge.phase === "thinking" || bridge.phase === "streaming";

  const voice = useVoice({
    baseUrl: VOICE_BASE,
    token,
    api,
    volume: settings.volume,
    subtitlesOnly: settings.subtitlesOnly,
    onNotice: setNotice,
  });

  // 每来一条新的 Monika 回复播一次语音；按 turn.id 去重，兜底台词（error 阶段）不配音
  const spokenRef = useRef<string | null>(null);
  useEffect(() => {
    const last = bridge.turns[bridge.turns.length - 1];
    if (!last || last.role !== "monika" || bridge.phase !== "idle") return;
    if (spokenRef.current === last.id) return;
    spokenRef.current = last.id;
    voice.speak(last.text);
  }, [bridge.turns, bridge.phase, voice]);

  useEffect(() => {
    api?.setCameraSensitivity(settings.cameraSensitivity);
  }, [api, settings.cameraSensitivity]);

  useEffect(() => {
    api?.setGazeSensitivity(settings.gazeSensitivity);
  }, [api, settings.gazeSensitivity]);

  useEffect(() => {
    document.body.dataset.phase = bridge.phase;
    document.body.dataset.bridge = bridge.connected ? "online" : "offline";
  }, [bridge.phase, bridge.connected]);

  useEffect(() => {
    const log = logRef.current;
    if (log) log.scrollTop = log.scrollHeight;
  }, [bridge.turns.length, bridge.phase]);

  const update = useCallback((patch: Partial<Settings>): void => {
    setSettings((prev) => {
      const next = normalizeSettings({ ...prev, ...patch });
      saveSettings(next);
      return next;
    });
  }, []);

  const submit = useCallback((): void => {
    if (busy) return;
    const text = draft.trim();
    if (!text) return;
    bridge.send(text);
    setDraft("");
  }, [bridge, busy, draft]);

  const exportTurns = useCallback((): void => {
    const payload = { exportedAt: new Date().toISOString(), turns: bridge.turns.slice(-MAX_TURNS) };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
    const href = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = href;
    link.download = "mas3d-transcript.json";
    link.click();
    // 同步 revoke 会在部分浏览器里取消正在进行的下载，交给下一轮事件循环
    window.setTimeout(() => URL.revokeObjectURL(href), 0);
    setNotice("已导出最近 " + payload.turns.length + " 条记录（不含令牌）。");
  }, [bridge.turns]);

  const doClear = useCallback((): void => {
    voice.stop();
    bridge.reset();
    setConfirmClear(false);
    setNotice(null);
  }, [bridge, voice]);

  return (
    <section className="chat" aria-label="与 Monika 的对话" data-phase={bridge.phase}>
      {bridge.offline && (
        <div className="chat-offline" role="status">
          <span>桥接服务没有响应，先由本地兜底回话。</span>
          <button type="button" onClick={bridge.reconnect}>
            重新连接
          </button>
        </div>
      )}

      <header className="chat-head">
        <span className={bridge.connected ? "dot on" : "dot off"} aria-hidden="true" />
        <span className="chat-status">{PHASE_LABEL[bridge.phase] ?? bridge.phase}</span>
        <span className="chat-count">
          {bridge.turns.filter((turn) => turn.role !== "backend").length}/{MAX_TURNS}
        </span>
        <div className="chat-actions">
          <button type="button" aria-pressed={showSettings} onClick={() => setShowSettings((v) => !v)}>
            设置
          </button>
          <button type="button" onClick={exportTurns} disabled={bridge.turns.length === 0}>
            导出
          </button>
          {confirmClear ? (
            <>
              <button type="button" className="danger" onClick={doClear}>
                确认清空
              </button>
              <button type="button" onClick={() => setConfirmClear(false)}>
                取消
              </button>
            </>
          ) : (
            <button type="button" onClick={() => setConfirmClear(true)} disabled={bridge.turns.length === 0}>
              清空
            </button>
          )}
        </div>
      </header>

      <div className="chat-log" ref={logRef} role="log" aria-live="polite">
        {bridge.turns.length === 0 && <p className="chat-empty">她坐在课桌前等你先开口。</p>}
        {bridge.turns.map((turn) => (
          <div key={turn.id} className={"turn " + turn.role}>
            <span className="turn-role">{ROLE_LABEL[turn.role]}</span>
            <p className="turn-text">{turn.text}</p>
          </div>
        ))}
        {busy && <div className="turn monika pending">…</div>}
      </div>

      {bridge.error && (
        <div className="chat-error" role="alert">
          <span>{bridge.error.message}</span>
          <button type="button" onClick={bridge.retry}>
            重试
          </button>
        </div>
      )}

      {notice && (
        <div className="chat-notice" role="status">
          <span>{notice}</span>
          <button type="button" onClick={() => setNotice(null)}>
            知道了
          </button>
        </div>
      )}

      {showSettings && (
        <div className="chat-settings">
          <label>
            <span>音量</span>
            <input
              type="range"
              min={0}
              max={1}
              step={0.05}
              value={settings.volume}
              onChange={(event) => update({ volume: Number(event.target.value) })}
            />
          </label>
          <label>
            <span>视线灵敏度</span>
            <input
              type="range"
              min={0.5}
              max={1.5}
              step={0.05}
              value={settings.gazeSensitivity}
              onChange={(event) => update({ gazeSensitivity: Number(event.target.value) })}
            />
          </label>
          <label>
            <span>动作增强</span>
            <input
              type="range"
              min={0}
              max={1.5}
              step={0.05}
              value={settings.gestureBoost}
              onChange={(event) => update({ gestureBoost: Number(event.target.value) })}
            />
          </label>
          <label>
            <span>相机灵敏度</span>
            <input
              type="range"
              min={0.4}
              max={2}
              step={0.05}
              value={settings.cameraSensitivity}
              onChange={(event) => update({ cameraSensitivity: Number(event.target.value) })}
            />
          </label>
          <label className="chat-toggle">
            <input
              type="checkbox"
              checked={settings.subtitlesOnly}
              onChange={(event) => update({ subtitlesOnly: event.target.checked })}
            />
            <span>仅字幕（不合成语音）</span>
          </label>
        </div>
      )}

      <form
        className="chat-input"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <textarea
          value={draft}
          maxLength={MAX_INPUT_CHARS}
          rows={2}
          placeholder={busy ? "等她说完…" : "和她说点什么（Enter 发送，Shift+Enter 换行）"}
          onChange={(event) => setDraft(event.target.value.slice(0, MAX_INPUT_CHARS))}
          onKeyDown={(event) => {
            if (event.key === "Enter" && !event.shiftKey) {
              event.preventDefault();
              submit();
            } else if (event.key === "Escape") {
              event.currentTarget.blur();
            }
          }}
        />
        <div className="chat-meta">
          <span className={draft.length >= MAX_INPUT_CHARS ? "chat-len max" : "chat-len"}>
            {draft.length}/{MAX_INPUT_CHARS}
          </span>
          <button type="submit" disabled={busy || draft.trim().length === 0}>
            发送
          </button>
        </div>
      </form>
    </section>
  );
}
