import { describe, expect, it } from "vitest";
import type { DivisionProfile } from "../../api/types";
import { agesLine } from "./agesLine";

const schoolDefault: DivisionProfile = {
  ageGroups: ["12", "13", "14", "15", "16", "17", "18+"],
  categories: ["M", "F"],
  tiers: ["Competitive"],
};

describe("agesLine", () => {
  it("reads the school's ages as a range ending on the open-ended oldest", () => {
    expect(agesLine(null, schoolDefault)).toBe("12 to 18+ · Boys, Girls · Competitive");
  });

  it("states a saved profile over the school's", () => {
    const saved: DivisionProfile = {
      ...schoolDefault,
      ageGroups: ["14+", "12", "13"],
      tiers: ["Competitive", "Social"],
    };
    expect(agesLine(saved, schoolDefault)).toBe("12 to 14+ · Boys, Girls · Competitive, Social");
  });

  it("still reads a legacy profile whose oldest age is plain", () => {
    const saved: DivisionProfile = { ...schoolDefault, ageGroups: ["13", "12", "14", "SNR"] };
    expect(agesLine(saved, null)).toBe("12 to 14, SNR · Boys, Girls · Competitive");
  });

  it("lists gapped ages rather than claiming a range", () => {
    expect(agesLine({ ...schoolDefault, ageGroups: ["12", "15"] }, null)).toMatch(/^12, 15 ·/);
  });

  it("states nothing with no ages anywhere", () => {
    expect(agesLine(null, null)).toBeNull();
    expect(agesLine(undefined, undefined)).toBeNull();
  });
});
