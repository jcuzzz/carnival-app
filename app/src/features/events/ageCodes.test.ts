import { describe, expect, it } from "vitest";
import {
  ageNumber,
  codesSummary,
  isOpenEnded,
  nonNumericCodes,
  numericCodes,
  orderCodes,
  rangeCodes,
} from "./ageCodes";

/**
 * The editor never types a code, so what these have to get right is the phrase a set of
 * ages READS as: a gapped {13, 15} must never be shown as a range 14s are not in.
 */

describe("ageNumber and isOpenEnded", () => {
  it("reads plain and open-ended ages, and nothing else", () => {
    expect(ageNumber("12")).toBe(12);
    expect(ageNumber("18+")).toBe(18);
    expect(ageNumber("SNR")).toBeNull();
    expect(ageNumber("+18")).toBeNull();
    expect(isOpenEnded("18+")).toBe(true);
    expect(isOpenEnded("18")).toBe(false);
  });
});

describe("orderCodes", () => {
  it("puts the numeric codes in ascending order, not string order", () => {
    expect(orderCodes(["13", "9", "10"])).toEqual(["9", "10", "13"]);
  });

  it("puts the open-ended oldest age after 17, and right after a plain 18", () => {
    expect(orderCodes(["18+", "9", "17", "12"])).toEqual(["9", "12", "17", "18+"]);
    expect(orderCodes(["19", "18+", "18"])).toEqual(["18", "18+", "19"]);
  });

  it("keeps non-numeric codes after the ages, as the server's key order does", () => {
    expect(orderCodes(["SNR", "12", "JNR", "18+"])).toEqual(["12", "18+", "JNR", "SNR"]);
  });

  it("drops blanks and duplicates", () => {
    expect(orderCodes([" 12 ", "12", "", "  "])).toEqual(["12"]);
  });
});

describe("numericCodes and nonNumericCodes", () => {
  it("splits ages from other codes", () => {
    expect(numericCodes(["12", "SNR", "18+", "13"])).toEqual(["12", "13", "18+"]);
    expect(nonNumericCodes(["12", "SNR", "JNR"])).toEqual(["JNR", "SNR"]);
    expect(nonNumericCodes(["12", "13+"])).toEqual([]);
  });
});

describe("rangeCodes", () => {
  it("makes every age plain except the oldest, which takes everyone older", () => {
    expect(rangeCodes(12, 18)).toEqual(["12", "13", "14", "15", "16", "17", "18+"]);
    expect(rangeCodes(10, 10)).toEqual(["10+"]);
    expect(rangeCodes(13, 12)).toEqual([]);
  });
});

describe("codesSummary", () => {
  it("reads the run as a range and still names the codes outside it", () => {
    expect(codesSummary(["12", "13", "14", "15", "16", "17", "18", "SNR"])).toBe("12–18, SNR");
    expect(codesSummary(["13"])).toBe("13");
    expect(codesSummary(["SNR"])).toBe("SNR");
    expect(codesSummary([])).toBe("");
  });

  it("ends a run on the open-ended oldest age", () => {
    expect(codesSummary(rangeCodes(12, 18))).toBe("12–18+");
    expect(codesSummary(rangeCodes(12, 18), " to ")).toBe("12 to 18+");
    expect(codesSummary(["18+"])).toBe("18+");
  });

  it("lists the ages rather than claiming a range they do not fill", () => {
    expect(codesSummary(["12", "14", "SNR"])).toBe("12, 14, SNR");
    expect(codesSummary(["12", "13+", "14"])).toBe("12, 13+, 14");
  });
});
