import type { MeasureType } from "../api/types";
import { formatSeconds } from "./timeFormat";

/** The canonical unit, never the definition's free-text `Units`. */
const UNIT_SUFFIX: Record<MeasureType, string> = {
  Time: "",
  Distance: " m",
  Height: " m",
  Count: "",
};

/** No unit, so a correction can be retyped straight from it. */
export function measureRaw(measureType: MeasureType, value: number): string {
  if (measureType === "Time") return formatSeconds(value);
  return measureType === "Count" ? String(Math.round(value)) : value.toFixed(2);
}

/** Mirrors the server's `EventLadder.FormatValue`. */
export function formatMeasure(measureType: MeasureType, value: number): string {
  return measureRaw(measureType, value) + UNIT_SUFFIX[measureType];
}

export function formatCapturedValue(measureType: MeasureType, raw: string): string {
  return raw + UNIT_SUFFIX[measureType];
}
