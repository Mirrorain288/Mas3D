import { describe, expect, it, vi } from "vitest";
import { MAX_DELTA_RAD, MIN_DELTA_RAD, solveKneeDelta } from "../seatFit";

// 一个线性假模型：每弧度抬高 0.1 m，膝盖行程之外没有别的力
const linear = (metersPerRad: number, base: number) => (delta: number) => base + delta * metersPerRad;

describe("solveKneeDelta", () => {
  it("线性模型一步到位", () => {
    // 脚在 0.02 m 高，目标 0.06 m，每弧度抬 0.1 m → 需要 0.4 rad
    const delta = solveKneeDelta(linear(0.1, 0.02), 0.06);
    expect(delta).toBeCloseTo(0.4, 3);
  });

  it("凸性模型靠迭代收敛（一阶估计不够）", () => {
    // y = 0.02 + 0.3·d - 0.2·d²，在 d≈0.15 附近目标 0.06
    const measure = (delta: number) => 0.02 + 0.3 * delta - 0.2 * delta * delta;
    const delta = solveKneeDelta(measure, 0.06);
    expect(Math.abs(measure(delta) - 0.06)).toBeLessThan(0.0021);
  });

  it("方向相反（负斜率）也能解，不依赖旋转正负约定", () => {
    // 每弧度反而压低 0.08 m：脚在 0.06 m，目标 0.02 m → 需要 +0.5 rad
    const delta = solveKneeDelta(linear(-0.08, 0.06), 0.02);
    expect(delta).toBeCloseTo(0.5, 3);
  });

  it("目标够不到时夹在边界上，不发散", () => {
    // 每弧度只抬 0.01 m，差 0.5 m 需要 50 rad → 夹到上限
    const delta = solveKneeDelta(linear(0.01, 0), 0.5);
    expect(delta).toBe(MAX_DELTA_RAD);
    const other = solveKneeDelta(linear(0.01, 1), 0.05);
    expect(other).toBe(MIN_DELTA_RAD);
  });

  it("没有垂直杠杆时保持不动", () => {
    const measure = vi.fn(() => 0.05);
    expect(solveKneeDelta(measure, 0.06)).toBe(0);
  });

  it("measure 返回非数时不写坏姿态", () => {
    const measure = vi.fn(() => Number.NaN);
    expect(solveKneeDelta(measure, 0.06)).toBe(0);
  });

  it("已经对齐时几乎不动", () => {
    const measure = vi.fn(linear(0.1, 0.06));
    const delta = solveKneeDelta(measure, 0.06);
    expect(Math.abs(delta)).toBeLessThan(0.002);
  });
});
