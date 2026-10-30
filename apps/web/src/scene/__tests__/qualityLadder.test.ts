import { describe, expect, it } from "vitest";
import { nextQuality } from "../qualityLadder";

describe("nextQuality", () => {
  it("掉帧时降一级，最低不再降", () => {
    expect(nextQuality("high", 24)).toBe("medium");
    expect(nextQuality("low", 24)).toBe("low");
  });

  it("帧率回到 55 以上时恢复一级", () => {
    expect(nextQuality("low", 60)).toBe("medium");
  });
});
