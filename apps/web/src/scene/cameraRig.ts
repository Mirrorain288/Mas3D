import * as THREE from "three";

/** 座位弧：玩家只能在这段范围内左右转，越过就贴边（不是自由环绕）。 */
export const YAW_LIMIT_DEG = 45;
export const PITCH_MIN_DEG = -10;
export const PITCH_MAX_DEG = 25;
export const DISTANCE_MIN = 1.6;
export const DISTANCE_MAX = 3.2;

export interface Orbit {
  /** 水平角，0 = 正对座位，正值向玩家右手边转 */
  yaw: number;
  /** 俯仰，正值抬高视线 */
  pitch: number;
  distance: number;
}

const clamp = (value: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, value));

/** 纯函数：把任意输入夹到座位弧内，供渲染循环与测试共用。 */
export function clampOrbit(yaw: number, pitch: number, distance: number): Orbit {
  return {
    yaw: clamp(yaw, -YAW_LIMIT_DEG, YAW_LIMIT_DEG),
    pitch: clamp(pitch, PITCH_MIN_DEG, PITCH_MAX_DEG),
    distance: clamp(distance, DISTANCE_MIN, DISTANCE_MAX),
  };
}

export interface CameraRig {
  readonly orbit: Orbit;
  /** 每帧调用：把 orbit 写进相机位置与朝向 */
  update(): void;
  setTarget(target: THREE.Vector3): void;
  dispose(): void;
}

const DEFAULT_TARGET = new THREE.Vector3(0, 1.32, 0);

/**
 * 受限轨道相机：拖动改 yaw/pitch，滚轮改 distance，全部经过 clampOrbit。
 * 没有阻尼惯性 —— 这是刻意选择，座位视角要「稳」，晃动反而晕。
 */
export function createCameraRig(
  camera: THREE.PerspectiveCamera,
  dom: HTMLElement,
  target: THREE.Vector3 = DEFAULT_TARGET.clone(),
): CameraRig {
  let orbit: Orbit = { yaw: 0, pitch: 4, distance: 2.75 };
  let pointerId: number | null = null;
  let lastX = 0;
  let lastY = 0;

  const onPointerDown = (event: PointerEvent): void => {
    if (pointerId !== null) return;
    pointerId = event.pointerId;
    lastX = event.clientX;
    lastY = event.clientY;
    dom.setPointerCapture(event.pointerId);
    dom.style.cursor = "grabbing";
  };

  const onPointerMove = (event: PointerEvent): void => {
    if (pointerId !== event.pointerId) return;
    const dx = event.clientX - lastX;
    const dy = event.clientY - lastY;
    lastX = event.clientX;
    lastY = event.clientY;
    orbit = clampOrbit(orbit.yaw - dx * 0.18, orbit.pitch + dy * 0.12, orbit.distance);
    rig.update();
  };

  const endDrag = (event: PointerEvent): void => {
    if (pointerId !== event.pointerId) return;
    if (dom.hasPointerCapture(event.pointerId)) dom.releasePointerCapture(event.pointerId);
    pointerId = null;
    dom.style.cursor = "grab";
  };

  const onWheel = (event: WheelEvent): void => {
    event.preventDefault();
    orbit = clampOrbit(orbit.yaw, orbit.pitch, orbit.distance + event.deltaY * 0.0012);
    rig.update();
  };

  const rig: CameraRig = {
    get orbit() {
      return orbit;
    },
    update(): void {
      const yaw = THREE.MathUtils.degToRad(orbit.yaw);
      const pitch = THREE.MathUtils.degToRad(orbit.pitch);
      const horizontal = Math.cos(pitch) * orbit.distance;
      camera.position.set(
        target.x + horizontal * Math.sin(yaw),
        target.y + Math.sin(pitch) * orbit.distance,
        target.z + horizontal * Math.cos(yaw),
      );
      camera.lookAt(target);
    },
    setTarget(next: THREE.Vector3): void {
      target.copy(next);
      rig.update();
    },
    dispose(): void {
      dom.removeEventListener("pointerdown", onPointerDown);
      dom.removeEventListener("pointermove", onPointerMove);
      dom.removeEventListener("pointerup", endDrag);
      dom.removeEventListener("pointercancel", endDrag);
      dom.removeEventListener("wheel", onWheel);
      dom.style.cursor = "";
    },
  };

  dom.style.cursor = "grab";
  dom.style.touchAction = "none";
  dom.addEventListener("pointerdown", onPointerDown);
  dom.addEventListener("pointermove", onPointerMove);
  dom.addEventListener("pointerup", endDrag);
  dom.addEventListener("pointercancel", endDrag);
  dom.addEventListener("wheel", onWheel, { passive: false });
  rig.update();

  return rig;
}
