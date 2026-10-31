import * as THREE from "three";
import type { ClassroomBuild } from "./classroom";

/**
 * B 方案原型：程序化小卧室。
 *
 * 和导入的整套房间比，这里每个尺寸、每张贴图都是我们自己的：
 * 地板是矩形、正好和四面墙对齐（不再是「圆地板塞进方墙」），
 * 墙面/地板/天花板用 1K PBR 三件套（颜色 + 法线 + 粗糙度），
 * 家具是箱体拼的、但都有明确语义与标注位置，改布局只改下面的常量。
 *
 * 房间 5.0 × 4.6 × 2.5 m —— 比现在的教室小一圈，镜头不必再靠家具密度硬撑。
 */
export const BEDROOM = {
  width: 5.0,
  depth: 4.6,
  height: 2.5,
  /** 窗洞在背墙上：x 跨度与上下沿 */
  window: { x0: -0.85, x1: 0.85, y0: 1.0, y1: 2.0 },
  /** 床：床尾朝相机，她坐在床沿 */
  bed: { x0: -1.15, x1: 0.45, z0: -2.24, z1: -0.24, frameTop: 0.3, mattressTop: 0.52 },
} as const;

const WALL_THICKNESS = 0.12;
/** 坐面到 hips 的距离，和教室里的标定一致 */
const HIP_ABOVE_SEAT = 0.14;

type Triple = {
  map: THREE.Texture | null;
  normalMap: THREE.Texture | null;
  roughnessMap: THREE.Texture | null;
};

async function loadTriple(base: string, repeatX: number, repeatY: number): Promise<Triple> {
  const load = async (url: string, srgb: boolean): Promise<THREE.Texture | null> => {
    try {
      const texture = await new THREE.TextureLoader().loadAsync(url);
      if (srgb) texture.colorSpace = THREE.SRGBColorSpace;
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
  if (normalMap) normalMap.colorSpace = THREE.NoColorSpace;
  return { map, normalMap, roughnessMap };
}

interface Kit {
  materials: THREE.Material[];
  textures: THREE.Texture[];
  geometries: THREE.BufferGeometry[];
}

function makeKit(): Kit {
  return { materials: [], textures: [], geometries: [] };
}

function fromTriple(kit: Kit, triple: Triple, options: { color?: number; roughness?: number; metalness?: number } = {}): THREE.MeshStandardMaterial {
  const material = new THREE.MeshStandardMaterial({
    color: options.color ?? 0xffffff,
    roughness: options.roughness ?? 0.9,
    metalness: options.metalness ?? 0.02,
  });
  if (triple.map) material.map = triple.map;
  if (triple.normalMap) material.normalMap = triple.normalMap;
  if (triple.roughnessMap) material.roughnessMap = triple.roughnessMap;
  for (const texture of [triple.map, triple.normalMap, triple.roughnessMap]) {
    if (texture) kit.textures.push(texture);
  }
  kit.materials.push(material);
  return material;
}

function plain(kit: Kit, color: number, roughness: number, metalness = 0): THREE.MeshStandardMaterial {
  const material = new THREE.MeshStandardMaterial({ color, roughness, metalness });
  kit.materials.push(material);
  return material;
}

/** 箱体：position 给的是底面中心（y 是底），和其它家具常量一致。 */
function box(
  kit: Kit,
  group: THREE.Group,
  material: THREE.Material,
  size: [number, number, number],
  at: [number, number, number],
  options: { cast?: boolean } = {},
): THREE.Mesh {
  const geometry = new THREE.BoxGeometry(size[0], size[1], size[2]);
  kit.geometries.push(geometry);
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.set(at[0], at[1] + size[1] / 2, at[2]);
  mesh.castShadow = options.cast ?? true;
  mesh.receiveShadow = true;
  group.add(mesh);
  return mesh;
}

function buildShell(kit: Kit, floorMaterial: THREE.Material, wallMaterial: THREE.Material, ceilingMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "bedroom-shell";
  const { width, depth, height, window: win } = BEDROOM;

  const floorGeometry = new THREE.PlaneGeometry(width, depth);
  kit.geometries.push(floorGeometry);
  const floor = new THREE.Mesh(floorGeometry, floorMaterial);
  floor.rotation.x = -Math.PI / 2;
  floor.receiveShadow = true;
  floor.name = "floor";
  group.add(floor);

  const ceilingGeometry = new THREE.PlaneGeometry(width, depth);
  kit.geometries.push(ceilingGeometry);
  const ceiling = new THREE.Mesh(ceilingGeometry, ceilingMaterial);
  ceiling.rotation.x = Math.PI / 2;
  ceiling.position.y = height;
  ceiling.receiveShadow = true;
  group.add(ceiling);

  // 背墙：绕窗洞拼四块，窗洞里直接能看到场景的星空
  const backZ = -depth / 2;
  const sideW = width / 2 - win.x1;
  const leftPanelX = -(win.x1 + sideW / 2);
  const rightPanelX = win.x1 + sideW / 2;
  box(kit, group, wallMaterial, [sideW, height, WALL_THICKNESS], [leftPanelX, 0, backZ]);
  box(kit, group, wallMaterial, [sideW, height, WALL_THICKNESS], [rightPanelX, 0, backZ]);
  box(kit, group, wallMaterial, [win.x1 - win.x0, win.y0, WALL_THICKNESS], [0, 0, backZ]);
  box(kit, group, wallMaterial, [win.x1 - win.x0, height - win.y1, WALL_THICKNESS], [0, win.y1, backZ]);

  // 两侧墙。**没有前墙**：相机在 z≈3 m 处、比房间的 z=+2.3 还靠外，
  // 一旦补上前墙就会贴到镜头前面（第一版试跑整屏都是墙皮）。座位弧限制在 ±45°，
  // 相机永远看不到自己背后，所以留空是安全的。
  for (const side of [-1, 1]) {
    box(kit, group, wallMaterial, [WALL_THICKNESS, height, depth], [side * (width / 2 - WALL_THICKNESS / 2), 0, 0]);
  }

  // 踢脚线：四面墙脚各一条，便宜但极能提升"装修完了"的观感
  const skirtMaterial = plain(kit, 0xfaf5ee, 0.75);
  const inset = WALL_THICKNESS;
  box(kit, group, skirtMaterial, [width - inset, 0.09, 0.02], [0, 0, -depth / 2 + inset + 0.01], { cast: false });
  for (const side of [-1, 1]) {
    box(kit, group, skirtMaterial, [0.02, 0.09, depth - inset], [side * (width / 2 - inset - 0.01), 0, 0], { cast: false });
  }

  return group;
}

function buildWindow(kit: Kit, frameMaterial: THREE.Material, clothMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "window";
  const { window: win } = BEDROOM;
  const backZ = -BEDROOM.depth / 2;

  // 窗框 + 窗台
  const bar = 0.06;
  box(kit, group, frameMaterial, [win.x1 - win.x0 + bar * 2, bar, WALL_THICKNESS + 0.02], [0, win.y1, backZ], { cast: false });
  box(kit, group, frameMaterial, [win.x1 - win.x0 + bar * 2, bar, WALL_THICKNESS + 0.02], [0, win.y0 - bar, backZ], { cast: false });
  for (const side of [-1, 1]) {
    box(kit, group, frameMaterial, [bar, win.y1 - win.y0 + bar * 2, WALL_THICKNESS + 0.02], [side * (win.x1 + bar / 2), win.y0 - bar, backZ], { cast: false });
  }
  // 中竖梃
  box(kit, group, frameMaterial, [0.04, win.y1 - win.y0, 0.06], [0, win.y0, backZ], { cast: false });
  // 窗台板
  box(kit, group, frameMaterial, [win.x1 - win.x0 + 0.3, 0.05, 0.22], [0, win.y0 - bar - 0.05, backZ + WALL_THICKNESS / 2 + 0.05]);

  // 窗帘：两片带褶皱的平面（顶点沿 x 做正弦位移），比贴图箱子更像布
  const curtain = (centerX: number, flip: number): void => {
    const geometry = new THREE.PlaneGeometry(0.62, 1.62, 16, 1);
    const positions = geometry.getAttribute("position");
    for (let i = 0; i < positions.count; i += 1) {
      const x = positions.getX(i);
      positions.setZ(i, Math.sin((x + 0.31) * 26) * 0.035 * flip);
    }
    geometry.computeVertexNormals();
    kit.geometries.push(geometry);
    const mesh = new THREE.Mesh(geometry, clothMaterial);
    mesh.position.set(centerX, win.y0 + 0.62, backZ + 0.16);
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    group.add(mesh);
  };
  curtain(win.x0 - 0.18, 1);
  curtain(win.x1 + 0.18, -1);

  // 窗帘杆
  const rodGeometry = new THREE.CylinderGeometry(0.016, 0.016, 2.4, 10);
  kit.geometries.push(rodGeometry);
  const rod = new THREE.Mesh(rodGeometry, frameMaterial);
  rod.rotation.z = Math.PI / 2;
  rod.position.set(0, win.y1 + 0.14, backZ + 0.18);
  group.add(rod);

  return group;
}

function buildBed(kit: Kit, bedMaterial: THREE.Material, sheetMaterial: THREE.Material, duvetMaterial: THREE.Material, woodMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "bed";
  const bed = BEDROOM.bed;
  const centerX = (bed.x0 + bed.x1) / 2;
  const width = bed.x1 - bed.x0;
  const length = bed.z1 - bed.z0;
  const centerZ = (bed.z0 + bed.z1) / 2;

  // 床架 + 四条腿
  box(kit, group, woodMaterial, [width, 0.18, length], [centerX, 0.12, centerZ]);
  for (const x of [bed.x0 + 0.1, bed.x1 - 0.1]) {
    for (const z of [bed.z0 + 0.12, bed.z1 - 0.12]) {
      box(kit, group, woodMaterial, [0.08, 0.12, 0.08], [x, 0, z]);
    }
  }
  // 床垫
  box(kit, group, sheetMaterial, [width - 0.04, bed.mattressTop - bed.frameTop, length - 0.04], [centerX, bed.frameTop, centerZ]);
  // 被子：盖住靠床尾那一段，比床垫宽出一点
  box(kit, group, duvetMaterial, [width + 0.06, 0.1, length * 0.62], [centerX, bed.mattressTop - 0.02, centerZ + length * 0.14]);
  // 枕头
  for (const offset of [-0.36, 0.36]) {
    box(kit, group, sheetMaterial, [0.58, 0.13, 0.34], [centerX + offset, bed.mattressTop, bed.z0 + 0.26]);
  }
  // 床头板
  box(kit, group, woodMaterial, [width + 0.1, 0.62, 0.07], [centerX, 0.24, bed.z0 - 0.02]);

  return group;
}

function buildNightstand(kit: Kit, woodMaterial: THREE.Material, accentMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "nightstand";
  const x = BEDROOM.bed.x1 + 0.42;
  const z = -1.98;
  box(kit, group, woodMaterial, [0.44, 0.52, 0.4], [x, 0, z]);
  box(kit, group, accentMaterial, [0.4, 0.16, 0.02], [x, 0.3, z + 0.21], { cast: false });

  // 台灯：底座 + 灯杆 + 灯罩（灯罩自发光，另配一盏暖光）
  const base = new THREE.CylinderGeometry(0.09, 0.1, 0.03, 20);
  kit.geometries.push(base);
  const baseMesh = new THREE.Mesh(base, accentMaterial);
  baseMesh.position.set(x, 0.535, z);
  baseMesh.castShadow = true;
  group.add(baseMesh);

  const pole = new THREE.CylinderGeometry(0.015, 0.015, 0.24, 8);
  kit.geometries.push(pole);
  const poleMesh = new THREE.Mesh(pole, accentMaterial);
  poleMesh.position.set(x, 0.67, z);
  group.add(poleMesh);

  const shadeMaterial = plain(kit, 0xfff0d2, 0.6);
  shadeMaterial.emissive = new THREE.Color(0xffca86);
  shadeMaterial.emissiveIntensity = 1.35;
  const shade = new THREE.ConeGeometry(0.13, 0.16, 20, 1, true);
  kit.geometries.push(shade);
  const shadeMesh = new THREE.Mesh(shade, shadeMaterial);
  shadeMesh.position.set(x, 0.855, z);
  group.add(shadeMesh);

  return group;
}

function buildWardrobe(kit: Kit, woodMaterial: THREE.Material, accentMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "wardrobe";
  const x = BEDROOM.width / 2 - 0.4;
  const z = -1.25;
  box(kit, group, woodMaterial, [0.6, 1.95, 1.2], [x, 0, z]);
  // 两扇门板贴在朝向相机的 -x 面，把手也放这边
  box(kit, group, accentMaterial, [0.02, 1.86, 0.58], [x - 0.31, 0.05, z - 0.3], { cast: false });
  box(kit, group, accentMaterial, [0.02, 1.86, 0.58], [x - 0.31, 0.05, z + 0.3], { cast: false });
  for (const offset of [-0.12, 0.12]) {
    const handle = new THREE.CylinderGeometry(0.012, 0.012, 0.16, 8);
    kit.geometries.push(handle);
    const mesh = new THREE.Mesh(handle, accentMaterial);
    mesh.position.set(x - 0.33, 1.0, z + offset);
    group.add(mesh);
  }
  // 正面（朝向相机的 +z 面）加一道中缝与两个把手，否则从座位视角看就是一块平板
  box(kit, group, accentMaterial, [0.56, 1.9, 0.015], [x, 0.03, z + 0.61], { cast: false });
  for (const offset of [-0.1, 0.1]) {
    const handle = new THREE.CylinderGeometry(0.013, 0.013, 0.18, 8);
    kit.geometries.push(handle);
    const mesh = new THREE.Mesh(handle, accentMaterial);
    mesh.position.set(x + offset, 1.0, z + 0.62);
    group.add(mesh);
  }
  return group;
}

function buildDesk(kit: Kit, woodMaterial: THREE.Material, accentMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "desk";
  const x = BEDROOM.width / 2 - 0.36;
  const z = 0.9;
  box(kit, group, woodMaterial, [0.62, 0.05, 1.4], [x, 0.72, z]);
  for (const dz of [-0.62, 0.62]) {
    box(kit, group, accentMaterial, [0.05, 0.72, 0.05], [x + 0.24, 0, z + dz]);
    box(kit, group, accentMaterial, [0.05, 0.72, 0.05], [x - 0.24, 0, z + dz]);
  }
  // 显示器：底座 + 立柱 + 屏
  const screenMaterial = plain(kit, 0x1b2430, 0.35, 0.15);
  screenMaterial.emissive = new THREE.Color(0x2c4a6e);
  screenMaterial.emissiveIntensity = 0.55;
  box(kit, group, accentMaterial, [0.22, 0.02, 0.16], [x, 0.77, z - 0.1], { cast: false });
  box(kit, group, accentMaterial, [0.04, 0.2, 0.04], [x, 0.79, z - 0.1], { cast: false });
  box(kit, group, screenMaterial, [0.02, 0.34, 0.56], [x - 0.02, 0.98, z - 0.1], { cast: false });
  return group;
}

/** 吸顶吊灯：一根吊绳 + 一个自发光灯罩，替天花板中央留一个视线上方的锚点。 */
function buildPendant(kit: Kit, accentMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "pendant";
  const x = 0.3;
  const z = 0.7;
  const cord = new THREE.CylinderGeometry(0.006, 0.006, 0.42, 6);
  kit.geometries.push(cord);
  const cordMesh = new THREE.Mesh(cord, accentMaterial);
  cordMesh.position.set(x, BEDROOM.height - 0.21, z);
  group.add(cordMesh);

  const shadeMaterial = plain(kit, 0xfff2dc, 0.65);
  shadeMaterial.emissive = new THREE.Color(0xffd7a3);
  shadeMaterial.emissiveIntensity = 1.6;
  shadeMaterial.side = THREE.DoubleSide;
  const shade = new THREE.SphereGeometry(0.17, 20, 12, 0, Math.PI * 2, 0, Math.PI / 2);
  kit.geometries.push(shade);
  const shadeMesh = new THREE.Mesh(shade, shadeMaterial);
  shadeMesh.position.set(x, BEDROOM.height - 0.44, z);
  shadeMesh.rotation.x = Math.PI;
  group.add(shadeMesh);

  return group;
}

function buildRug(kit: Kit, clothMaterial: THREE.Material): THREE.Mesh {
  const geometry = new THREE.CircleGeometry(1.02, 56);
  kit.geometries.push(geometry);
  const mesh = new THREE.Mesh(geometry, clothMaterial);
  mesh.rotation.x = -Math.PI / 2;
  mesh.position.set(0.15, 0.006, 0.95);
  mesh.receiveShadow = true;
  mesh.name = "rug";
  return mesh;
}

function buildWallArt(kit: Kit, frameMaterial: THREE.Material): THREE.Group {
  const group = new THREE.Group();
  group.name = "wall-art";
  const palette = [0xd98fa1, 0x7f9ec4, 0xe4c68d];
  const x = -BEDROOM.width / 2 + 0.02;
  const spots: Array<[number, number, number]> = [
    [-1.35, 1.55, 0.5],
    [-0.55, 1.62, 0.34],
    [0.35, 1.45, 0.6],
  ];
  spots.forEach(([z, y, size], index) => {
    box(kit, group, frameMaterial, [0.04, size, size * 0.8], [x, y - size / 2, z], { cast: false });
    const color = palette[index % palette.length] as number;
    const art = plain(kit, color, 0.85);
    box(kit, group, art, [0.012, size * 0.86, size * 0.66], [x + 0.026, y - size / 2 + size * 0.07, z], { cast: false });
  });
  return group;
}

/**
 * 装配整间卧室，返回和教室同形的 ClassroomBuild：座位在床沿、朝向相机。
 */
export async function buildBedroom(onProgress?: (ratio: number) => void): Promise<ClassroomBuild> {
  const kit = makeKit();
  const steps = 4;
  let done = 0;
  const tick = (): void => {
    done += 1;
    onProgress?.(done / steps);
  };

  const [floorTriple, wallTriple, ceilingTriple] = await Promise.all([
    loadTriple("wood-floor", BEDROOM.width / 1.6, BEDROOM.depth / 1.6),
    loadTriple("clay", 2.4, 1.1),
    loadTriple("ceiling", BEDROOM.width / 2.4, BEDROOM.depth / 2.4),
  ]);
  tick();

  const floorMaterial = fromTriple(kit, floorTriple, { roughness: 0.72 });
  // clay 的颜色贴图偏红砖，当卧室墙会像木屋；只留法线与粗糙度，拿纯色当乳胶漆
  const wallMaterial = new THREE.MeshStandardMaterial({ color: 0xf4ece4, roughness: 0.95, metalness: 0 });
  wallMaterial.normalMap = wallTriple.normalMap;
  wallMaterial.roughnessMap = wallTriple.roughnessMap;
  if (wallTriple.normalMap) kit.textures.push(wallTriple.normalMap);
  if (wallTriple.roughnessMap) kit.textures.push(wallTriple.roughnessMap);
  if (wallTriple.map) wallTriple.map.dispose();
  kit.materials.push(wallMaterial);
  const ceilingMaterial = fromTriple(kit, ceilingTriple, { color: 0xf2e9dc, roughness: 0.98 });
  const woodMaterial = plain(kit, 0x8a6144, 0.62, 0.04);
  const accentMaterial = plain(kit, 0xb08a6a, 0.55, 0.1);
  const sheetMaterial = plain(kit, 0xf3f1ee, 0.95);
  const duvetMaterial = plain(kit, 0xd9b7bd, 0.98);
  const clothMaterial = plain(kit, 0xc9a2a8, 1.0);
  const frameMaterial = plain(kit, 0x3d3a38, 0.5, 0.3);
  tick();

  const root = new THREE.Group();
  root.name = "classroom";
  root.add(buildShell(kit, floorMaterial, wallMaterial, ceilingMaterial));
  root.add(buildWindow(kit, frameMaterial, clothMaterial));
  root.add(buildBed(kit, woodMaterial, sheetMaterial, duvetMaterial, woodMaterial));
  root.add(buildNightstand(kit, woodMaterial, accentMaterial));
  root.add(buildWardrobe(kit, woodMaterial, accentMaterial));
  root.add(buildDesk(kit, woodMaterial, accentMaterial));
  root.add(buildRug(kit, clothMaterial));
  root.add(buildPendant(kit, accentMaterial));
  root.add(buildWallArt(kit, frameMaterial));
  tick();

  // 两盏暖光：台灯压住床头的暗部，吸顶灯给整体一个低对比的底
  const lampLight = new THREE.PointLight(0xffcf94, 9, 6.5, 2);
  lampLight.position.set(BEDROOM.bed.x1 + 0.42, 0.95, -1.98);
  const roomLight = new THREE.PointLight(0xffe0bd, 12, 9, 2);
  roomLight.position.set(0.3, 1.98, 0.7);
  root.add(lampLight, roomLight);
  tick();

  const seat = new THREE.Vector3(
    (BEDROOM.bed.x0 + BEDROOM.bed.x1) / 2,
    BEDROOM.bed.mattressTop + HIP_ABOVE_SEAT,
    BEDROOM.bed.z1 - 0.1,
  );

  return {
    root,
    degraded: [],
    seat: { position: seat, facingY: 0 },
    setShadows(enabled: boolean): void {
      root.traverse((child) => {
        const mesh = child as THREE.Mesh;
        if (mesh.isMesh) mesh.castShadow = enabled;
      });
      lampLight.castShadow = false;
      roomLight.castShadow = false;
    },
    dispose(): void {
      for (const geometry of kit.geometries) geometry.dispose();
      for (const material of kit.materials) material.dispose();
      for (const texture of kit.textures) texture.dispose();
      lampLight.dispose();
      roomLight.dispose();
      root.clear();
    },
  };
}
