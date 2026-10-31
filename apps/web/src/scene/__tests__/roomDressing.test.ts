import { describe, expect, it } from "vitest";
import { decorPlacements, materialKindFor } from "../roomDressing";

describe("materialKindFor", () => {
  it("把三处丢贴图的白模与屋顶分到对应材质", () => {
    expect(materialKindFor("Mesh_0116out")).toBe("ceiling");
    expect(materialKindFor("Mesh_0138out")).toBe("paintedWhite");
    expect(materialKindFor("Mesh_0200out")).toBe("paintedWhite");
    expect(materialKindFor("Mesh_0014out")).toBe("mirror");
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
