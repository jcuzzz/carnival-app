import { describe, expect, it } from "vitest";
import {
  measureCaption,
  measureFieldLabel,
  measureHint,
  readMarkField,
  readMeasureField,
  type FieldMeasureType,
} from "./markMask";

describe("readMarkField — centimetre digits (ADR-0052 §3)", () => {
  it.each([
    ["432", "4.32"],
    ["1052", "10.52"],
    ["135", "1.35"],
    ["4", "0.04"],
    ["43", "0.43"],
    ["0432", "4.32"],
    ["12345", "123.45"],
  ])("reads %s as %s m", (raw, value) => {
    const state = readMarkField(raw, "Distance");
    expect(state.value).toBe(value);
    expect(state.valid).toBe(true);
    expect(state.display).toBe(value);
    // The field keeps the keystrokes, not the reading (F1).
    expect(state.input).toBe(raw);
    expect(state.ghost).toBe("");
  });

  it.each([
    ["4.32", "4.32"],
    ["4.3", "4.30"],
    ["4.", "4.00"],
    ["10.5", "10.50"],
    [".5", "0.50"],
  ])("honours a typed dot: %s -> %s", (raw, value) => {
    expect(readMarkField(raw, "Distance").value).toBe(value);
  });

  it("echoes the typed dot and ghosts the hundredths it will supply", () => {
    expect(readMarkField("4.", "Distance")).toMatchObject({ input: "4.", ghost: "00" });
    expect(readMarkField("4.3", "Distance")).toMatchObject({ input: "4.3", ghost: "0" });
    expect(readMarkField("4.32", "Distance")).toMatchObject({ input: "4.32", ghost: "" });
    // The reading is what the caption says, so it is complete from the first dot.
    expect(readMarkField("4.", "Distance").display).toBe("4.00");
    expect(readMarkField("4.3", "Distance").display).toBe("4.30");
  });

  it("is empty and inert with nothing typed", () => {
    expect(readMarkField("", "Distance")).toMatchObject({
      mode: "empty",
      display: "",
      valid: false,
      error: null,
    });
  });

  it("blocks a zero mark quietly — no mark is NM, not 0.00", () => {
    const state = readMarkField("0", "Distance");
    expect(state.valid).toBe(false);
    expect(state.value).toBeNull();
    expect(state.error).toBeNull();
  });

  it("caps the whole metres at 999 on a distance, with a message", () => {
    const state = readMarkField("123456", "Distance");
    expect(state.error).toBe("Metres must be under 1000.");
    expect(state.valid).toBe(false);
    // The 999.99 under the cap still reads.
    expect(readMarkField("99999", "Distance").value).toBe("999.99");
  });

  it("caps the whole metres at 9 on a height, with a message", () => {
    expect(readMarkField("1234", "Height").error).toBe("Metres must be under 10.");
    expect(readMarkField("999", "Height").value).toBe("9.99");
    expect(readMarkField("135", "Height").value).toBe("1.35");
  });

  it("takes whole numbers only for a count", () => {
    expect(readMarkField("12", "Count")).toMatchObject({ value: "12", display: "12", ghost: "" });
    // A dot ENDS a count; the digits after it never glue on (F10).
    expect(readMarkField("1.2", "Count").value).toBe("1");
    expect(readMarkField("12.5", "Count")).toMatchObject({ value: "12", input: "12." });
    expect(readMarkField("007", "Count").value).toBe("7");
    expect(readMarkField("0", "Count").valid).toBe(false);
  });

  it("switches to status mode on letters, NM among them", () => {
    for (const code of ["DNS", "DNF", "DQ", "NM"] as const) {
      expect(readMarkField(code.toLowerCase(), "Distance")).toMatchObject({
        mode: "status",
        statusCode: code,
        valid: true,
      });
    }
    expect(readMarkField("N", "Distance")).toMatchObject({
      valid: false,
      error: "Type DNS, DNF, DQ or NM.",
    });
  });
});

/** One change event per keystroke, exactly as the controlled input fires them. */
function typeInto(measureType: FieldMeasureType, keys: string) {
  let state = readMarkField("", measureType);
  for (const key of keys) state = readMarkField(state.input + key, measureType);
  return state;
}

describe("readMarkField — typed one keystroke at a time (F1)", () => {
  it.each([
    ["Distance", "432", "4.32"],
    ["Distance", "1052", "10.52"],
    ["Distance", "4.32", "4.32"],
    ["Height", "135", "1.35"],
  ] as const)("%s: typing %s reaches %s", (measureType, keys, value) => {
    expect(typeInto(measureType, keys).value).toBe(value);
  });

  it("never glues a count's digits across a typed dot", () => {
    expect(typeInto("Count", "12").value).toBe("12");
    expect(typeInto("Count", "12.5").value).toBe("12");
  });

  it("keeps the caption's reading in step with the keystrokes", () => {
    expect(measureCaption("Distance", typeInto("Distance", "432"))).toBe("reads 4.32 m");
    expect(measureCaption("Height", typeInto("Height", "1"))).toBe("reads 0.01 m");
    expect(measureCaption("Distance", typeInto("Distance", "12345"))).toBe("reads 123.45 m");
    expect(measureCaption("Distance", typeInto("Distance", "123456"))).toBe(
      "Metres must be under 1000.",
    );
    expect(measureCaption("Count", typeInto("Count", "12"))).toBe("whole number");
    expect(measureCaption("Time", readMeasureField("Time", "123"))).toBe(
      "123 reads 1:23 — type . for hundredths",
    );
  });
});

describe("readMeasureField — one reader, the event's measure", () => {
  it("leaves the time mask exactly as it was", () => {
    expect(readMeasureField("Time", "123")).toMatchObject({
      mode: "value",
      value: "1:23.00",
      // The time mask's echo IS its reading, so the field still holds it.
      input: "1:23",
      display: "1:23",
    });
    expect(readMeasureField("Time", "1:60")).toMatchObject({ error: "Seconds must be under 60." });
  });

  it("refuses NM on a time event — a runner who starts and stops is a DNF", () => {
    expect(readMeasureField("Time", "NM")).toMatchObject({
      statusCode: null,
      valid: false,
      error: "Type DNS, DNF or DQ.",
    });
    expect(readMeasureField("Distance", "NM")).toMatchObject({ statusCode: "NM", valid: true });
  });

  it("names the field and its rule in the official's own words", () => {
    expect(measureFieldLabel("Time", "Sam Swimmer")).toBe("Time for Sam Swimmer");
    expect(measureFieldLabel("Distance", "Sam Swimmer")).toBe("Mark for Sam Swimmer");
    expect(measureFieldLabel("Count", "Sam Swimmer")).toBe("Count for Sam Swimmer");
    expect(measureHint("Height")).toBe("432 reads 4.32 m");
    expect(measureHint("Count")).toBe("whole number");
  });
});
