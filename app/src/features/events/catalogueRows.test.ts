import { describe, expect, it } from "vitest";
import type { EventDefinition } from "../../api/types";
import { definitionSummary } from "./catalogueRows";

/** The one line under a catalogue row: what capture this definition configures. */
type Summarised = Pick<
  EventDefinition,
  "code" | "measureType" | "units" | "attemptCount" | "ladder"
>;

const row = (over: Partial<Summarised>): Summarised => ({
  code: "SW-50-FREE",
  measureType: "Time",
  units: "seconds",
  attemptCount: null,
  ladder: null,
  ...over,
});

describe("definitionSummary", () => {
  it("says the code and the type, and nothing it has nothing to say about", () => {
    expect(definitionSummary(row({}))).toBe("SW-50-FREE · Timed (seconds)");
  });

  it("folds the attempt series into the type on a field definition", () => {
    expect(
      definitionSummary(
        row({ code: "AT-LONG-JUMP", measureType: "Distance", units: "metres", attemptCount: 3 }),
      ),
    ).toBe("AT-LONG-JUMP · Best of 3 attempts (metres)");
  });

  /** The amendment: the type says bar ladder; the numbers are suggestions. */
  it("names the bar ladder, and its suggestions where a definition carries them", () => {
    expect(definitionSummary(row({ code: "AT-HIGH-JUMP", measureType: "Height", units: "metres" }))).toBe(
      "AT-HIGH-JUMP · Bar ladder (metres)",
    );
    expect(
      definitionSummary(
        row({
          code: "AT-HIGH-JUMP",
          measureType: "Height",
          units: "metres",
          ladder: { start: 1.2, step: 0.05 },
        }),
      ),
    ).toBe("AT-HIGH-JUMP · Bar ladder (metres) · suggests 1.20 m, +0.05 m");
  });
});
