import { describe, expect, it } from "vitest";
import { nextFieldsForMeasure, readDefinitionFields } from "./definitionFields";

/** The two pure halves both definition editors share (ADR-0053 §1, ADR-0056 §5). */

const fields = { attempts: "3", barStart: "1.00", barStep: "0.05", perHeight: "" };

const values = {
  name: "High Jump",
  code: "AT-HIGH-JUMP",
  units: "metres",
  ...fields,
};

describe("nextFieldsForMeasure", () => {
  it("seeds the shipped ladder and clears the count on Height", () => {
    expect(nextFieldsForMeasure({ ...fields, barStart: "", barStep: "" }, "Height")).toEqual({
      attempts: "",
      barStart: "1.20",
      barStep: "0.05",
      perHeight: "3",
    });
  });

  it("keeps the count only where the type owns it — the attempts type", () => {
    expect(nextFieldsForMeasure({ ...fields, perHeight: "2" }, "Distance")).toEqual({
      attempts: "3",
      barStart: "",
      barStep: "",
      perHeight: "",
    });
    expect(nextFieldsForMeasure(fields, "Count")).toMatchObject({ attempts: "3" });
    expect(nextFieldsForMeasure(fields, "Time")).toEqual({
      attempts: "",
      barStart: "",
      barStep: "",
      perHeight: "",
    });
  });
});

describe("readDefinitionFields", () => {
  it("reads a ladder, and never a count beside it", () => {
    expect(readDefinitionFields({ ...values, measureType: "Height" })).toEqual({
      attemptCount: null,
      ladder: { start: 1, step: 0.05 },
      attemptsPerHeight: 3,
      incompleteWithoutSport: false,
    });
  });

  it("reads the count on the attempts type alone", () => {
    const noLadder = { ...values, barStart: "", barStep: "" };
    expect(readDefinitionFields({ ...noLadder, measureType: "Distance" })).toMatchObject({
      attemptCount: 3,
      ladder: null,
    });
    expect(readDefinitionFields({ ...noLadder, measureType: "Count" })).toMatchObject({
      attemptCount: 3,
    });
    expect(readDefinitionFields({ ...noLadder, measureType: "Time" })).toMatchObject({
      attemptCount: null,
    });
    // A legacy Height definition's count is not this type's field any more.
    expect(readDefinitionFields({ ...noLadder, measureType: "Height" })).toMatchObject({
      attemptCount: null,
      ladder: null,
    });
  });

  /** The amendment: a bar ladder with no suggestions is a complete event. */
  it("takes both suggestions blank, with nothing else to say about it", () => {
    expect(
      readDefinitionFields({ ...values, measureType: "Height", barStart: "", barStep: "" }),
    ).toEqual({
      attemptCount: null,
      ladder: null,
      attemptsPerHeight: 3,
      incompleteWithoutSport: false,
    });
  });

  it("is incomplete on a blank field, an out-of-range count or a half-filled ladder", () => {
    const distance = { ...values, measureType: "Distance" as const, barStart: "", barStep: "" };
    expect(readDefinitionFields({ ...distance, name: " " }).incompleteWithoutSport).toBe(true);
    expect(readDefinitionFields({ ...distance, code: "" }).incompleteWithoutSport).toBe(true);
    expect(readDefinitionFields({ ...distance, units: "" }).incompleteWithoutSport).toBe(true);
    expect(readDefinitionFields({ ...distance, attempts: "7" }).incompleteWithoutSport).toBe(true);
    expect(
      readDefinitionFields({ ...values, measureType: "Height", barStep: "" }),
    ).toMatchObject({ ladder: null, incompleteWithoutSport: true });
  });

  // A count the server would refuse is still what the editor must compare
  // against what is stored, or a typed 7 reads as "unchanged" from a blank.
  it("keeps an out-of-range count readable while refusing the save", () => {
    const distance = { ...values, measureType: "Distance" as const, barStart: "", barStep: "" };
    expect(readDefinitionFields({ ...distance, attempts: "7" })).toMatchObject({
      attemptCount: 7,
      incompleteWithoutSport: true,
    });
  });
});

describe("attempts per height (ADR-0056 amendment 2026-09-25)", () => {
  it("reads 2 or 3 on a Height event, and nothing on any other", () => {
    expect(
      readDefinitionFields({ ...values, measureType: "Height", perHeight: "2" }).attemptsPerHeight,
    ).toBe(2);
    expect(
      readDefinitionFields({ ...values, measureType: "Height", perHeight: "3" }).attemptsPerHeight,
    ).toBe(3);
    expect(
      readDefinitionFields({ ...values, measureType: "Distance", perHeight: "2" })
        .attemptsPerHeight,
    ).toBeNull();
  });
});
