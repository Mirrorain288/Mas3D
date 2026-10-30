import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import type { Emotion, Gaze, Gesture, MotionPlan } from "@mas3d/protocol";
import { createCameraRig, type CameraRig } from "./cameraRig";
import { buildClassroom, type ClassroomBuild, type ClassroomKey } from "./classroom";
import { createLights, type SceneLights } from "./lights";
import { applySkybox, disposeSkybox, fallbackSky, SKY_1K_URL, SKY_2K_URL, type SkyboxHandle } from "./skybox";
import { nextQuality, qualitySettings, type Quality } from "./qualityLadder";
import { MonikaAvatar, type AvatarApi, type AvatarHost, type SeatPose } from "../avatar/MonikaAvatar";

export interface SceneApi {
  setEmotion(emotion: Emotion, intensity: number): void;
  setGaze(mode: Gaze): void;
  playGesture(gesture: Gesture, intensity: number): boolean;
  /** 把 Tier2 动作计划的持续时长告诉角色，期间挡下 Tier1 的手部动作。 */
  holdArmsFor(plan: MotionPlan): void;
  /** 设置面板的相机灵敏度（0.4–2），立即生效。 */
  setCameraSensitivity(mult: number): void;
  /** 设置面板的视线灵敏度（0.5–1.5），立即生效。 */
  setGazeSensitivity(mult: number): void;
}

interface SceneProps {
  onReady?: (api: SceneApi) => void;
}

const clamp01 = (value: number): number => Math.min(1, Math.max(0, value));

/**
 * 场景宿主：负责 WebGL 生命周期、资产装配、帧率自适应，并把每帧回调
 * 通过 AvatarHost 交给角色模块。对外只暴露 SceneApi 三个方法。
 */
export function Scene({ onReady }: SceneProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const onReadyRef = useRef(onReady);
  onReadyRef.current = onReady;
  const avatarRef = useRef<AvatarApi | null>(null);

  const [progress, setProgress] = useState(0);
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [degraded, setDegraded] = useState<ClassroomKey[]>([]);
  const [skyFailed, setSkyFailed] = useState(false);
  const [avatarState, setAvatarState] = useState<"loading" | "ready" | "placeholder">("loading");
  const [fatal, setFatal] = useState<string | null>(null);
  const [host, setHost] = useState<AvatarHost | null>(null);
  const [seat, setSeat] = useState<SeatPose | null>(null);

  useEffect(() => {
    const container = hostRef.current;
    if (!container) return;

    let disposed = false;
    let frame = 0;
    let classroom: ClassroomBuild | null = null;
    let sky: SkyboxHandle | null = null;
    let lights: SceneLights | null = null;
    let rig: CameraRig | null = null;

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 120);

    let renderer: THREE.WebGLRenderer;
    try {
      renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance" });
    } catch (error) {
      setFatal(error instanceof Error ? error.message : String(error));
      setStatus("error");
      return;
    }

    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.05;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFShadowMap;
    container.appendChild(renderer.domElement);

    // 每帧回调：角色与后续的对话动效都挂在这里，由同一个 rAF 驱动
    const ticks = new Set<(dtMs: number, nowMs: number) => void>();
    const avatarHost: AvatarHost = {
      scene,
      camera,
      addTick(fn) {
        ticks.add(fn);
        return () => {
          ticks.delete(fn);
        };
      },
    };
    setHost(avatarHost);

    let quality: Quality = "high";
    let appliedSky: 1024 | 2048 = 2048;

    const resize = (): void => {
      const width = container.clientWidth || window.innerWidth;
      const height = container.clientHeight || window.innerHeight;
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      renderer.setPixelRatio(Math.min(window.devicePixelRatio, qualitySettings(quality).pixelRatio));
      renderer.setSize(width, height, false);
    };

    const loadSky = async (size: 1024 | 2048): Promise<void> => {
      appliedSky = size;
      try {
        const next = await applySkybox(scene, size === 1024 ? SKY_1K_URL : SKY_2K_URL);
        if (disposed) {
          next.texture?.dispose();
          return;
        }
        const previous = sky;
        sky = next;
        if (previous && previous.url !== next.url) previous.texture?.dispose();
        setSkyFailed(false);
      } catch {
        if (disposed) return;
        fallbackSky(scene);
        setSkyFailed(true);
      }
    };

    const applyQuality = (next: Quality): void => {
      quality = next;
      const settings = qualitySettings(next);
      renderer.setPixelRatio(Math.min(window.devicePixelRatio, settings.pixelRatio));
      renderer.shadowMap.enabled = settings.shadows;
      lights?.setShadows(settings.shadows);
      classroom?.setShadows(settings.shadows);
      if (settings.skySize !== appliedSky) void loadSky(settings.skySize);
      resize();
    };

    lights = createLights();
    scene.add(lights.root);
    rig = createCameraRig(camera, renderer.domElement);
    resize();
    window.addEventListener("resize", resize);

    const api: SceneApi = {
      setEmotion(emotion: Emotion, intensity: number): void {
        avatarRef.current?.setEmotion(emotion, clamp01(intensity));
      },
      setGaze(mode: Gaze): void {
        avatarRef.current?.setGaze(mode);
      },
      playGesture(gesture: Gesture, intensity: number): boolean {
        return avatarRef.current?.playGesture(gesture, clamp01(intensity)) ?? false;
      },
      holdArmsFor(plan: MotionPlan): void {
        avatarRef.current?.holdArmsFor(plan);
      },
      setCameraSensitivity(mult: number): void {
        rig?.setSensitivity(mult);
      },
      setGazeSensitivity(mult: number): void {
        avatarRef.current?.setGazeSensitivity(mult);
      },
    };

    void (async () => {
      void loadSky(2048);
      try {
        classroom = await buildClassroom(undefined, (ratio) => {
          if (!disposed) setProgress(ratio);
        });
        if (disposed) {
          classroom.dispose();
          return;
        }
        scene.add(classroom.root);
        classroom.setShadows(qualitySettings(quality).shadows);
        setDegraded(classroom.degraded);
        setSeat({ position: classroom.seat.position.clone(), facingY: classroom.seat.facingY });
        if (import.meta.env.DEV) {
          (window as unknown as { __mas3dClassroom?: unknown }).__mas3dClassroom = classroom;
        }
        setProgress(1);
        setStatus("ready");
        onReadyRef.current?.(api);
      } catch (error) {
        if (disposed) return;
        setFatal(error instanceof Error ? error.message : String(error));
        setStatus("error");
      }
    })();

    let last = performance.now();
    let frames = 0;
    let windowStart = last;
    let sustainStart = 0;

    const tick = (now: number): void => {
      frame = requestAnimationFrame(tick);
      const delta = now - last;
      last = now;
      frames += 1;

      if (now - windowStart >= 1000) {
        const fps = (frames * 1000) / (now - windowStart);
        frames = 0;
        windowStart = now;

        if (fps < 45) {
          sustainStart = 0;
          const next = nextQuality(quality, fps);
          if (next !== quality) applyQuality(next);
        } else if (fps >= 55) {
          if (sustainStart === 0) sustainStart = now;
          else if (now - sustainStart >= 3000) {
            sustainStart = 0;
            const next = nextQuality(quality, fps);
            if (next !== quality) applyQuality(next);
          }
        } else {
          sustainStart = 0;
        }
      }

      rig?.update();
      for (const fn of ticks) {
        try {
          fn(delta, now);
        } catch (error) {
          ticks.delete(fn);
          console.error("[mas3d] 每帧回调出错，已移除该回调", error);
        }
      }
      renderer.render(scene, camera);
    };
    frame = requestAnimationFrame(tick);

    return () => {
      disposed = true;
      cancelAnimationFrame(frame);
      window.removeEventListener("resize", resize);
      ticks.clear();
      setHost(null);
      setSeat(null);
      rig?.dispose();
      classroom?.dispose();
      lights?.dispose();
      disposeSkybox(scene, sky);
      renderer.dispose();
      renderer.domElement.remove();
    };
  }, []);

  const avatarNote = avatarState === "placeholder" ? "角色没能加载，已用占位人形代替。" : "";
  const showBanner = status === "ready" && (degraded.length > 0 || skyFailed || avatarNote !== "");

  return (
    <div className="scene" ref={hostRef}>
      {host && seat && (
        <MonikaAvatar
          host={host}
          seat={seat}
          onApi={(api) => {
            avatarRef.current = api;
          }}
          onStatus={(next) => setAvatarState(next)}
        />
      )}

      {status === "loading" && (
        <div className="overlay">
          <p className="overlay-title">正在布置教室…</p>
          <div className="bar" role="progressbar" aria-valuenow={Math.round(progress * 100)} aria-valuemin={0} aria-valuemax={100}>
            <span style={{ width: `${Math.round(progress * 100)}%` }} />
          </div>
          <p className="overlay-hint">星空已就位，桌椅还在搬</p>
        </div>
      )}

      {showBanner && (
        <div className="banner" role="status">
          <span>
            {degraded.length > 0 ? `部分素材没能加载（${degraded.join(" / ")}），已用占位物代替。` : ""}
            {skyFailed ? "天空暂时是纯色。" : ""}
            {avatarNote}
          </span>
          <button type="button" onClick={() => window.location.reload()}>
            重试
          </button>
        </div>
      )}

      {status === "error" && (
        <div className="overlay overlay-error" role="alert">
          <p className="overlay-title">浏览器没能启动 3D 渲染</p>
          <p className="overlay-hint">{fatal ?? "未知错误"}</p>
          <button type="button" onClick={() => window.location.reload()}>
            重试
          </button>
        </div>
      )}
    </div>
  );
}
