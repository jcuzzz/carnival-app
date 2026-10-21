import { describe, expect, it } from "vitest";
import { agesAsAt } from "./cutOff";

describe("agesAsAt", () => {
  it("names the day, the month and the competition year", () => {
    expect(agesAsAt("12-31", 2027)).toBe("31 December 2027");
    expect(agesAsAt("01-01", 2026)).toBe("1 January 2026");
  });

  it("moves a 29 February cut-off to the 28th in a common year, as the server does", () => {
    expect(agesAsAt("02-29", 2027)).toBe("28 February 2027");
    expect(agesAsAt("02-29", 2028)).toBe("29 February 2028");
  });
});
