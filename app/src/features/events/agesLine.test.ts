import { describe, expect, it } from "vitest";
import type { DivisionProfile } from "../../api/types";
import { agesLine, leftOutText, staleText } from "./agesLine";

/** ADR-0067 §1, §6: one line for the carnival's ages; only saved ages can disagree. */

const rosterDefault: DivisionProfile = {
  ageGroups: ["12", "13", "14", "15", "16", "17", "18"],
  categories: ["M", "F"],
  tiers: ["Competitive"],
};

describe("agesLine", () => {
  it("names the roster default as coming from the student list", () => {
    const line = agesLine(null, rosterDefault, rosterDefault.ageGroups!);
    expect(line).toEqual({
      text: "Ages 12–18 from the student list · Boys, Girls · Competitive",
      stale: [],
      leftOut: [],
    });
  });

  it("states a saved profile as it is, with nothing to say when the roster agrees", () => {
    const saved: DivisionProfile = {
      ...rosterDefault,
      ageGroups: ["14", "12", "13"],
      tiers: ["Competitive", "Social"],
    };
    const line = agesLine(saved, null, ["13", "12", "14"]);
    expect(line?.text).toBe("Ages 12–14 · Boys, Girls · Competitive, Social");
    expect(line?.stale).toEqual([]);
    expect(line?.leftOut).toEqual([]);
  });

  it("names saved ages no student has as stale", () => {
    const line = agesLine(rosterDefault, null, ["SNR", "13", "12", "14", "15", "16", "17"]);
    expect(line?.stale).toEqual(["18"]);
    expect(staleText(line!.stale)).toBe(
      "This carnival has ages no student has: 18. Their divisions can’t be entered.",
    );
  });

  it("names the student list's ages the carnival leaves out, in key order", () => {
    const saved: DivisionProfile = { ...rosterDefault, ageGroups: ["12", "13"] };
    const line = agesLine(saved, null, ["SNR", "13", "12", "11"]);
    expect(line?.stale).toEqual([]);
    expect(line?.leftOut).toEqual(["11", "SNR"]);
    expect(leftOutText(line!.leftOut)).toBe(
      "Left out of this carnival: 11, SNR. Students of those ages can’t be entered.",
    );
  });

  it("compares nothing without a roster", () => {
    for (const roster of [[], undefined]) {
      const line = agesLine(rosterDefault, null, roster);
      expect(line?.stale).toEqual([]);
      expect(line?.leftOut).toEqual([]);
    }
  });

  it("orders SNR after the numeric ages", () => {
    const roster = ["SNR", "13", "12"];
    const line = agesLine(null, { ...rosterDefault, ageGroups: roster }, roster);
    expect(line?.text).toBe("Ages 12–13, SNR from the student list · Boys, Girls · Competitive");
  });

  it("states nothing with neither a saved profile nor a roster", () => {
    expect(agesLine(null, null, [])).toBeNull();
    expect(agesLine(undefined, undefined, undefined)).toBeNull();
  });
});
