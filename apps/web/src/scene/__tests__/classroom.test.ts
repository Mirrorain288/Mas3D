import { describe, expect, it } from "vitest";
import * as THREE from "three";
import { buildClassroom, CLASSROOM_ASSETS, findSeatAnchor } from "../classroom";

interface FakeLoader {
  loadAsync(url: string): Promise<{ scene: THREE.Group }>;
}

/** 只让指定 key 成功，其余 reject —— 用来验证「单个槽位失败不影响整间教室」。 */
function loaderThatFailsExcept(succeed: Array<keyof typeof CLASSROOM_ASSETS> = []): FakeLoader {
  return {
    loadAsync(url: string) {
      const key = (Object.keys(CLASSROOM_ASSETS) as Array<keyof typeof CLASSROOM_ASSETS>).find(
        (name) => CLASSROOM_ASSETS[name] === url,
      );
      if (key && succeed.includes(key)) {
        const group = new THREE.Group();
        group.add(new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshStandardMaterial()));
        return Promise.resolve({ scene: group });
      }
      return Promise.reject(new Error(`missing ${url}`));
    },
  };
}

describe("buildClassroom", () => {
  it("所有道具都加载失败时，仍然给出四件占位物并标记 degraded", async () => {
    const built = await buildClassroom(loaderThatFailsExcept() as never);
    expect(built.degraded.sort()).toEqual(["board", "lamp", "lectern", "seat"]);
    const names = built.root.children.map((child) => child.name);
    for (const key of ["seat", "board", "lectern", "lamp"]) expect(names).toContain(key);
    built.dispose();
  });

  it("只有座位可用时，其余槽位降级而座位仍在原点附近", async () => {
    const built = await buildClassroom(loaderThatFailsExcept(["seat"]) as never);
    expect(built.degraded.sort()).toEqual(["board", "lamp", "lectern"]);
    expect(Math.abs(built.seat.position.x)).toBeLessThan(0.01);
    // 立方体占位物没有可用的水平面，退回常量座高
    expect(built.seat.position.y).toBeCloseTo(0.5, 2);
    built.dispose();
  });

  it("从桌凳合一的整体网格里量出坐面，不会落到桌面上", () => {
    const geometry = new THREE.BufferGeometry();
    const points: number[] = [];
    const quad = (y: number, z0: number, z1: number) => {
      points.push(-0.7, y, z0, 0.7, y, z0, 0.7, y, z1, -0.7, y, z1);
    };
    quad(0, -0.9, 0.1); // 地面附近
    quad(0.4, -0.9, -0.55); // 坐面
    quad(0.72, -0.2, 0.1); // 桌面
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(points, 3));
    const object = new THREE.Group();
    object.add(new THREE.Mesh(geometry, new THREE.MeshStandardMaterial()));

    const anchor = findSeatAnchor(object);

    expect(anchor.position.y).toBeCloseTo(0.54, 2); // 坐面 0.40 + hips 偏移 0.14
    expect(anchor.position.z).toBeCloseTo(-0.725, 2); // 坐面质心
    expect(anchor.facingY).toBe(0);
  });

  it("进度回调按槽位推进到 1", async () => {
    const ratios: number[] = [];
    const built = await buildClassroom(loaderThatFailsExcept(["seat", "board"]) as never, (ratio) => ratios.push(ratio));
    expect(ratios.length).toBe(4);
    expect(Math.max(...ratios)).toBe(1);
    built.dispose();
  });
});
