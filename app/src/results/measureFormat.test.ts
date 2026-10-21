import { describe, expect, it } from "vitest";
import { formatCapturedValue, formatMeasure, measureRaw } from "./measureFormat";

describe("formatMeasure — the per-kind formatter (ADR-0052 §1)", () => {
  it("formats a time exactly as it always did", () => {
    expect(formatMeasure("Time", 34.56)).toBe("34.56");
    expect(formatMeasure("Time", 83.45)).toBe("1:23.45");
  });

  it("prints a distance and a height in metres, to the centimetre", () => {
    expect(formatMeasure("Distance", 10.52)).toBe("10.52 m");
    expect(formatMeasure("Distance", 4.3)).toBe("4.30 m");
    expect(formatMeasure("Height", 1.35)).toBe("1.35 m");
  });

  it("prints a count whole", () => {
    expect(formatMeasure("Count", 12)).toBe("12");
    expect(formatMeasure("Count", 12.0)).toBe("12");
  });

  it("hands the mask a value with no unit, so a correction can be retyped", () => {
    expect(measureRaw("Distance", 4.32)).toBe("4.32");
    expect(measureRaw("Count", 12)).toBe("12");
    expect(measureRaw("Time", 83.45)).toBe("1:23.45");
  });

  it("dresses a captured string the same way the recorded one is dressed", () => {
    expect(formatCapturedValue("Distance", "4.32")).toBe("4.32 m");
    expect(formatCapturedValue("Time", "1:23.45")).toBe("1:23.45");
    expect(formatCapturedValue("Count", "12")).toBe("12");
  });
});
