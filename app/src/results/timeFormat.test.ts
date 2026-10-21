import { describe, expect, it } from "vitest";
import { formatSeconds, parseSwimTime } from "./timeFormat";

// Mirrors the server's ResultValueParser rules for Time (mm:ss.ff / ss.ff).
describe("parseSwimTime", () => {
  it.each([
    ["1:23.45", 83.45],
    ["0:59.9", 59.9],
    ["34.56", 34.56],
    ["34", 34],
    ["12:03.125", 723.125],
  ])("accepts %s as %d seconds", (raw, seconds) => {
    const parsed = parseSwimTime(raw);
    expect(parsed.ok).toBe(true);
    if (parsed.ok) expect(parsed.value.seconds).toBeCloseTo(seconds, 5);
  });

  it.each([
    "", // empty
    "1:63.45", // seconds must be < 60 when minutes given
    "1:2.45", // seconds must be two digits with minutes
    "abc",
    "-3.2",
    "1:23.4567", // fraction max 3 digits
    "0", // must be > 0
    "0:00.00",
  ])("rejects '%s'", (raw) => {
    expect(parseSwimTime(raw).ok).toBe(false);
  });

  it("returns a helpful message for bad input", () => {
    const parsed = parseSwimTime("splash");
    expect(parsed.ok).toBe(false);
    if (!parsed.ok) expect(parsed.error).toContain("mm:ss.ff");
  });
});

describe("formatSeconds", () => {
  it.each([
    [83.45, "1:23.45"],
    [34.56, "34.56"],
    [60, "1:00.00"],
    [59.9, "59.90"],
    [603.5, "10:03.50"],
  ])("formats %d as %s", (seconds, expected) => {
    expect(formatSeconds(seconds)).toBe(expected);
  });
});
