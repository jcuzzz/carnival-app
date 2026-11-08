import { describe, expect, it } from "vitest";
import type { CarnivalEvent, Schedule } from "../../api/types";
import { readiness, type ReadinessFacts } from "./readiness";

const texts = (facts: ReadinessFacts) => readiness(facts).warnings.map((w) => w.text);

/** ADR-0066 §5: Ready to open warns from what Manage already reads, and never blocks. */

function division(
  id: string,
  ageGroup: string,
  scheduleSlotId: string | null = null,
  eventDefinitionId = "def-1",
): CarnivalEvent {
  return { id, ageGroup, scheduleSlotId, eventDefinitionId, status: "Scheduled" } as CarnivalEvent;
}

const program: Schedule = {
  carnivalId: "c-1",
  streams: [{ id: "s-1", name: "Pool", sortOrder: 0, slots: [{ id: "sl-1" }] }],
} as Schedule;

const ready: ReadinessFacts = {
  carnival: { date: "2027-03-06" },
  events: [division("e-1", "12", "sl-1"), division("e-2", "13", "sl-1", "def-2")],
  rosterAgeGroups: ["12", "13"],
  rosterYear: 2027,
  schedule: program,
};

describe("readiness", () => {
  it("raises nothing for a carnival that is ready, and counts what it holds", () => {
    expect(readiness(ready)).toEqual({ warnings: [], summary: "2 events · 2 divisions" });
  });

  it("warns when the roster's year is not the carnival's", () => {
    expect(readiness({ ...ready, rosterYear: 2026 }).warnings).toEqual([
      {
        id: "rosterYear",
        text: "The student list’s ages are for 2026, but this carnival is in 2027.",
      },
    ]);
  });

  it("says nothing about the year with no roster, or before the settings land", () => {
    expect(readiness({ ...ready, rosterYear: null }).warnings).toEqual([]);
    expect(readiness({ ...ready, rosterYear: undefined }).warnings).toEqual([]);
  });

  it("counts divisions whose age no current student has, naming the ages", () => {
    const facts = {
      ...ready,
      events: [
        ...ready.events!,
        division("e-3", "11", "sl-1"),
        division("e-4", "19", "sl-1"),
        division("e-5", "SNR", "sl-1"),
        division("e-6", "11", "sl-1"),
      ],
    };
    expect(readiness(facts).warnings).toEqual([
      {
        id: "unmatchedAges",
        text: "4 divisions have an age no current student has (ages 11, 19, SNR).",
      },
    ]);
  });

  it("never counts the all-ages division as unmatched", () => {
    const facts = { ...ready, events: [...ready.events!, division("e-3", "OPEN", "sl-1")] };
    expect(readiness(facts).warnings).toEqual([]);
  });

  it("names a single unmatched age in the singular", () => {
    const facts = { ...ready, events: [...ready.events!, division("e-3", "14", "sl-1")] };
    expect(texts(facts)).toEqual(["1 division has an age no current student has (age 14)."]);
  });

  it("leaves the ages to the Students step when there is no roster", () => {
    const facts = { ...ready, rosterAgeGroups: [], rosterYear: null };
    expect(readiness(facts).warnings).toEqual([]);
  });

  it("skips the age check while the settings are unread", () => {
    const facts = {
      ...ready,
      rosterAgeGroups: undefined,
      events: [...ready.events!, division("e-3", "14", "sl-1")],
    };
    expect(readiness(facts).warnings).toEqual([]);
  });

  it("counts divisions outside the program when there is one", () => {
    const facts = {
      ...ready,
      events: [...ready.events!, division("e-3", "12"), division("e-4", "13")],
    };
    expect(readiness(facts).warnings).toEqual([
      {
        id: "outsideProgram",
        text:
          "2 divisions aren’t in the program, so they won’t appear on the printed running " +
          "order. Leave them if they run separately.",
        action: "Add them to the program",
      },
    ]);
  });

  it("says what one division outside the program means, in the singular", () => {
    const facts = { ...ready, events: [...ready.events!, division("e-3", "12")] };
    const [warning] = readiness(facts).warnings;
    expect(warning?.text).toBe(
      "1 division isn’t in the program, so it won’t appear on the printed running order. " +
        "Leave it if it runs separately.",
    );
    expect(warning?.action).toBe("Add it to the program");
  });

  it("does not ask for a program the carnival has not built", () => {
    const unslotted = [division("e-1", "12"), division("e-2", "13")];
    for (const schedule of [{ carnivalId: "c-1", streams: [] }, null, undefined]) {
      expect(readiness({ ...ready, events: unslotted, schedule }).warnings).toEqual([]);
    }
  });

  it("warns when there are no events yet, with no count line", () => {
    expect(readiness({ ...ready, events: [] })).toEqual({
      warnings: [{ id: "noEvents", text: "No events yet." }],
      summary: null,
    });
  });

  it("claims nothing about events before the list lands", () => {
    expect(readiness({ ...ready, events: undefined })).toEqual({ warnings: [], summary: null });
  });
});
