import * as THREE from "three";

/**
 * A+ 修补：给导入的卧室（素材/mita 导出的 mita-room.glb）做「补装修」。
 *
 * 三处白模在原 .blend 里就丢了贴图（材质名还留着 Tex_xxxx，但节点里没有图片），
 * 屋顶内侧用的是一张近乎纯白的图，所以从室内看过去是几块死白的斜面。
 * 这些都不是几何问题，因此不需要回 Blender 重建：导入后按网格名换材质即可。
 *
 * 网格名来自 Blender 场景（见 .superpowers/mita-mesh-audit.json），改素材要同步这里。
 */
export type RoomMaterialKind = "ceiling" | "paintedWhite" | "mirror" | "keep";

const KIND_BY_MESH: Readonly<Record<string, RoomMaterialKind>> = {
  /** 斜屋顶（两块坡面 + 屋脊），原贴图接近纯白 */
  Mesh_0116out: "ceiling",
  /** 床右边那只小抽屉柜 */
  Mesh_0138out: "paintedWhite",
  /** 窗边墙上的四格置物架 */
  Mesh_0200out: "paintedWhite",
  /** 靠墙的立式穿衣镜（原材质是灰白塑料感） */
  Mesh_0014out: "mirror",
};

export function materialKindFor(meshName: string): RoomMaterialKind {
  return KIND_BY_MESH[meshName] ?? "keep";
}

export interface RoomDressing {
  materials: THREE.Material[];
  textures: THREE.Texture[];
  dispose(): void;
}

async function loadTriple(base: string, repeatX: number, repeatY: number): Promise<{
  map: THREE.Texture | null;
  normalMap: THREE.Texture | null;
  roughnessMap: THREE.Texture | null;
}> {
  const load = async (url: string, srgb: boolean): Promise<THREE.Texture | null> => {
    try {
      const texture = await new THREE.TextureLoader().loadAsync(url);
      texture.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
      texture.wrapS = THREE.RepeatWrapping;
      texture.wrapT = THREE.RepeatWrapping;
      texture.repeat.set(repeatX, repeatY);
      texture.anisotropy = 8;
      return texture;
    } catch {
      return null;
    }
  };
  const [map, normalMap, roughnessMap] = await Promise.all([
    load(`/assets/pbr/${base}-color@1k.webp`, true),
    load(`/assets/pbr/${base}-normal@1k.webp`, false),
    load(`/assets/pbr/${base}-roughness@1k.webp`, false),
  ]);
  return { map, normalMap, roughnessMap };
}

/**
 * 换掉三处白模与屋顶的材质。共用材质实例：导入的房间是逐网格一份材质，
 * 这里给同类网格共用一份，少几次材质切换。
 */
export async function dressImportedRoom(room: THREE.Object3D): Promise<RoomDressing> {
  const ceiling = await loadTriple("ceiling", 1, 1);
  const paintedWhite = new THREE.MeshStandardMaterial({
    color: 0xf1eae2,
    roughness: 0.62,
    metalness: 0.02,
  });
  // 没有环境贴图时高金属度 = 纯黑，所以这里走「浅灰玻璃」而不是真镜面：
  // 低粗糙度给出高光，金属度留一点，反射交给窗外那盏月光。
  const mirror = new THREE.MeshStandardMaterial({
    color: 0xb9c4cd,
    roughness: 0.16,
    metalness: 0.18,
  });
  const ceilingMaterial = new THREE.MeshStandardMaterial({
    color: 0xf4efe8,
    roughness: 0.92,
    metalness: 0,
  });
  if (ceiling.map) ceilingMaterial.map = ceiling.map;
  if (ceiling.normalMap) ceilingMaterial.normalMap = ceiling.normalMap;
  if (ceiling.roughnessMap) ceilingMaterial.roughnessMap = ceiling.roughnessMap;

  const byKind: Record<Exclude<RoomMaterialKind, "keep">, THREE.Material> = {
    ceiling: ceilingMaterial,
    paintedWhite,
    mirror,
  };
  const materials: THREE.Material[] = [paintedWhite, mirror, ceilingMaterial];
  const textures = [ceiling.map, ceiling.normalMap, ceiling.roughnessMap].filter(
    (texture): texture is THREE.Texture => texture !== null,
  );

  room.traverse((child) => {
    const mesh = child as THREE.Mesh;
    if (!mesh.isMesh) return;
    const kind = materialKindFor(mesh.name);
    if (kind === "keep") return;
    const previous = mesh.material;
    const replaced = Array.isArray(previous) ? previous.map(() => byKind[kind]) : byKind[kind];
    mesh.material = replaced;
    if (Array.isArray(previous)) {
      for (const material of previous) material?.dispose();
    } else {
      previous?.dispose();
    }
  });

  return {
    materials,
    textures,
    dispose(): void {
      for (const texture of textures) texture.dispose();
      for (const material of materials) material.dispose();
    },
  };
}

/** 地面世界高度（导入房间地板顶面，见 .superpowers 的包围盒探测）。 */
const WORLD_UP = new THREE.Vector3(0, 1, 0);
const FLOOR_Y = 0.04;
/** 右墙内表面 x（房间导入后旋转到世界坐标的数值）。 */
const RIGHT_WALL_X = 3.45;

export interface DecorPlacement {
  /**
   * 一起复制的网格组。NinjaRipper 是按 draw call 切的网格，不是按家具切的
   * （Mesh_0025out 其实是「显示器 + 主机 + 支架」，真正的衣柜根本不是一个网格），
   * 所以必须整组复制、整体摆，才能保住它们之间的相对位置。
   */
  sources: string[];
  /** 复制品绕 Y 的朝向：-π/2 让原来的 +z 正面转向房间内侧（-x） */
  yaw: number;
  /** 摆到世界坐标的哪里；y 只有挂墙件用（底边高度），落地件用 FLOOR_Y */
  at: { x: number; z: number; y?: number };
  mount: "floor" | "wall";
}

/**
 * 右墙（世界 x≈3.45）原本只有一扇蓝门，一转身就是一大片空墙。
 * 这里不新造资产：把房间自带的衣柜、抽屉柜、挂画各复制一份摆过去，
 * 材质与原家具完全一致，不会出现「两种画风混在一间屋里」。
 * 坐标来自 e2e/room-map.mjs 打印的世界包围盒，改房间尺寸要重新量。
 */
export function decorPlacements(): DecorPlacement[] {
  return [
    // 书桌一整组：桌子 + 桌上的显示器与主机（两个网格共用同一个位移，摆过去还是一套）
    { sources: ["Mesh_0146out", "Mesh_0025out"], yaw: -Math.PI / 2, at: { x: 3.0, z: 0.55 }, mount: "floor" },
    // 床边抽屉柜：单独一件，摆在门与书桌之间
    { sources: ["Mesh_0138out"], yaw: -Math.PI / 2, at: { x: 3.05, z: -0.55 }, mount: "floor" },
  ];
}

export interface RoomDecor {
  root: THREE.Group;
  count: number;
}

/**
 * 按 decorPlacements() 复制家具并摆到位。复制品与母本共享几何与材质，
 * 所以卸载时只需要把自己从场景里摘掉，别去 dispose 这些共享资源。
 */
/**
 * 调试开关：VITE_ROOM_DECOR=none 不摆复制家具；=tag 时给每件复制品上纯色，
 * 用来在截图里认出"这一坨到底是哪个副本"。产品构建里这两个值都不会出现。
 */
const DECOR_MODE = import.meta.env.VITE_ROOM_DECOR ?? "";
const DECOR_DISABLED = DECOR_MODE === "none";
const DECOR_TINT = DECOR_MODE === "tag";

export function furnishImportedRoom(room: THREE.Object3D): RoomDecor {
  const root = new THREE.Group();
  root.name = "room-decor";
  let count = 0;
  if (DECOR_DISABLED) return { root, count };

  for (const placement of decorPlacements()) {
    // 每件母本先按它自己的世界矩阵摆一位（Blender 导出的朝向就在这个矩阵里），
    // 再整组绕组心转 yaw、最后整组平移到落点——只动这层 holder，不碰子节点。
    const holder = new THREE.Group();
    holder.name = placement.sources.join("+") + "-decor";
    for (const name of placement.sources) {
      const source = room.getObjectByName(name);
      if (!source) continue;
      source.updateWorldMatrix(true, false);
      const wrap = new THREE.Group();
      source.matrixWorld.decompose(wrap.position, wrap.quaternion, wrap.scale);
      const clone = source.clone();
      clone.position.set(0, 0, 0);
      clone.quaternion.identity();
      clone.scale.set(1, 1, 1);
      wrap.add(clone);
      holder.add(wrap);
    }
    if (holder.children.length === 0) continue;
    root.add(holder);
    root.updateMatrixWorld(true);

    // 绕组心转：先把当前包围盒中心当支点，转完把 holder 的位移补回去，
    // 否则整组会绕着世界原点甩出去。
    const before = new THREE.Box3().setFromObject(holder);
    const pivot = before.getCenter(new THREE.Vector3());
    const spin = new THREE.Quaternion().setFromAxisAngle(WORLD_UP, placement.yaw);
    holder.quaternion.copy(spin);
    holder.position.copy(pivot).sub(pivot.clone().applyQuaternion(spin));
    root.updateMatrixWorld(true);

    // 落点：底面贴地（挂墙件贴墙），这样母本原来摆在哪儿都不影响结果。
    const box = new THREE.Box3().setFromObject(holder);
    const bottomY = placement.mount === "wall" ? (placement.at.y ?? FLOOR_Y) : FLOOR_Y;
    const centreX = placement.mount === "wall" ? RIGHT_WALL_X - 0.015 - (box.max.x - box.min.x) / 2 : placement.at.x;
    holder.position.x += centreX - (box.min.x + box.max.x) / 2;
    holder.position.z += placement.at.z - (box.min.z + box.max.z) / 2;
    holder.position.y += bottomY - box.min.y;

    holder.traverse((child) => {
      const mesh = child as THREE.Mesh;
      if (!mesh.isMesh) return;
      mesh.castShadow = true;
      mesh.receiveShadow = true;
      if (DECOR_TINT) {
        const tint = [0xff3355, 0x33ff66, 0x3366ff][count] ?? 0xffffff;
        mesh.material = new THREE.MeshBasicMaterial({ color: tint });
      }
    });
    count += 1;
  }

  return { root, count };
}

export interface RoomLightRig {
  root: THREE.Group;
  setShadows(enabled: boolean): void;
  dispose(): void;
}

/**
 * 卧室自己的灯：窗外一盏冷月光当主光与边缘光，床头柜上一盏暖台灯，
 * 再加一点很弱的冷色半球光把死黑提起来。坐标是**房间原始坐标**（挂在 room 下），
 * 房间整体旋转/平移时灯跟着走。
 */
export function createRoomLights(): RoomLightRig {
  const root = new THREE.Group();
  root.name = "room-lights";

  const moon = new THREE.DirectionalLight(0xbcd6ff, 2.1);
  moon.name = "moon";
  moon.position.set(-2.6, -1.1, 2.5);
  moon.target.position.set(0.5, -0.3, 0.6);
  moon.castShadow = true;
  moon.shadow.mapSize.set(1024, 1024);
  moon.shadow.bias = -0.0008;
  moon.shadow.normalBias = 0.02;
  const shadowCamera = moon.shadow.camera;
  shadowCamera.near = 0.4;
  shadowCamera.far = 12;
  shadowCamera.left = -3.4;
  shadowCamera.right = 3.4;
  shadowCamera.top = 3.4;
  shadowCamera.bottom = -3.4;

  const lamp = new THREE.PointLight(0xffb877, 5.0, 4.6, 2);
  lamp.name = "bedside-lamp";
  lamp.position.set(-0.7, 0.6, 0.5);

  const bounce = new THREE.HemisphereLight(0x8fa6d8, 0x1b1620, 0.28);
  bounce.name = "bounce";

  // 斜屋顶本来只被台灯扫到一点，从室内看是一大片近黑的棕；补一盏弱的暖色顶光
  // 把屋脊照出来，不投影（否则屋顶会把整间房子压暗）。
  const ceilingGlow = new THREE.PointLight(0xffdcb8, 6.0, 8.0, 2);
  ceilingGlow.name = "ceiling-glow";
  ceilingGlow.position.set(0.4, -0.5, 2.55);

  root.add(moon, moon.target, lamp, bounce, ceilingGlow);

  return {
    root,
    setShadows(enabled: boolean): void {
      moon.castShadow = enabled;
    },
    dispose(): void {
      moon.dispose();
      lamp.dispose();
      bounce.dispose();
      ceilingGlow.dispose();
      root.clear();
    },
  };
}
