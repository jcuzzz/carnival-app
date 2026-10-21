/**
 * Digits read as centimetres from the right (`432` -> 4.32 m); a typed `.` fixes the point
 * instead. The server re-parses and is authoritative.
 */

import type { MeasureType } from "../api/types";
import { formatCapturedValue } from "./measureFormat";
import { readTimeField } from "./timeMask";

export type MeasureStatusCode = "DNS" | "DNF" | "DQ" | "NM";
export type MeasureFieldMode = "empty" | "value" | "status";

export interface MeasureFieldState {
  mode: MeasureFieldMode;
  input: string;
  display: string;
  ghost: string;
  /** Only when valid. */
  value: string | null;
  statusCode: MeasureStatusCode | null;
  valid: boolean;
  error: string | null;
}

export type FieldMeasureType = Exclude<MeasureType, "Time">;

const FRACTION_DIGITS = 2;
const MAX_COUNT_DIGITS = 6;

const WHOLE_CAP: Record<"Distance" | "Height", number> = { Distance: 999, Height: 9 };

const EMPTY: MeasureFieldState = {
  mode: "empty",
  input: "",
  display: "",
  ghost: "",
  value: null,
  statusCode: null,
  valid: false,
  error: null,
};

const FIELD_STATUS: readonly MeasureStatusCode[] = ["DNS", "DNF", "DQ", "NM"];

export function readMarkField(input: string, measureType: FieldMeasureType): MeasureFieldState {
  if (/[a-z]/i.test(input)) {
    const letters = input.replace(/[^a-z]/gi, "").toUpperCase().slice(0, 3);
    const statusCode = FIELD_STATUS.find((code) => code === letters) ?? null;
    return {
      mode: "status",
      input: letters,
      display: letters,
      ghost: "",
      value: null,
      statusCode,
      valid: statusCode !== null,
      error: statusCode === null ? "Type DNS, DNF, DQ or NM." : null,
    };
  }
  return measureType === "Count" ? readCount(input) : readMetres(input, measureType);
}

function readCount(input: string): MeasureFieldState {
  // A dot ends the count (12.5 is 12, not 125) and stays in the echo so later digits fall away.
  const dot = input.indexOf(".");
  const digits = (dot === -1 ? input : input.slice(0, dot))
    .replace(/\D/g, "")
    .slice(0, MAX_COUNT_DIGITS);
  if (digits === "") return EMPTY;
  const display = String(Number(digits));
  const valid = Number(digits) > 0;
  return {
    mode: "value",
    input: digits + (dot === -1 ? "" : "."),
    display,
    ghost: "",
    value: valid ? display : null,
    statusCode: null,
    valid,
    error: null,
  };
}

function readMetres(input: string, measureType: "Distance" | "Height"): MeasureFieldState {
  const cap = WHOLE_CAP[measureType];
  // One digit past the cap is typeable so the block has something to say.
  const maxWholeDigits = String(cap).length + 1;
  let whole = "";
  let fraction: string | null = null;
  for (const ch of input) {
    if (ch >= "0" && ch <= "9") {
      if (fraction !== null) {
        if (fraction.length < FRACTION_DIGITS) fraction += ch;
      } else if (whole.length < maxWholeDigits + FRACTION_DIGITS) {
        whole += ch;
      }
      continue;
    }
    if (ch === "." && fraction === null) {
      whole = whole.slice(0, maxWholeDigits);
      fraction = "";
    }
  }
  if (whole === "" && fraction === null) return EMPTY;

  // No dot typed: the digits are centimetres, so the last two are the fraction.
  const positional = fraction === null;
  const padded = positional ? whole.padStart(FRACTION_DIGITS + 1, "0") : whole;
  const wholeDigits = positional ? padded.slice(0, -FRACTION_DIGITS) : padded;
  const fractionDigits = positional ? padded.slice(-FRACTION_DIGITS) : (fraction ?? "");
  const metres = wholeDigits.replace(/^0+/, "");
  const wholeNumber = metres === "" ? 0 : Number(metres);
  const hundredths = fractionDigits.padEnd(FRACTION_DIGITS, "0");
  const typed = whole + (positional ? "" : `.${fraction}`);
  const display = `${wholeNumber}.${hundredths}`;
  const ghost = positional ? "" : "0".repeat(FRACTION_DIGITS - fractionDigits.length);
  const blocked = (error: string | null): MeasureFieldState => ({
    mode: "value",
    input: typed,
    display,
    ghost,
    value: null,
    statusCode: null,
    valid: false,
    error,
  });

  if (wholeNumber > cap) return blocked(`Metres must be under ${cap + 1}.`);
  if (wholeNumber === 0 && Number(hundredths) === 0) return blocked(null);
  return {
    mode: "value",
    input: typed,
    display,
    ghost,
    value: display,
    statusCode: null,
    valid: true,
    error: null,
  };
}

export function readMeasureField(measureType: MeasureType, input: string): MeasureFieldState {
  if (measureType !== "Time") return readMarkField(input, measureType);
  const time = readTimeField(input);
  return {
    mode: time.mode === "time" ? "value" : time.mode,
    input: time.display,
    display: time.display,
    ghost: time.ghost,
    value: time.timeValue,
    statusCode: time.statusCode,
    valid: time.valid,
    error: time.error,
  };
}

/** A high-jump bar is a height the whole division jumps at, not one athlete's mark. */
export type MeasureVariant = "bar";

export function measureNoun(measureType: MeasureType, variant?: MeasureVariant): string {
  if (variant === "bar") return "height";
  if (measureType === "Time") return "time";
  return measureType === "Count" ? "count" : "mark";
}

export function measureFieldLabel(measureType: MeasureType, name: string): string {
  const noun = measureNoun(measureType);
  return `${noun[0]!.toUpperCase()}${noun.slice(1)} for ${name}`;
}

export function measureHint(measureType: MeasureType, variant?: MeasureVariant): string {
  if (variant === "bar") return "135 reads 1.35 m";
  if (measureType === "Time") return "123 reads 1:23 — type . for hundredths";
  return measureType === "Count" ? "whole number" : "432 reads 4.32 m";
}

export function measureCaption(
  measureType: MeasureType,
  state: MeasureFieldState,
  variant?: MeasureVariant,
): string {
  if (state.error !== null) return state.error;
  if (state.mode === "value" && (measureType === "Distance" || measureType === "Height"))
    return `reads ${formatCapturedValue(measureType, state.display)}`;
  return measureHint(measureType, variant);
}
