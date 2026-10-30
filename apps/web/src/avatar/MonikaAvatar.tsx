import { useEffect, useRef } from "react";
import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { VRMLoaderPlugin, VRMUtils, type VRM, type VRMHumanBoneName } from "@pixiv/three-vrm";
import type { Emotion, Gaze, Gesture, MotionPlan } from "@mas3d/protocol";
import { Arbiter } from "@mas3d/motion";
import { FaceDriver, FALLBACK_FACE_MAP, resolveFaceMap, type MorphMesh } from "./faceDriver";
import { BLINK_CLOSE_MS, mulberry32, nextBlink } from "./idle";
import { gazeTarget } from "./gaze";
import { applyPose, armHoldEndMs, type PoseBones } from "./applyPose";

export const VRM_URL = "/assets/monika/Monika.vrm";

/** 场景宿主交给角色的最小契约。 */
export interface AvatarHost {
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  addTick(fn: (dtMs: number, nowMs: number) => void): () => void;
}

export interface SeatPose {
  /** x/z 是座位点，y 是 hips 骨骼该落的高度（由场景量出的坐面高度决定）。 */
  position: THREE.Vector3;
  facingY: number;
}

export interface AvatarApi {
  setEmotion(emotion: Emotion, intensity: number): void;
  setGaze(mode: Gaze): void;
  /** 返回是否被仲裁器接受（被 Tier2 占用手臂、或强度不足以抢占时返回 false）。 */
  playGesture(gesture: Gesture, intensity: number): boolean;
  /** Tier2（HAST-SEC）动作用到了手臂：按计划的 delay+hold 占住手臂，期间挡下 Tier1 的手部动作。 */
  holdArmsFor(plan: MotionPlan): void;
  /** 设置里的「视线灵敏度」：缩放头/颈/眼的跟随幅度，1 = 默认。 */
  setGazeSensitivity(mult: number): void;
}

export interface MonikaAvatarProps {
  host: AvatarHost;
  seat: SeatPose;
  onApi?: (api: AvatarApi | null) => void;
  onStatus?: (status: "loading" | "ready" | "placeholder", detail?: string) => void;
}

/**
 * 坐姿常量：这个 VRM 没有动画片段，坐、呼吸、眨眼、视线全部程序生成。
 * 大腿抬到接近水平（-1.32 rad）、小腿弯回（+1.28 rad）、脚背压平，
 * 坐面高度由场景量出（SeatPose.position.y），水平位置按 hips 对齐座位点。
 */
export const SIT_POSE = {
  thigh: 1.32,
  knee: -1.28,
  foot: 0.06,
  spineLean: 0.05,
  armDown: 1.16,
  shoulderForward: 0.18,
  elbowBend: 0.32,
  legSplay: 0.07,
} as const;

const BREATH_PERIOD_MS = 4000;
const HEAD_SWAY_PERIOD_MS = 7000;
const BREATH_RAD = 0.012;
const HEAD_SWAY_RAD = 0.01;
const HEAD_FOLLOW = 0.35;
const NECK_FOLLOW = 0.15;
const EYE_FOLLOW = 0.55;

function setNodeRotation(
  vrm: VRM,
  name: VRMHumanBoneName,
  rotation: { x?: number; y?: number; z?: number },
): void {
  const node = vrm.humanoid.getNormalizedBoneNode(name);
  if (!node) return;
  node.rotation.set(rotation.x ?? node.rotation.x, rotation.y ?? node.rotation.y, rotation.z ?? node.rotation.z);
}

function applySitPose(vrm: VRM): void {
  setNodeRotation(vrm, "leftUpperLeg", { x: SIT_POSE.thigh, z: -SIT_POSE.legSplay });
  setNodeRotation(vrm, "rightUpperLeg", { x: SIT_POSE.thigh, z: SIT_POSE.legSplay });
  setNodeRotation(vrm, "leftLowerLeg", { x: SIT_POSE.knee });
  setNodeRotation(vrm, "rightLowerLeg", { x: SIT_POSE.knee });
  setNodeRotation(vrm, "leftFoot", { x: SIT_POSE.foot });
  setNodeRotation(vrm, "rightFoot", { x: SIT_POSE.foot });
  setNodeRotation(vrm, "spine", { x: SIT_POSE.spineLean });
  setNodeRotation(vrm, "leftUpperArm", { z: SIT_POSE.armDown, y: -SIT_POSE.shoulderForward });
  setNodeRotation(vrm, "rightUpperArm", { z: -SIT_POSE.armDown, y: SIT_POSE.shoulderForward });
  setNodeRotation(vrm, "leftLowerArm", { y: -SIT_POSE.elbowBend, z: -0.12 });
  setNodeRotation(vrm, "rightLowerArm", { y: SIT_POSE.elbowBend, z: 0.12 });
}

/**
 * 落座定位：SkinnedMesh 的包围盒不跟随骨骼，因此不能用 bbox 贴地，
 * 改为把 hips 骨骼抬到场景量出的座面高度，脚靠小腿长度自然垂到地面附近。
 */
function placeSeated(vrm: VRM, seat: SeatPose): void {
  vrm.scene.position.set(seat.position.x, 0, seat.position.z);
  vrm.scene.rotation.y += seat.facingY;
  vrm.scene.updateMatrixWorld(true);
  const hips = vrm.humanoid.getNormalizedBoneNode("hips");
  if (!hips) return;
  const world = new THREE.Vector3();
  hips.getWorldPosition(world);
  // hips 在模型内并不在原点：水平方向按 hips 对齐座位点，垂直方向对齐座面高度
  vrm.scene.position.x += seat.position.x - world.x;
  vrm.scene.position.z += seat.position.z - world.z;
  vrm.scene.position.y += seat.position.y - world.y;
  vrm.scene.updateMatrixWorld(true);
}

function makePlaceholder(): THREE.Mesh {
  const geometry = new THREE.CapsuleGeometry(0.22, 0.8, 6, 12);
  const material = new THREE.MeshStandardMaterial({ color: 0x3d4a6b, roughness: 0.8 });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.y = 0.72;
  mesh.castShadow = true;
  return mesh;
}

/**
 * Monika 角色宿主。渲染为 null —— 所有 3D 对象直接挂到场景里，
 * 由 Scene 的渲染循环通过 host.addTick 驱动。
 */
export function MonikaAvatar({ host, seat, onApi, onStatus }: MonikaAvatarProps): null {
  const apiRef = useRef<AvatarApi | null>(null);
  const onApiRef = useRef(onApi);
  const onStatusRef = useRef(onStatus);
  onApiRef.current = onApi;
  onStatusRef.current = onStatus;

  useEffect(() => {
    let disposed = false;
    let detachTick: (() => void) | null = null;
    let driver: FaceDriver | null = null;
    let vrm: VRM | null = null;
    let placeholder: THREE.Mesh | null = null;

    const cleanup = (): void => {
      detachTick?.();
      detachTick = null;
      driver?.dispose();
      driver = null;
      if (vrm) {
        host.scene.remove(vrm.scene);
        VRMUtils.deepDispose(vrm.scene);
        vrm = null;
      }
      if (placeholder) {
        host.scene.remove(placeholder);
        placeholder.geometry.dispose();
        (placeholder.material as THREE.Material).dispose();
        placeholder = null;
      }
      apiRef.current = null;
      onApiRef.current?.(null);
    };

    onStatusRef.current?.("loading");
    const loader = new GLTFLoader();
    loader.register((parser) => new VRMLoaderPlugin(parser));

    loader.load(
      VRM_URL,
      (gltf) => {
        if (disposed) return;
        const loaded = gltf.userData.vrm as VRM | undefined;
        if (!loaded) {
          onStatusRef.current?.("placeholder", "VRM 插件没有返回角色");
          placeholder = makePlaceholder();
          placeholder.position.set(seat.position.x, 0, seat.position.z);
          host.scene.add(placeholder);
          return;
        }
        vrm = loaded;

        // VRM 0.x 是 -Z 朝向，three-vrm 提供 180° 修正
        const metaVersion = (loaded.meta as { metaVersion?: string } | undefined)?.metaVersion;
        if (metaVersion === "0") VRMUtils.rotateVRM0(loaded);

        loaded.scene.traverse((object) => {
          const mesh = object as THREE.Mesh;
          if (mesh.isMesh) {
            mesh.castShadow = true;
            mesh.receiveShadow = true;
            mesh.frustumCulled = false;
          }
        });

        applySitPose(loaded);
        placeSeated(loaded, seat);
        host.scene.add(loaded.scene);

        const morphMeshes: MorphMesh[] = [];
        loaded.scene.traverse((object) => {
          const mesh = object as THREE.Mesh;
          if (mesh.isMesh && mesh.morphTargetInfluences && mesh.morphTargetInfluences.length > 0) {
            morphMeshes.push(mesh as unknown as MorphMesh);
          }
        });
        driver = new FaceDriver(morphMeshes, resolveFaceMap((gltf.parser as unknown as { json?: unknown }).json, FALLBACK_FACE_MAP));

        const head = loaded.humanoid.getNormalizedBoneNode("head");
        const neck = loaded.humanoid.getNormalizedBoneNode("neck");
        const chest = loaded.humanoid.getNormalizedBoneNode("upperChest") ?? loaded.humanoid.getNormalizedBoneNode("chest");
        const spine = loaded.humanoid.getNormalizedBoneNode("spine");
        const leftEye = loaded.humanoid.getNormalizedBoneNode("leftEye");
        const rightEye = loaded.humanoid.getNormalizedBoneNode("rightEye");
        const leftUpperArm = loaded.humanoid.getNormalizedBoneNode("leftUpperArm");
        const rightUpperArm = loaded.humanoid.getNormalizedBoneNode("rightUpperArm");

        // Tier1 手势仲裁：时间由渲染循环注入，可确定性测试
        const arbiter = new Arbiter();
        const idleBone = { rotation: { x: 0, y: 0, z: 0 } };
        const bones: PoseBones = {
          head: head ?? idleBone,
          neck: neck ?? idleBone,
          spine: spine ?? idleBone,
          upperArmL: leftUpperArm ?? idleBone,
          upperArmR: rightUpperArm ?? idleBone,
        };
        // applyPose 是叠加式的，手臂每帧必须先回到坐姿基准，否则会累加漂移
        const armBaseZ = { l: leftUpperArm?.rotation.z ?? 0, r: rightUpperArm?.rotation.z ?? 0 };
        let armsOwnedUntilMs = 0;
        const base = {
          chestX: chest?.rotation.x ?? 0,
          spineX: spine?.rotation.x ?? 0,
          headX: head?.rotation.x ?? 0,
          headY: head?.rotation.y ?? 0,
          neckX: neck?.rotation.x ?? 0,
        };

        let elapsed = 0;
        const rnd = mulberry32(Math.floor(seat.position.x * 1000) + 1337);
        let blinkAt = nextBlink(performance.now(), rnd);
        let blinkStart = 0;
        let blinkWeight = 0;
        const stats = { blinks: 0, lastBlinkAt: 0 };
        let gazeMode: Gaze = "player";
        let gazeSensitivity = 1;

        const headWorld = new THREE.Vector3();

        detachTick = host.addTick((dtMs, nowMs) => {
          elapsed += dtMs;
          const breath = Math.sin((elapsed / BREATH_PERIOD_MS) * Math.PI * 2) * BREATH_RAD;
          const sway = Math.sin((elapsed / HEAD_SWAY_PERIOD_MS) * Math.PI * 2) * HEAD_SWAY_RAD;
          if (chest) chest.rotation.x = base.chestX + breath;
          if (spine) spine.rotation.x = base.spineX + breath * 0.6;

          let gaze = { yaw: 0, pitch: 0 };
          if (head) {
            head.getWorldPosition(headWorld);
            gaze = gazeTarget(host.camera.position, headWorld, gazeMode);
            const followPitch = gaze.pitch * HEAD_FOLLOW * gazeSensitivity;
            const followYaw = gaze.yaw * HEAD_FOLLOW * gazeSensitivity;
            head.rotation.set(base.headX + followPitch + breath * 0.3, base.headY + followYaw + sway, 0);
          }
          if (neck) neck.rotation.x = base.neckX + gaze.pitch * NECK_FOLLOW * gazeSensitivity;
          const eyePitch = gaze.pitch * EYE_FOLLOW * gazeSensitivity;
          const eyeYaw = gaze.yaw * EYE_FOLLOW * gazeSensitivity;
          if (leftEye) leftEye.rotation.set(eyePitch, eyeYaw, 0);
          if (rightEye) rightEye.rotation.set(eyePitch, eyeYaw, 0);

          if (leftUpperArm) leftUpperArm.rotation.z = armBaseZ.l;
          if (rightUpperArm) rightUpperArm.rotation.z = armBaseZ.r;
          if (armsOwnedUntilMs > 0 && nowMs >= armsOwnedUntilMs) {
            armsOwnedUntilMs = 0;
            arbiter.setArmOwnedByTier2(false, nowMs);
          }
          const gestureSample = arbiter.sample(nowMs);
          if (gestureSample.gesture !== "none") applyPose(bones, gestureSample.delta, 1);

          if (nowMs >= blinkAt) {
            blinkStart = nowMs;
            blinkAt = nextBlink(nowMs, rnd);
            stats.blinks += 1;
            stats.lastBlinkAt = nowMs;
          }
          const phase = (nowMs - blinkStart) / BLINK_CLOSE_MS;
          blinkWeight = phase >= 0 && phase < 1 ? Math.sin(Math.PI * phase) : 0;
          driver?.setBlink(blinkWeight);
          driver?.update(dtMs);
          // 归一化骨骼与 spring bone 都要靠 vrm.update 才会落到原始骨架上
          loaded.update(dtMs / 1000);
        });

        const api: AvatarApi = {
          setEmotion(emotion: Emotion, intensity: number): void {
            driver?.setEmotion(emotion, intensity);
          },
          setGaze(mode: Gaze): void {
            gazeMode = mode;
          },
          playGesture(gesture: Gesture, intensity: number): boolean {
            return arbiter.request(gesture, intensity, performance.now());
          },
          holdArmsFor(plan: MotionPlan): void {
            const holdMs = armHoldEndMs(plan.intents, plan.timing);
            if (holdMs <= 0) return;
            const now = performance.now();
            armsOwnedUntilMs = Math.max(armsOwnedUntilMs, now + holdMs);
            arbiter.setArmOwnedByTier2(true, now);
          },
          setGazeSensitivity(mult: number): void {
            gazeSensitivity = Math.min(1.5, Math.max(0.5, mult));
          },
        };
        apiRef.current = api;
        onApiRef.current?.(api);
        onStatusRef.current?.("ready");

        if (import.meta.env.DEV) {
          // 调试钩子：Task 7/8 的姿势标定靠它读骨骼世界坐标
          (window as unknown as { __mas3dAvatar?: unknown }).__mas3dAvatar = {
            vrm: loaded,
            driver,
            api,
            arbiter,
            stats: () => ({ ...stats, blinkWeight, blinkAt, now: performance.now() }),
            pose(): Record<string, unknown> {
              const out: Record<string, unknown> = {};
              const names = ["hips", "head", "leftFoot", "rightFoot", "leftUpperLeg", "leftLowerLeg", "leftUpperArm", "leftHand"] as const;
              const world = new THREE.Vector3();
              for (const name of names) {
                const node = loaded.humanoid.getNormalizedBoneNode(name);
                if (!node) continue;
                node.getWorldPosition(world);
                out[name] = {
                  pos: [Number(world.x.toFixed(3)), Number(world.y.toFixed(3)), Number(world.z.toFixed(3))],
                  rot: [Number(node.rotation.x.toFixed(2)), Number(node.rotation.y.toFixed(2)), Number(node.rotation.z.toFixed(2))],
                };
              }
              const box = new THREE.Box3().setFromObject(loaded.scene);
              out.box = {
                min: [Number(box.min.x.toFixed(2)), Number(box.min.y.toFixed(2)), Number(box.min.z.toFixed(2))],
                max: [Number(box.max.x.toFixed(2)), Number(box.max.y.toFixed(2)), Number(box.max.z.toFixed(2))],
              };
              return out;
            },
          };
        }
      },
      undefined,
      (error) => {
        if (disposed) return;
        onStatusRef.current?.("placeholder", error instanceof Error ? error.message : String(error));
        placeholder = makePlaceholder();
        placeholder.position.set(seat.position.x, 0, seat.position.z);
        host.scene.add(placeholder);
      },
    );

    return () => {
      disposed = true;
      cleanup();
    };
  }, [host, seat]);

  return null;
}
