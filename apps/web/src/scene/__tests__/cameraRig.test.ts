import { describe, expect, it } from "vitest";
import { clampOrbit } from "../cameraRig";

describe("clampOrbit", () => {
  it("把越界的偏航与俯仰夹回座位弧", () => {
    expect(clampOrbit(90, 0, 2).yaw).toBe(45);
  });

  it("俯仰与距离同时越界时都夹到上限", () => {
    const orbit = clampOrbit(0, 90, 9);
    expect(orbit.pitch).toBe(25);
    expect(orbit.distance).toBe(3.2);
  });
});
