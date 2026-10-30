import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import type { Emotion, Gesture } from "@mas3d/protocol";
import { createCameraRig, type CameraRig } from "./cameraRig";
import { buildClassroom, type ClassroomBuild, type ClassroomKey } from "./classroom";
import { createLights, type SceneLights } from "./lights";
import { applySkybox, disposeSkybox, fallbackSky, SKY_1K_URL, SKY_2K_URL, type SkyboxHandle } from "./skybox";
import { nextQuality, qualitySettings, type Quality } from "./qualityLadder";

export interface SceneApi {
  setEmotion(emotion: Emotion, intensity: number): void;
  playGesture(gesture: Gesture, intensity: number): void;
}

interface SceneProps {
  onReady?: (api: SceneApi) => void;
}

const clamp01 = (value: number): number => Math.min(1, Math.max(0, value));

/**
 * 场景宿主：负责 WebGL 生命周期、资产装配、帧率自适应。
 * 对外的唯一接口是 SceneApi —— 角色的表情与手势都从这里进，
 * 具体怎么落到骨头上由 Task 6/8 的模块决定，本任务只记录意图。
 */
export function Scene({ onReady }: SceneProps) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const onReadyRef = useRef(onReady);
  onReadyRef.current = onReady;

  const [progress, setProgress] = useState(0);
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [degraded, setDegraded] = useState<ClassroomKey[]>([]);
  const [skyFailed, setSkyFailed] = useState(false);
  const [fatal, setFatal] = useState<string | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

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
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    host.appendChild(renderer.domElement);

    let quality: Quality = "high";
    let appliedSky: 1024 | 2048 = 2048;

    const resize = (): void => {
      const width = host.clientWidth || window.innerWidth;
      const height = host.clientHeight || window.innerHeight;
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
        // 换档位时只释放旧贴图，scene 已经指向新贴图
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

    const cues = {
      emotion: "neutral" as Emotion,
      emotionIntensity: 0,
      gesture: null as Gesture | null,
      gestureIntensity: 0,
    };

    const api: SceneApi = {
      setEmotion(emotion: Emotion, intensity: number): void {
        cues.emotion = emotion;
        cues.emotionIntensity = clamp01(intensity);
      },
      playGesture(gesture: Gesture, intensity: number): void {
        cues.gesture = gesture;
        cues.gestureIntensity = clamp01(intensity);
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
      renderer.render(scene, camera);
      void delta;
    };
    frame = requestAnimationFrame(tick);

    return () => {
      disposed = true;
      cancelAnimationFrame(frame);
      window.removeEventListener("resize", resize);
      rig?.dispose();
      classroom?.dispose();
      lights?.dispose();
      disposeSkybox(scene, sky);
      renderer.dispose();
      renderer.domElement.remove();
    };
  }, []);

  const showBanner = status === "ready" && (degraded.length > 0 || skyFailed);

  return (
    <div className="scene" ref={hostRef}>
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
            部分素材没能加载
            {degraded.length > 0 ? `（${degraded.join(" / ")}）` : ""}
            {skyFailed ? "，天空暂时是纯色" : ""}
            ，已用占位物代替。
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
