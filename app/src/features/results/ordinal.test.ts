import { describe, expect, it } from "vitest";
import { ordinal } from "./ordinal";

describe("ordinal", () => {
  it("suffixes the ones digit", () => {
    expect([1, 2, 3, 4, 5, 9].map(ordinal)).toEqual(["1st", "2nd", "3rd", "4th", "5th", "9th"]);
  });

  it("gives the teens 'th'", () => {
    expect([11, 12, 13, 14].map(ordinal)).toEqual(["11th", "12th", "13th", "14th"]);
  });

  it("resumes past the teens", () => {
    expect([21, 22, 23, 24, 101, 111].map(ordinal)).toEqual([
      "21st",
      "22nd",
      "23rd",
      "24th",
      "101st",
      "111th",
    ]);
  });
});
