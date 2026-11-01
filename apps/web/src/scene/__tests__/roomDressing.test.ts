import * as THREE from "three";
import { describe, expect, it } from "vitest";
import {
  EAVE_HEIGHT,
  FLOOR_MESH,
  ROOF_MESH,
  ROOF_SLOPE_MIN_Y,
  buildFlatCeiling,
  decorPlacements,
  flatCeilingLayout,
  flatCeilingUv,
  materialKindFor,
  stripRoofSlope,
} from "../roomDressing";

/** 地板 Mesh_0140out 的世界包围盒，探测脚本 room-map 量出来的原值。 */
const FLOOR_MIN = { x: -3.24, y: 0.04, z: -2.74 };
const FLOOR_MAX = { x: 3.46, y: 0.04, z: 4.26 };

describe("materialKindFor", () => {
  it("把三处丢贴图的白模与屋顶分到对应材质", () => {
    expect(materialKindFor("Mesh_0116out")).toBe("ceiling");
    expect(materialKindFor("Mesh_0138out")).toBe("paintedWhite");
    expect(materialKindFor("Mesh_0200out")).toBe("paintedWhite");
    expect(materialKindFor("Mesh_0014out")).toBe("mirror");
  });

  it("窗上方那道白塑料横板换成有质感的白色木作", () => {
    expect(materialKindFor("Mesh_0122out")).toBe("paintedTrim");
  });

  it("墙纸仍归墙纸，只叠凹凸，不整块换掉粉色云", () => {
    expect(materialKindFor("Mesh_0115out")).toBe("wall");
  });

  it("床、地板这些自带贴图的网格保持原样", () => {
    expect(materialKindFor("Mesh_0121out")).toBe("keep");
    expect(materialKindFor("Mesh_0140out")).toBe("keep");
    expect(materialKindFor("平面")).toBe("keep");
  });

  it("换了素材、名字对不上时不偷偷改材质", () => {
    expect(materialKindFor("Mesh_9999out")).toBe("keep");
    expect(materialKindFor("")).toBe("keep");
  });
});

describe("flatCeilingLayout", () => {
  it("平顶压在侧墙顶 3.00 m，而不是屋脊 4.68 m", () => {
    const layout = flatCeilingLayout(FLOOR_MIN, FLOOR_MAX);
    expect(layout.y).toBe(EAVE_HEIGHT);
    expect(layout.y).toBeCloseTo(3.0, 5);
  });

  it("按地板范围铺满，四周略放出压进墙体", () => {
    const layout = flatCeilingLayout(FLOOR_MIN, FLOOR_MAX, EAVE_HEIGHT, 0.06);
    expect(layout.width).toBeCloseTo(6.7 + 0.12, 5);
    expect(layout.depth).toBeCloseTo(7.0 + 0.12, 5);
    expect(layout.centerX).toBeCloseTo(0.11, 5);
    expect(layout.centerZ).toBeCloseTo(0.76, 5);
  });

  it("贴图循环次数按 0.75 m 一格算，宽度走 X、深度走 Y", () => {
    // 6.82 / 0.75 = 9.0933，7.12 / 0.75 = 9.4933：两个数不相等，换算轴调换会立刻红。
    const uv = flatCeilingUv(flatCeilingLayout(FLOOR_MIN, FLOOR_MAX));
    expect(uv.repeatX).toBeCloseTo(9.0933, 3);
    expect(uv.repeatY).toBeCloseTo(9.4933, 3);
  });

  it("放过墙体的边距改了，循环次数跟着变（默认值也走这条路径）", () => {
    const narrow = flatCeilingUv(flatCeilingLayout(FLOOR_MIN, FLOOR_MAX, EAVE_HEIGHT, 0.06));
    const none = flatCeilingUv(flatCeilingLayout(FLOOR_MIN, FLOOR_MAX, EAVE_HEIGHT, 0));
    expect(narrow.repeatX).toBeGreaterThan(none.repeatX);
    expect(none.repeatX).toBeCloseTo(6.7 / 0.75, 4);
  });
});

describe("buildFlatCeiling", () => {
  it("平顶挂在房间的父节点下，世界位置压在侧墙顶、接着收阴影", () => {
    const { parent, room } = fakeRoom(true);
    const out = buildFlatCeiling(room, new THREE.MeshBasicMaterial());
    expect(out).not.toBeNull();
    expect(out?.mesh.parent).toBe(parent);
    expect(out?.mesh.name).toBe("flat-ceiling");
    expect(out?.mesh.receiveShadow).toBe(true);
    expect(out?.layout.y).toBe(EAVE_HEIGHT);
    parent.updateWorldMatrix(true, true);
    const world = out!.mesh.getWorldPosition(new THREE.Vector3());
    expect(world.x).toBeCloseTo(0.11, 5);
    expect(world.y).toBeCloseTo(EAVE_HEIGHT, 5);
    expect(world.z).toBeCloseTo(0.76, 5);
  });

  it("平顶的 uv 真的放大到循环次数，轴调换会红", () => {
    const { room } = fakeRoom(true);
    const out = buildFlatCeiling(room, new THREE.MeshBasicMaterial());
    const uv = out!.mesh.geometry.getAttribute("uv");
    let maxU = 0;
    let maxV = 0;
    for (let i = 0; i < uv.count; i += 1) {
      maxU = Math.max(maxU, uv.getX(i));
      maxV = Math.max(maxV, uv.getY(i));
    }
    expect(maxU).toBeCloseTo(9.0933, 3);
    expect(maxV).toBeCloseTo(9.4933, 3);
  });

  it("只去屋面，四面墙的墙裙留着（整个节点不许关掉）", () => {
    const { room, roof } = fakeRoom(true);
    buildFlatCeiling(room, new THREE.MeshBasicMaterial());
    expect(roof.visible).toBe(true);
    expect(roof.geometry.getAttribute("position").count).toBe(3);
  });

  it("没有地板时返回 null，屋顶几何不动", () => {
    const { room, roof } = fakeRoom(false);
    expect(buildFlatCeiling(room, new THREE.MeshBasicMaterial())).toBeNull();
    expect(roof.geometry.getAttribute("position").count).toBe(6);
  });
});

describe("stripRoofSlope", () => {
  it("按世界 y 过滤：低处三角形留下，屋面去掉，属性一起搬过去", () => {
    const roof = new THREE.Mesh(roofWithWallAndSlope(), new THREE.MeshBasicMaterial());
    expect(stripRoofSlope(roof)).toBe(1);
    expect(roof.geometry.getAttribute("position").count).toBe(3);
    expect(roof.geometry.getAttribute("uv").count).toBe(3);
  });

  it("全在阈值以下时原样保留，不重建几何", () => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.BufferAttribute(new Float32Array(9), 3));
    const roof = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial());
    expect(stripRoofSlope(roof)).toBe(1);
    expect(roof.geometry).toBe(geometry);
  });

  it("阈值落在墙裙与屋面之间（实测 0.75 与 2.92）", () => {
    expect(ROOF_SLOPE_MIN_Y).toBeGreaterThan(0.75);
    expect(ROOF_SLOPE_MIN_Y).toBeLessThan(2.92);
  });
});

/** 屋顶那个节点的简化版：一个低处三角形（墙裙）+ 一个高处三角形（屋面）。 */
function roofWithWallAndSlope(): THREE.BufferGeometry {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute(
    "position",
    new THREE.BufferAttribute(
      new Float32Array([-1, 0.0, 0, 1, 0.0, 0, 1, 0.4, 0, -1, 3.2, 0, 1, 3.2, 0, 1, 4.6, 0]),
      3,
    ),
  );
  geometry.setAttribute("uv", new THREE.BufferAttribute(new Float32Array(12), 2));
  return geometry;
}

function fakeRoom(withFloor: boolean): { parent: THREE.Group; room: THREE.Group; roof: THREE.Mesh } {
  const parent = new THREE.Group();
  const room = new THREE.Group();
  parent.add(room);
  const roof = new THREE.Mesh(roofWithWallAndSlope(), new THREE.MeshBasicMaterial());
  roof.name = ROOF_MESH;
  room.add(roof);
  if (withFloor) {
    const floor = new THREE.Mesh(new THREE.BoxGeometry(6.7, 0.02, 7), new THREE.MeshBasicMaterial());
    floor.name = FLOOR_MESH;
    floor.position.set(0.11, 0.04, 0.76);
    room.add(floor);
  }
  return { parent, room, roof };
}

describe("decorPlacements", () => {
  it("每件复制品都有一组非空的母本网格", () => {
    for (const placement of decorPlacements()) {
      expect(placement.sources.length).toBeGreaterThan(0);
      for (const name of placement.sources) expect(name).toMatch(/^Mesh_\d+out$/);
    }
  });

  it("书桌与桌上的显示器必须同组，否则摆过去会散架", () => {
    const desk = decorPlacements().find((placement) => placement.sources.includes("Mesh_0146out"));
    expect(desk).toBeDefined();
    expect(desk?.sources).toContain("Mesh_0025out");
  });

  it("只允许四分之一圈的朝向，落点是有限数", () => {
    for (const placement of decorPlacements()) {
      const quarter = placement.yaw / (Math.PI / 2);
      expect(Math.abs(quarter - Math.round(quarter))).toBeLessThan(1e-9);
      expect(Number.isFinite(placement.at.x)).toBe(true);
      expect(Number.isFinite(placement.at.z)).toBe(true);
      if (placement.mount === "wall") expect(Number.isFinite(placement.at.y ?? NaN)).toBe(true);
    }
  });
});
