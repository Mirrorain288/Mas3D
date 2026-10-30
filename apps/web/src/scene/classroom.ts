import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";

/**
 * 教室道具全部取自 Styloo 教室素材包，按「原生尺寸」摆放（GLB 已是真实米制，
 * 例如椅子 0.87 m、黑板 4.55 m），只在需要时给一个显式缩放系数。
 */
export const CLASSROOM_ASSETS = {
  seat: "/assets/classroom/seat.glb",
  board: "/assets/classroom/board.glb",
  lectern: "/assets/classroom/lectern.glb",
  lamp: "/assets/classroom/lamp.glb",
} as const;

export type ClassroomKey = keyof typeof CLASSROOM_ASSETS;

export interface ClassroomBuild {
  root: THREE.Group;
  /** 加载失败、改用占位方块的槽位 */
  degraded: ClassroomKey[];
  /** Monika 的座位锚点（世界坐标，y 为地面高度）：Task 6 用它落座 */
  seat: { position: THREE.Vector3; facingY: number };
  setShadows(enabled: boolean): void;
  dispose(): void;
}

interface Slot {
  key: ClassroomKey;
  /** 显式缩放系数（1 = 原生尺寸） */
  scale: number;
  /** 水平位置；y 由 bottomTo 决定 */
  position: [number, number];
  /** 底面贴到这个高度（米） */
  bottomTo: number;
  rotationY: number;
  /** 占位方块：宽 / 高 / 深 / 颜色 */
  fallback: [number, number, number, number];
}

const SLOTS: readonly Slot[] = [
  // 课桌椅一体的座位：Monika 与玩家共用的一张桌子，正面朝相机（+z）
  { key: "seat", scale: 1, position: [0, -0.5], bottomTo: 0, rotationY: 0, fallback: [1.3, 0.7, 0.9, 0x6b4a2f] },
  // 黑板贴着后舱壁，缩到 3 米宽，底边 1.05 m
  { key: "board", scale: 0.66, position: [0, -3.5], bottomTo: 1.05, rotationY: Math.PI, fallback: [3, 0.88, 0.12, 0x16211b] },
  // 讲台（素材包里的 desk 其实是讲桌）放在左后侧，稍微斜向相机
  { key: "lectern", scale: 0.92, position: [-2.75, -2.35], bottomTo: 0, rotationY: 0.5, fallback: [0.88, 1.54, 0.81, 0x5c3b24] },
  // 吊灯：吊在座位正上方
  { key: "lamp", scale: 1, position: [0, -0.15], bottomTo: 2.86, rotationY: 0, fallback: [1.27, 0.33, 0.4, 0xfff2cc] },
];

/** 把 GLB 摆到目标位置：水平居中、底面贴合 bottomTo、按 rotationY 转向。 */
function placeAt(object: THREE.Object3D, slot: Slot, scale: number): void {
  object.scale.multiplyScalar(scale);
  const box = new THREE.Box3().setFromObject(object);
  const center = new THREE.Vector3();
  box.getCenter(center);
  object.position.x += slot.position[0] - center.x;
  object.position.z += slot.position[1] - center.z;
  object.position.y += slot.bottomTo - box.min.y;
  object.rotation.y += slot.rotationY;
  // 旋转后再以旋转后的包围盒重新对齐，保证贴地与水平居中在旋转之后依然成立
  const after = new THREE.Box3().setFromObject(object);
  const afterCenter = new THREE.Vector3();
  after.getCenter(afterCenter);
  object.position.x += slot.position[0] - afterCenter.x;
  object.position.z += slot.position[1] - afterCenter.z;
  object.position.y += slot.bottomTo - after.min.y;
}

function makePlaceholder(slot: Slot): THREE.Mesh {
  const [w, h, d, color] = slot.fallback;
  const mesh = new THREE.Mesh(
    new THREE.BoxGeometry(w, h, d),
    new THREE.MeshStandardMaterial({ color, roughness: 0.85, metalness: 0.05 }),
  );
  mesh.name = `placeholder-${slot.key}`;
  mesh.castShadow = true;
  mesh.receiveShadow = true;
  return mesh;
}

/** 地板：WoodFloor070 颜色/法线/粗糙度三张 1K WebP，重复 6×6。 */
async function buildFloor(): Promise<THREE.Mesh> {
  const geometry = new THREE.CircleGeometry(3.6, 64);
  const material = new THREE.MeshStandardMaterial({ color: 0x2a2f3a, roughness: 0.9, metalness: 0.02 });
  const load = async (url: string, srgb: boolean): Promise<THREE.Texture | null> => {
    try {
      const texture = await new THREE.TextureLoader().loadAsync(url);
      if (srgb) texture.colorSpace = THREE.SRGBColorSpace;
      texture.wrapS = THREE.RepeatWrapping;
      texture.wrapT = THREE.RepeatWrapping;
      texture.repeat.set(6, 6);
      texture.anisotropy = 4;
      return texture;
    } catch {
      return null;
    }
  };
  const [color, normal, roughness] = await Promise.all([
    load("/assets/pbr/wood-floor-color@1k.webp", true),
    load("/assets/pbr/wood-floor-normal@1k.webp", false),
    load("/assets/pbr/wood-floor-roughness@1k.webp", false),
  ]);
  if (color) {
    material.map = color;
    material.color.setHex(0xffffff);
  }
  if (normal) {
    normal.colorSpace = THREE.NoColorSpace;
    material.normalMap = normal;
  }
  if (roughness) material.roughnessMap = roughness;
  if (color || normal || roughness) material.needsUpdate = true;

  const floor = new THREE.Mesh(geometry, material);
  floor.name = "floor";
  floor.rotation.x = -Math.PI / 2;
  floor.receiveShadow = true;
  return floor;
}

/**
 * 舱壁：后墙在黑板上方开一扇横向星窗（x∈[-1.7,1.7], y∈[1.95,3.15]），
 * 左右侧墙 + 天花板把视线收住，相机再怎么转也看不到教室背面。
 */
function buildShell(): { group: THREE.Group; disposables: Array<THREE.BufferGeometry | THREE.Material> } {
  const group = new THREE.Group();
  group.name = "shell";
  const disposables: Array<THREE.BufferGeometry | THREE.Material> = [];

  const wallMaterial = new THREE.MeshStandardMaterial({
    color: 0x232a36,
    roughness: 0.78,
    metalness: 0.18,
    side: THREE.DoubleSide,
  });
  const stripMaterial = new THREE.MeshStandardMaterial({
    color: 0x8fc4ff,
    emissive: new THREE.Color(0x3f7fd8),
    emissiveIntensity: 1.8,
    roughness: 0.4,
  });
  disposables.push(wallMaterial, stripMaterial);

  const addPanel = (w: number, h: number, d: number, x: number, y: number, z: number): void => {
    const geometry = new THREE.BoxGeometry(w, h, d);
    disposables.push(geometry);
    const panel = new THREE.Mesh(geometry, wallMaterial);
    panel.position.set(x, y, z);
    panel.receiveShadow = true;
    group.add(panel);
  };

  const backZ = -3.62;
  const roomHalf = 3.62;
  const winX = 1.7;
  const winY0 = 1.95;
  const winY1 = 3.15;
  const wallH = 3.3;
  const thickness = 0.16;

  // 后墙：围绕星窗拼四块板
  const sideW = roomHalf - winX;
  addPanel(sideW, wallH, thickness, -(winX + sideW / 2), wallH / 2, backZ);
  addPanel(sideW, wallH, thickness, winX + sideW / 2, wallH / 2, backZ);
  addPanel(winX * 2, winY0, thickness, 0, winY0 / 2, backZ);
  addPanel(winX * 2, wallH - winY1, thickness, 0, (wallH + winY1) / 2, backZ);

  // 侧墙：左右各开一条竖向星缝（z∈[-2.4,-1.2], y∈[1.3,2.9]），转头时也能看见宇宙
  const slitZ0 = -2.4;
  const slitZ1 = -1.2;
  const slitY0 = 1.3;
  const slitY1 = 2.9;
  const wallZ0 = -3.6;
  const wallZ1 = 1.2;
  for (const side of [-1, 1]) {
    const x = side * roomHalf;
    // 星缝前后的整段墙
    addPanel(thickness, wallH, slitZ0 - wallZ0, x, wallH / 2, (wallZ0 + slitZ0) / 2);
    addPanel(thickness, wallH, wallZ1 - slitZ1, x, wallH / 2, (slitZ1 + wallZ1) / 2);
    // 星缝上下
    addPanel(thickness, slitY0, slitZ1 - slitZ0, x, slitY0 / 2, (slitZ0 + slitZ1) / 2);
    addPanel(thickness, wallH - slitY1, slitZ1 - slitZ0, x, (wallH + slitY1) / 2, (slitZ0 + slitZ1) / 2);
  }

  // 天花板
  const ceilingGeometry = new THREE.PlaneGeometry(7.24, 7.24);
  disposables.push(ceilingGeometry);
  const ceiling = new THREE.Mesh(ceilingGeometry, wallMaterial);
  ceiling.rotation.x = Math.PI / 2;
  ceiling.position.set(0, wallH, -0.6);
  group.add(ceiling);

  // 星窗窗框：一圈窄条，让缺口看起来是被加工过的舷窗而不是断层
  const frameMaterial = new THREE.MeshStandardMaterial({ color: 0x2a3346, roughness: 0.62, metalness: 0.25 });
  disposables.push(frameMaterial);
  const frameBar = (w: number, h: number, x: number, y: number): void => {
    const geometry = new THREE.BoxGeometry(w, h, 0.1);
    disposables.push(geometry);
    const bar = new THREE.Mesh(geometry, frameMaterial);
    bar.position.set(x, y, backZ + 0.05);
    group.add(bar);
  };
  const frameT = 0.08;
  frameBar(winX * 2 + frameT * 2, frameT, 0, winY1);
  frameBar(winX * 2 + frameT * 2, frameT, 0, winY0);
  frameBar(frameT, winY1 - winY0, -winX, (winY0 + winY1) / 2);
  frameBar(frameT, winY1 - winY0, winX, (winY0 + winY1) / 2);

  // 灯带
  const stripGeometry = new THREE.BoxGeometry(0.06, 0.06, 6.4);
  disposables.push(stripGeometry);
  for (const x of [-3.5, 3.5]) {
    const strip = new THREE.Mesh(stripGeometry, stripMaterial);
    strip.position.set(x, 0.08, -1.2);
    group.add(strip);
  }
  const backStripGeometry = new THREE.BoxGeometry(6.9, 0.06, 0.06);
  disposables.push(backStripGeometry);
  const backStrip = new THREE.Mesh(backStripGeometry, stripMaterial);
  backStrip.position.set(0, 0.08, backZ + 0.12);
  group.add(backStrip);

  return { group, disposables };
}

/**
 * 装配单个座位区：课桌椅、黑板、讲台、吊灯 + 地板与舱壁。
 * 任何一个 GLB 加载失败都只影响自己的槽位（占位方块 + degraded 记录），
 * 不会让整个场景白屏。
 */
export async function buildClassroom(
  loader: GLTFLoader = new GLTFLoader(),
  onProgress?: (ratio: number) => void,
): Promise<ClassroomBuild> {
  const root = new THREE.Group();
  root.name = "classroom";
  const degraded: ClassroomKey[] = [];
  const disposables: Array<THREE.BufferGeometry | THREE.Material> = [];

  const shell = buildShell();
  root.add(shell.group);
  disposables.push(...shell.disposables);

  const floor = await buildFloor();
  root.add(floor);
  disposables.push(floor.geometry, floor.material as THREE.Material);

  let settled = 0;
  const total = SLOTS.length;
  const results = await Promise.all(
    SLOTS.map(async (slot) => {
      try {
        const gltf = await loader.loadAsync(CLASSROOM_ASSETS[slot.key]);
        return { slot, object: gltf.scene as THREE.Object3D | null, error: false };
      } catch {
        return { slot, object: null, error: true };
      } finally {
        settled += 1;
        onProgress?.(settled / total);
      }
    }),
  );

  let seatObject: THREE.Object3D | null = null;
  for (const { slot, object, error } of results) {
    const node: THREE.Object3D = error || !object ? makePlaceholder(slot) : object;
    if (error) degraded.push(slot.key);
    placeAt(node, slot, slot.scale);
    node.name = slot.key;
    node.traverse((child) => {
      if ((child as THREE.Mesh).isMesh) {
        child.castShadow = true;
        child.receiveShadow = true;
        const material = (child as THREE.Mesh).material;
        if (Array.isArray(material)) disposables.push(...material);
        else if (material) disposables.push(material);
      }
    });
    if (slot.key === "seat") seatObject = node;
    root.add(node);
  }

  const seatPosition = new THREE.Vector3(0, 0, 0);
  if (seatObject) {
    const box = new THREE.Box3().setFromObject(seatObject);
    box.getCenter(seatPosition);
    seatPosition.y = 0;
  }

  return {
    root,
    degraded,
    seat: { position: seatPosition, facingY: 0 },
    setShadows(enabled: boolean): void {
      root.traverse((child) => {
        if ((child as THREE.Mesh).isMesh) child.castShadow = enabled;
      });
    },
    dispose(): void {
      root.traverse((child) => {
        const mesh = child as THREE.Mesh;
        if (mesh.isMesh) mesh.geometry.dispose();
      });
      for (const item of disposables) item.dispose();
      root.clear();
    },
  };
}
