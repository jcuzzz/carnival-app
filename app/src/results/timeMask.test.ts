import { describe, expect, it } from "vitest";
import { readTimeField } from "./timeMask";
import { parseSwimTime } from "./timeFormat";

describe("readTimeField — positional digits (ADR-0041 §1, owner ruling 2026-08-31)", () => {
  it.each([
    ["35", "35.00"],
    ["5", "5.00"],
    ["123", "1:23.00"],
    ["1234", "12:34.00"],
    ["123.23", "1:23.23"],
    ["9.5", "9.50"],
    ["35.", "35.00"],
    ["34.56", "34.56"],
    ["05", "5.00"],
    ["035", "35.00"],
    ["0035", "35.00"],
    ["5959", "59:59.00"],
    ["1:02", "1:02.00"],
    ["1:02.5", "1:02.50"],
    ["0:35", "35.00"],
    ["12:34.56", "12:34.56"],
  ])("reads %s as %s", (raw, value) => {
    const state = readTimeField(raw);
    expect(state.timeValue).toBe(value);
    expect(state.valid).toBe(true);
    expect(parseSwimTime(value).ok).toBe(true);
  });

  it("echoes the positional reading and ghosts only the hundredths it supplies", () => {
    expect(readTimeField("1")).toMatchObject({ display: "1", ghost: ".00" });
    expect(readTimeField("12")).toMatchObject({ display: "12", ghost: ".00" });
    expect(readTimeField("123")).toMatchObject({ display: "1:23", ghost: ".00" });
    expect(readTimeField("1234")).toMatchObject({ display: "12:34", ghost: ".00" });
    expect(readTimeField("123.2")).toMatchObject({ display: "1:23.2", ghost: "0" });
    expect(readTimeField("35.")).toMatchObject({ display: "35.", ghost: "00" });
    expect(readTimeField("35.12")).toMatchObject({ display: "35.12", ghost: "" });
    expect(readTimeField("1:02")).toMatchObject({ display: "1:02", ghost: ".00" });
  });

  it("is empty and inert with nothing typed", () => {
    const state = readTimeField("");
    expect(state.mode).toBe("empty");
    expect(state.display).toBe("");
    expect(state.ghost).toBe("");
    expect(state.valid).toBe(false);
    expect(state.error).toBeNull();
  });

  it.each([":", ".", "1:", ".5", "1:2"])(
    "blocks %s quietly — present, but not yet a time",
    (raw) => {
      const state = readTimeField(raw);
      expect(state.valid).toBe(false);
      expect(state.timeValue).toBeNull();
      expect(state.error).toBeNull();
    },
  );

  it("shows a half-typed colon time literally", () => {
    expect(readTimeField("1:2").display).toBe("1:2");
  });

  it("blocks a zero time quietly (no scary error on a fresh field)", () => {
    for (const raw of ["0", "00", "000", "0.00", "0:00.00"]) {
      const state = readTimeField(raw);
      expect(state.valid).toBe(false);
      expect(state.error).toBeNull();
    }
  });

  it.each([
    ["75", "75"],
    ["175", "1:75"],
    ["1:75", "1:75"],
  ])("refuses %s — seconds >= 60 are never carried into minutes", (raw, display) => {
    const state = readTimeField(raw);
    expect(state.display).toBe(display); // literal, NOT normalised to 1:15 / 2:15
    expect(state.valid).toBe(false);
    expect(state.error).toContain("under 60");
    expect(state.timeValue).toBeNull();
  });

  it("ignores junk and the keystrokes past each part's width", () => {
    expect(readTimeField("3a5").mode).toBe("status"); // a letter is a status code
    expect(readTimeField("3-5,1").timeValue).toBe("3:51.00");
    expect(readTimeField("12345").timeValue).toBe("12:34.00"); // capped at 99:59
    expect(readTimeField("1:234").timeValue).toBe("1:23.00");
    expect(readTimeField("35.123").timeValue).toBe("35.12");
    expect(readTimeField("1:2:3").timeValue).toBe("1:23.00"); // one colon only
    expect(readTimeField("35.1.2").timeValue).toBe("35.12"); // one dot only
  });
});

describe("readTimeField — status shortcut", () => {
  it.each(["dns", "DNF", "dq", "Dns"])("recognises %s", (raw) => {
    const state = readTimeField(raw);
    expect(state.mode).toBe("status");
    expect(state.statusCode).toBe(raw.toUpperCase());
    expect(state.valid).toBe(true);
  });

  it("shows guidance for an incomplete/unknown code", () => {
    const state = readTimeField("d");
    expect(state.mode).toBe("status");
    expect(state.statusCode).toBeNull();
    expect(state.valid).toBe(false);
    expect(state.error).toContain("DNS");
  });
});
