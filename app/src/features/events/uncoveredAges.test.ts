import { describe, expect, it } from "vitest";
import type { DivisionProfile } from "../../api/types";
import {
  extendedText,
  extension,
  uncoveredStudents,
  uncoveredText,
  uncoveredWarning,
  youngestAge,
} from "./uncoveredAges";

const profile: DivisionProfile = {
  ageGroups: ["12", "13", "14", "15", "16", "17", "18+"],
  categories: ["M", "F"],
  tiers: ["Competitive"],
};

describe("uncovered ages wording", () => {
  it("names one age and its students", () => {
    const one = [{ ageGroup: "11", students: 14 }];
    expect(uncoveredText(one)).toBe(
      "14 students are aged 11, and this carnival has no events for age 11.",
    );
    expect(uncoveredWarning(one)).toBe("14 students are aged 11, and no event has age 11.");
  });

  it("names several ages youngest first, as those ages", () => {
    const two = [
      { ageGroup: "11", students: 10 },
      { ageGroup: "10", students: 4 },
    ];
    expect(uncoveredText(two)).toBe(
      "14 students are aged 10 or 11, and this carnival has no events for those ages.",
    );
    expect(uncoveredWarning(two)).toBe("14 students are aged 10 or 11, and no event has those ages.");
    expect(
      uncoveredStudents([...two, { ageGroup: "9", students: 1 }]),
    ).toBe("15 students are aged 9, 10 or 11");
  });

  it("says one student is", () => {
    expect(uncoveredStudents([{ ageGroup: "11", students: 1 }])).toBe("1 student is aged 11");
  });
});

describe("extension", () => {
  it("offers the one age just below the youngest", () => {
    expect(extension([{ ageGroup: "11", students: 14 }], profile)).toEqual({
      youngest: 11,
      label: "Add age 11",
    });
  });

  it("offers every age down to the youngest uncovered one", () => {
    const uncovered = [
      { ageGroup: "9", students: 2 },
      { ageGroup: "11", students: 14 },
    ];
    expect(extension(uncovered, profile)).toEqual({ youngest: 9, label: "Add ages 9 to 11" });
  });

  it("offers nothing for an age above the youngest, or with no ages to extend", () => {
    const legacy: DivisionProfile = { ...profile, ageGroups: ["12", "13", "14"] };
    expect(extension([{ ageGroup: "15", students: 3 }], legacy)).toBeNull();
    expect(extension([{ ageGroup: "SNR", students: 3 }], legacy)).toBeNull();
    expect(extension([{ ageGroup: "11", students: 3 }], null)).toBeNull();
  });

  it("reads the youngest age through an open-ended code", () => {
    expect(youngestAge({ ...profile, ageGroups: ["14+"] })).toBe(14);
    expect(youngestAge({ ...profile, ageGroups: ["SNR"] })).toBeNull();
  });
});

describe("extendedText", () => {
  it("says what was added", () => {
    expect(extendedText(["11"], 52)).toBe("Age 11 added: 52 divisions.");
    expect(extendedText(["10", "11"], 1)).toBe("Ages 10 to 11 added: 1 division.");
    expect(extendedText(["11"], 0)).toBe("Age 11 added. No events needed new divisions.");
  });
});
