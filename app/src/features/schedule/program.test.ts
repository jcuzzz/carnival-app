import { describe, expect, it } from "vitest";
import type { CarnivalEvent, Schedule } from "../../api/types";
import { buildProgram, slotTime } from "./program";

/**
 * The printed program (ADR-0014 §2). Streams become COLUMNS, and the two real
 * cases are asserted directly: swimming's one stream (one column, a long
 * time-ordered list) and athletics' six concurrent streams (track beside five
 * field sites).
 *
 * The division text is the other half: it comes from the shared label helpers,
 * so every division reads exactly as it does on the deck — and a Social division
 * must not be mistakable for its Competitive twin.
 */

function event(over: Partial<CarnivalEvent> & Pick<CarnivalEvent, "id">): CarnivalEvent {
  return {
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    definitionCode: "SW-50-FREE",
    definitionName: "Freestyle 50m",
    ageGroup: "13",
    category: "F",
    tier: "Competitive",
    scoringMode: null,
    effectiveScoringMode: "Places",
    scoresPoints: true,
    scheduleSlotId: "slot-free",
    slotOrder: 0,
    status: "Open",
    version: 1,
    entryCount: 0,
    resultCount: 0,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

function slot(over: Partial<Schedule["streams"][number]["slots"][number]> & { id: string }) {
  return {
    streamId: "pool",
    sortOrder: 0,
    startTime: null,
    endTime: null,
    kind: "Events" as const,
    label: "BLOCK",
    adjustmentCategory: null,
    eventIds: [],
    ...over,
  };
}

describe("slotTime", () => {
  it("prints a start alone, and a window where the organiser set one", () => {
    // The pool runs start times; the field sites run windows ("LONG JUMP
    // 10:00–12:00") — both shapes are in the school's own programs.
    expect(slotTime({ startTime: "09:45:00", endTime: null })).toBe("09:45");
    expect(slotTime({ startTime: "10:00:00", endTime: "11:00:00" })).toBe("10:00–11:00");
  });

  it("is null for an untimed slot rather than inventing a time", () => {
    expect(slotTime({ startTime: null, endTime: null })).toBeNull();
  });
});

describe("buildProgram", () => {
  it("lays swimming's single stream out as one column of time-ordered slots", () => {
    const schedule: Schedule = {
      carnivalId: "c-1",
      streams: [
        {
          id: "pool",
          name: "Pool",
          sortOrder: 0,
          slots: [
            slot({ id: "slot-free", startTime: "09:45:00", label: "FREESTYLE EVENTS", eventIds: ["free-13"] }),
            slot({
              id: "slot-novelty",
              sortOrder: 1,
              startTime: "11:00:00",
              kind: "Item",
              label: "Staff v student relay",
              adjustmentCategory: "Novelty",
            }),
          ],
        },
      ],
    };

    const columns = buildProgram(schedule, [event({ id: "free-13" })]);
    expect(columns).toHaveLength(1);
    expect(columns[0]!.name).toBe("Pool");
    expect(columns[0]!.slots.map((s) => [s.time, s.label])).toEqual([
      ["09:45", "FREESTYLE EVENTS"],
      ["11:00", "Staff v student relay"],
    ]);
    // A non-competition item holds no events and keeps its ADR-0005 link.
    expect(columns[0]!.slots[1]!.entries).toEqual([]);
    expect(columns[0]!.slots[1]!.adjustmentCategory).toBe("Novelty");
  });

  it("gives athletics one column per concurrent stream, in the server's order", () => {
    const names = ["Track", "Long Jump", "Shot Put", "Discus", "High Jump", "Triple Jump"];
    const schedule: Schedule = {
      carnivalId: "c-1",
      streams: names.map((name, i) => ({
        id: `stream-${i}`,
        name,
        sortOrder: i,
        slots: [slot({ id: `slot-${i}`, streamId: `stream-${i}`, startTime: "10:00:00" })],
      })),
    };
    expect(buildProgram(schedule, []).map((c) => c.name)).toEqual(names);
  });

  it("prints one line per definition, listing every division it covers", () => {
    // The M/F split is real (separate instances, ADR-0014 §5) but the program
    // does not print one line per instance — it prints the block and its
    // divisions, which is what the school's document does.
    const schedule: Schedule = {
      carnivalId: "c-1",
      streams: [
        {
          id: "pool",
          name: "Pool",
          sortOrder: 0,
          slots: [
            slot({
              id: "slot-free",
              label: "FREESTYLE EVENTS",
              eventIds: ["free-13-m", "free-13-f", "back-13-m"],
            }),
          ],
        },
      ],
    };
    const entries = buildProgram(schedule, [
      event({ id: "free-13-m", category: "M" }),
      event({ id: "free-13-f", category: "F" }),
      event({
        id: "back-13-m",
        eventDefinitionId: "def-back",
        definitionName: "Backstroke 50m",
        category: "M",
      }),
    ])[0]!.slots[0]!.entries;

    expect(entries.map((e) => e.name)).toEqual(["Freestyle 50m", "Backstroke 50m"]);
    expect(entries[0]!.divisions).toEqual(["13 · Boys", "13 · Girls"]);
  });

  it("distinguishes a Social twin from the Competitive division beside it", () => {
    const schedule: Schedule = {
      carnivalId: "c-1",
      streams: [
        {
          id: "pool",
          name: "Pool",
          sortOrder: 0,
          slots: [slot({ id: "slot-1", eventIds: ["comp", "social"] })],
        },
      ],
    };
    const entries = buildProgram(schedule, [
      event({ id: "comp", ageGroup: "15" }),
      event({
        id: "social",
        ageGroup: "15",
        tier: "Social",
        effectiveScoringMode: "Social",
      }),
    ])[0]!.slots[0]!.entries;

    expect(entries[0]!.divisions).toEqual(["15 · Girls", "15 · Girls · Social"]);
  });

  it("prints a non-numeric age exactly as the roster derives it", () => {
    const schedule: Schedule = {
      carnivalId: "c-1",
      streams: [
        { id: "pool", name: "Pool", sortOrder: 0, slots: [slot({ id: "s", eventIds: ["e"] })] },
      ],
    };
    const entries = buildProgram(schedule, [
      event({ id: "e", ageGroup: "SNR" }),
    ])[0]!.slots[0]!.entries;
    expect(entries[0]!.divisions).toEqual(["SNR · Girls"]);
  });

  it("skips an event the list does not carry rather than printing a placeholder", () => {
    // Only reachable with a half-refreshed cache. A line reading "unknown event"
    // on a program handed to staff is worse than the line not being there.
    const schedule: Schedule = {
      carnivalId: "c-1",
      streams: [
        {
          id: "pool",
          name: "Pool",
          sortOrder: 0,
          slots: [slot({ id: "s", eventIds: ["free-13", "not-in-the-list"] })],
        },
      ],
    };
    const entries = buildProgram(schedule, [event({ id: "free-13" })])[0]!.slots[0]!.entries;
    expect(entries).toHaveLength(1);
    expect(entries[0]!.divisions).toEqual(["13 · Girls"]);
  });

  it("has no columns for a carnival with no program, and does not throw", () => {
    // Both ordinary: a carnival before its program is built, and an offline load
    // where the schedule simply is not cached yet.
    expect(buildProgram(undefined, [event({ id: "free-13" })])).toEqual([]);
    expect(buildProgram({ carnivalId: "c-1", streams: [] }, [])).toEqual([]);
  });
});
