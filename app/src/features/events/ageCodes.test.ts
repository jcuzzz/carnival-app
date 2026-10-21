import { describe, expect, it } from "vitest";
import {
  codeRange,
  codesSummary,
  FALLBACK_AGE_CODES,
  nonNumericCodes,
  numericCodes,
  orderCodes,
} from "./ageCodes";

/**
 * The editor never types a code (ADR-0057 §3, ADR-0058 §4), so what these have
 * to get right is the phrase a picked set READS as — a gapped {13, 15} must
 * never be shown as a range 14s are not in.
 */

describe("orderCodes", () => {
  it("puts the numeric codes in ascending order, not string order", () => {
    expect(orderCodes(["13", "9", "10"])).toEqual(["9", "10", "13"]);
  });

  it("keeps non-numeric codes after the numbers, as the server's key order does", () => {
    expect(orderCodes(["SNR", "12", "JNR"])).toEqual(["12", "JNR", "SNR"]);
  });

  it("drops blanks and duplicates", () => {
    expect(orderCodes([" 12 ", "12", "", "  "])).toEqual(["12"]);
  });
});

describe("numericCodes and codeRange", () => {
  it("offers only the numeric codes to the selects", () => {
    expect(numericCodes(["12", "SNR", "13"])).toEqual(["12", "13"]);
  });

  it("names the rest separately, because a from/to range cannot reach them", () => {
    expect(nonNumericCodes(["12", "SNR", "JNR"])).toEqual(["JNR", "SNR"]);
    expect(nonNumericCodes(["12", "13"])).toEqual([]);
  });

  it("takes every code in the range, inclusive of both ends", () => {
    expect(codeRange(["12", "13", "14", "15"], "13", "15")).toEqual(["13", "14", "15"]);
    expect(codeRange(["12", "13", "14"], "13", "13")).toEqual(["13"]);
  });

  it("is empty for a reversed or non-numeric range", () => {
    expect(codeRange(["12", "13"], "13", "12")).toEqual([]);
    expect(codeRange(["12", "13"], "SNR", "13")).toEqual([]);
  });

  it("falls back to 5–21 when the roster names no ages", () => {
    expect(FALLBACK_AGE_CODES[0]).toBe("5");
    expect(FALLBACK_AGE_CODES.at(-1)).toBe("21");
    expect(codesSummary(FALLBACK_AGE_CODES)).toBe("5–21");
  });
});

describe("codesSummary", () => {
  it("reads the run as a range and still names the codes outside it", () => {
    expect(codesSummary(["12", "13", "14", "15", "16", "17", "18", "SNR"])).toBe("12–18, SNR");
    expect(codesSummary(["13"])).toBe("13");
    expect(codesSummary(["SNR"])).toBe("SNR");
    expect(codesSummary([])).toBe("");
  });

  it("lists the numbers rather than claiming a range they do not fill", () => {
    expect(codesSummary(["12", "14", "SNR"])).toBe("12, 14, SNR");
  });
});
