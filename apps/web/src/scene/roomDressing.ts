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
export type RoomMaterialKind = "ceiling" | "paintedWhite" | "paintedTrim" | "wall" | "mirror" | "keep";

/** 屋顶那块网格：斜屋面 + 四面墙下部 0.75 m 的墙裙都在这一个节点里（见 stripRoofSlope）。 */
export const ROOF_MESH = "Mesh_0116out";
/** 地板：平顶的水平范围与高度都从它的世界包围盒反推。 */
export const FLOOR_MESH = "Mesh_0140out";
/**
 * 屋面三角形的判定线。实测（e2e/roof-split.mjs）：Mesh_0116out 共 22 个三角形，
 * 14 个最高顶点 ≤ 0.75 m（四面墙的白色墙裙），8 个最低顶点 ≥ 2.92 m（两块坡面 + 屋脊）。
 * 取中间值，两边都留足余量。
 */
export const ROOF_SLOPE_MIN_Y = 2.9;

/**
 * 白模判定依据（把 glb 里的贴图抽出来逐张看过，.superpowers/room-tex/）：
 * Tex_0381_2 是三块横板接缝的近白图 → 屋顶；Tex_0084_0 是一张「白塑料板」
 * 图集，肉眼几乎无细节 → 窗套/窗台板（Mesh_0122out，窗上方那道死白横板就是它）；
 * 墙纸 Tex_0376_2 的粉色云与白色顶带是有画的，只是顶带太干净，配一层凹凸就够。
 */
const KIND_BY_MESH: Readonly<Record<string, RoomMaterialKind>> = {
  /** 斜屋面（两块坡面 + 屋脊），原贴图接近纯白；同一个节点里还带着四面墙的墙裙 */
  [ROOF_MESH]: "ceiling",
  /** 床右边那只小抽屉柜 */
  Mesh_0138out: "paintedWhite",
  /** 窗边墙上的四格置物架 */
  Mesh_0200out: "paintedWhite",
  /** 窗套 + 窗上方那道横板（原贴图是白塑料图集，看不出细节） */
  Mesh_0122out: "paintedTrim",
  /** 墙壳：保留原墙纸的粉色云，只补一层凹凸与粗糙度 */
  Mesh_0115out: "wall",
  /** 靠墙的立式穿衣镜（原材质是灰白塑料感） */
  Mesh_0014out: "mirror",
};

export function materialKindFor(meshName: string): RoomMaterialKind {
  return KIND_BY_MESH[meshName] ?? "keep";
}

export interface RoomDressing {
  materials: THREE.Material[];
  textures: THREE.Texture[];
  /** 换掉斜屋顶后补的那块平顶，没做平顶时为 null。 */
  flatCeiling: FlatCeiling | null;
  dispose(): void;
}

/**
 * 侧墙顶高。量出来的：Mesh_0115out 两侧长墙（|x| > 3.2）的最高顶点就是 3.00 m，
 * 山墙往上继续升到 4.68 m 接屋脊。平天花板压在这条线上，接缝正好落在墙顶。
 */
export const EAVE_HEIGHT = 3.0;
/** 平顶贴图的循环尺寸（米）：一块格子大约 0.75 m，和原来斜屋面的格子密度接近。 */
export const CEILING_TILE_M = 0.75;

export interface FlatCeilingLayout {
  width: number;
  depth: number;
  centerX: number;
  centerZ: number;
  y: number;
}

export interface FlatCeiling {
  mesh: THREE.Mesh;
  layout: FlatCeilingLayout;
}

/**
 * 平顶贴图的循环次数。抽成纯函数是为了让单测能钉死「轴序 + 数值」——
 * 直接断言 geometry 的 uv 属性会写成一堆魔法数，断言函数自己算的值则是自证。
 */
export function flatCeilingUv(layout: FlatCeilingLayout): { repeatX: number; repeatY: number } {
  return {
    repeatX: layout.width / CEILING_TILE_M,
    repeatY: layout.depth / CEILING_TILE_M,
  };
}

/**
 * 平顶的位置与尺寸：按地板的水平范围铺，四周各放出一点压进墙体，
 * 高度压在侧墙顶（EAVE_HEIGHT）。抽成纯函数，单测里能直接钉住这些数。
 */
export function flatCeilingLayout(
  min: Readonly<{ x: number; y: number; z: number }>,
  max: Readonly<{ x: number; y: number; z: number }>,
  eaveY: number = EAVE_HEIGHT,
  margin = 0.06,
): FlatCeilingLayout {
  return {
    width: Math.abs(max.x - min.x) + margin * 2,
    depth: Math.abs(max.z - min.z) + margin * 2,
    centerX: (min.x + max.x) / 2,
    centerZ: (min.z + max.z) / 2,
    y: eaveY,
  };
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
  // 腻子/乳胶漆那套图给白色木作补凹凸：不换底色，只让表面不再是「一块橡皮泥」。
  const clay = await loadTriple("clay", 3, 3);
  // 这些网格在原 glb 里都是 doubleSided（薄板、单面墙），换材质要跟着，否则从背面看会漏。
  const paintedWhite = new THREE.MeshStandardMaterial({
    color: 0xf1eae2,
    roughness: 0.72,
    metalness: 0.02,
    side: THREE.DoubleSide,
  });
  if (clay.normalMap) {
    paintedWhite.normalMap = clay.normalMap;
    paintedWhite.normalScale.set(0.45, 0.45);
  }
  if (clay.roughnessMap) paintedWhite.roughnessMap = clay.roughnessMap;
  // 窗套/窗台板：原来贴的是一张白塑料图集，换成有漆面质感的暖白木作。
  const paintedTrim = new THREE.MeshStandardMaterial({
    color: 0xe8dbcb,
    roughness: 0.84,
    metalness: 0,
    side: THREE.DoubleSide,
  });
  // 只用凹凸/粗糙度：clay 的底色是红陶土，直接贴上去会把这圈白木作染成砖红。
  if (clay.normalMap) {
    paintedTrim.normalMap = clay.normalMap;
    paintedTrim.normalScale.set(0.6, 0.6);
  }
  if (clay.roughnessMap) paintedTrim.roughnessMap = clay.roughnessMap;
  // 没有环境贴图时高金属度 = 纯黑，所以这里走「浅灰玻璃」而不是真镜面：
  // 低粗糙度给出高光，金属度留一点，反射交给窗外那盏月光。
  const mirror = new THREE.MeshStandardMaterial({
    color: 0xb9c4cd,
    roughness: 0.16,
    metalness: 0.18,
  });
  const ceilingMaterial = new THREE.MeshStandardMaterial({
    color: 0xe2d7c9,
    roughness: 0.92,
    metalness: 0,
    side: THREE.DoubleSide,
  });
  if (ceiling.map) ceilingMaterial.map = ceiling.map;
  if (ceiling.normalMap) ceilingMaterial.normalMap = ceiling.normalMap;
  if (ceiling.roughnessMap) ceilingMaterial.roughnessMap = ceiling.roughnessMap;

  const byKind: Record<Exclude<RoomMaterialKind, "keep" | "wall">, THREE.Material> = {
    ceiling: ceilingMaterial,
    paintedWhite,
    paintedTrim,
    mirror,
  };
  const materials: THREE.Material[] = [paintedWhite, paintedTrim, mirror, ceilingMaterial];
  const textures = [
    ceiling.map,
    ceiling.normalMap,
    ceiling.roughnessMap,
    clay.map,
    clay.normalMap,
    clay.roughnessMap,
  ].filter((texture): texture is THREE.Texture => texture !== null);

  room.traverse((child) => {
    const mesh = child as THREE.Mesh;
    if (!mesh.isMesh) return;
    const kind = materialKindFor(mesh.name);
    if (kind === "keep") return;
    const previous = mesh.material;
    if (kind === "wall") {
      // 墙纸是这间屋子的主视觉，不能整块换掉：留原色与原贴图，只叠上腻子凹凸。
      const source = Array.isArray(previous) ? previous[0] : previous;
      const standard = source as THREE.MeshStandardMaterial | null;
      const wall = new THREE.MeshStandardMaterial({
        color: standard?.color?.clone() ?? new THREE.Color(0xffffff),
        map: standard?.map ?? null,
        roughness: 0.92,
        metalness: 0,
      });
      if (clay.normalMap) {
        // 墙面是大块斜平面，凹凸给强了会变成一片脏斑；只留一点点，够让白色顶带不是死平。
        wall.normalMap = clay.normalMap;
        wall.normalScale.set(0.14, 0.14);
      }
      if (clay.roughnessMap) wall.roughnessMap = clay.roughnessMap;
      wall.transparent = false;
      wall.side = standard?.side ?? THREE.FrontSide;
      mesh.material = wall;
      materials.push(wall);
    } else {
      const replaced = Array.isArray(previous) ? previous.map(() => byKind[kind]) : byKind[kind];
      mesh.material = replaced;
    }
    if (Array.isArray(previous)) {
      for (const material of previous) material?.dispose();
    } else {
      previous?.dispose();
    }
  });

  // 斜屋顶 → 平顶。材质沿用 ceilingMaterial，所以贴图/参数一处改两处生效。
  const flatCeiling = buildFlatCeiling(room, ceilingMaterial);

  return {
    materials,
    textures,
    flatCeiling,
    dispose(): void {
      // 只摘掉自己加的平顶；共享的材质与纹理统一在下面释放。
      flatCeiling?.mesh.removeFromParent();
      flatCeiling?.mesh.geometry.dispose();
      for (const texture of textures) texture.dispose();
      for (const material of materials) material.dispose();
    },
  };
}

/**
 * 只去掉屋顶节点里的**屋面**三角形，留下同一个节点里四面墙下部的墙裙。
 *
 * 之前这里写的是 roof.visible = false，连 0.75 m 高的白色墙裙一起没了
 * （评审按 glb 顶点算的：22 个三角形里 14 个最高点 ≤ 0.75 m）。
 * 现在按世界坐标逐三角形过滤，重新生成一份非索引几何，返回留下几个三角形。
 */
export function stripRoofSlope(roof: THREE.Mesh, maxY: number = ROOF_SLOPE_MIN_Y): number {
  const geometry = roof.geometry;
  const position = geometry.getAttribute("position");
  if (!position) return 0;
  roof.updateWorldMatrix(true, false);
  const index = geometry.index;
  const vertexCount = index ? index.count : position.count;
  const vertexAt = (i: number): number => (index ? index.getX(i) : i);

  const vertex = new THREE.Vector3();
  const kept: number[] = [];
  for (let i = 0; i + 2 < vertexCount; i += 3) {
    let top = -Infinity;
    for (let k = 0; k < 3; k += 1) {
      vertex.fromBufferAttribute(position, vertexAt(i + k)).applyMatrix4(roof.matrixWorld);
      top = Math.max(top, vertex.y);
    }
    if (top <= maxY) kept.push(vertexAt(i), vertexAt(i + 1), vertexAt(i + 2));
  }
  // 一个都不用去（比如换了一版素材），就别动几何。
  if (kept.length >= vertexCount) return vertexCount / 3;

  const next = new THREE.BufferGeometry();
  for (const name of Object.keys(geometry.attributes)) {
    const source = geometry.getAttribute(name) as THREE.BufferAttribute;
    if ((source as unknown as { isInterleavedBufferAttribute?: boolean }).isInterleavedBufferAttribute) continue;
    const out = new Float32Array(kept.length * source.itemSize);
    let w = 0;
    for (const vi of kept) {
      const base = vi * source.itemSize;
      for (let c = 0; c < source.itemSize; c += 1) out[w + c] = source.array[base + c] as number;
      w += source.itemSize;
    }
    next.setAttribute(name, new THREE.BufferAttribute(out, source.itemSize, source.normalized));
  }

  geometry.dispose();
  roof.geometry = next;
  return kept.length / 3;
}

/**
 * 把斜屋顶换成平顶。
 *
 * 原 glb 的屋面（Mesh_0116out）是两块坡面 + 屋脊，室内看是尖顶；这里去掉屋面三角形、
 * 再按地板的水平范围补一块平天花板，材质沿用屋顶那套（用户要求天花板材质不变）。
 * 山墙在 3.0 m 以上还剩一块三角，被平顶挡住，从室内看不到。
 *
 * 挂载点沿用 room.parent（导入后房间挂在 `root` 下、带 yaw 与位移），位置折回它的局部坐标。
 * 注意：只折了平移、没折旋转 —— 现在唯一调用方 classroom.ts 给的是无旋转的 root；
 * 将来把房间挂到带旋转的组下，这里要一起补四元数。
 */
export function buildFlatCeiling(
  room: THREE.Object3D,
  material: THREE.Material,
): FlatCeiling | null {
  const roof = room.getObjectByName(ROOF_MESH);
  const floor = room.getObjectByName(FLOOR_MESH);
  if (!floor) return null;

  room.updateWorldMatrix(true, true);
  const box = new THREE.Box3().setFromObject(floor);
  if (box.isEmpty()) return null;
  const layout = flatCeilingLayout(box.min, box.max);

  const geometry = new THREE.PlaneGeometry(layout.width, layout.depth);
  const uv = geometry.getAttribute("uv");
  const { repeatX, repeatY } = flatCeilingUv(layout);
  for (let i = 0; i < uv.count; i += 1) {
    uv.setXY(i, uv.getX(i) * repeatX, uv.getY(i) * repeatY);
  }
  uv.needsUpdate = true;
  // PlaneGeometry 默认躺在 XY 面朝 +Z，转成水平面；材质是 DoubleSide，朝上朝下都可见。
  geometry.rotateX(-Math.PI / 2);

  const mesh = new THREE.Mesh(geometry, material);
  mesh.name = "flat-ceiling";
  mesh.receiveShadow = true;

  const parent = room.parent ?? room;
  parent.updateWorldMatrix(true, false);
  // 位置先按世界坐标算好，再折回挂载点的局部坐标 —— 房间自带旋转与位移。
  mesh.position.copy(parent.worldToLocal(new THREE.Vector3(layout.centerX, layout.y, layout.centerZ)));
  parent.add(mesh);

  // 只去屋面，别整个节点关掉：同一个节点里还有四面墙的墙裙。
  if (roof instanceof THREE.Mesh) stripRoofSlope(roof);

  return { mesh, layout };
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
  const ceilingGlow = new THREE.PointLight(0xffdcb8, 4.6, 8.0, 2);
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
