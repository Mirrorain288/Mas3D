import { describe, expect, it } from "vitest";
import * as THREE from "three";
import { buildClassroom, CLASSROOM_ASSETS } from "../classroom";

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
    expect(built.seat.position.y).toBe(0);
    built.dispose();
  });

  it("进度回调按槽位推进到 1", async () => {
    const ratios: number[] = [];
    const built = await buildClassroom(loaderThatFailsExcept(["seat", "board"]) as never, (ratio) => ratios.push(ratio));
    expect(ratios.length).toBe(4);
    expect(Math.max(...ratios)).toBe(1);
    built.dispose();
  });
});
