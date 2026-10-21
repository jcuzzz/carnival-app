import { describe, expect, it } from "vitest";
import type { CarnivalEvent, Race, RaceEntry, Schedule } from "../../api/types";
import {
  groupRacesBySlot,
  laneSummary,
  nextSortOrder,
  OFF_PROGRAM_KEY,
  raceSummary,
  raceTitle,
  slotOptions,
} from "./raceGroups";

/**
 * The race list reshaped for the way a human navigates a thirty-block program
 * (ADR-0015 §3). Kept through the ADR-0018 lane gap, so tested directly.
 */

const schedule: Schedule = {
  carnivalId: "c-1",
  streams: [
    {
      id: "pool",
      name: "Pool",
      sortOrder: 0,
      slots: [
        {
          id: "slot-free",
          streamId: "pool",
          sortOrder: 0,
          startTime: "09:45:00",
          endTime: null,
          kind: "Events",
          label: "FREESTYLE EVENTS",
          adjustmentCategory: null,
          eventIds: [],
        },
        {
          id: "slot-lunch",
          streamId: "pool",
          sortOrder: 1,
          startTime: "12:00:00",
          endTime: null,
          kind: "Item",
          label: "LUNCH",
          adjustmentCategory: null,
          eventIds: [],
        },
        {
          id: "slot-back",
          streamId: "pool",
          sortOrder: 2,
          startTime: "13:00:00",
          endTime: null,
          kind: "Events",
          label: "BACKSTROKE EVENTS",
          adjustmentCategory: null,
          eventIds: [],
        },
      ],
    },
  ],
};

const race = (id: string, sortOrder: number, scheduleSlotId: string | null, label: string | null = null): Race => ({
  id,
  eventId: null,
  scheduleSlotId,
  sortOrder,
  label,
  entries: [],
});

describe("groupRacesBySlot", () => {
  it("groups by program block, in program order, off-program last", () => {
    const groups = groupRacesBySlot(
      [race("r-3", 1, null), race("r-2", 2, "slot-free"), race("r-1", 1, "slot-free"), race("r-4", 1, "slot-back")],
      schedule,
    );
    expect(groups.map((g) => g.key)).toEqual(["slot-free", "slot-back", OFF_PROGRAM_KEY]);
    // Empty blocks are dropped: thirty headers would bury the two that matter.
    expect(groups[0]!.subtitle).toBe("Pool · 09:45");
    expect(groups[0]!.races.map((r) => r.id)).toEqual(["r-1", "r-2"]);
    expect(groups[2]!.title).toBe("Not on the program");
  });

  it("does not call every race off-program when the program is simply not cached", () => {
    const groups = groupRacesBySlot([race("r-1", 1, "slot-free")], undefined);
    expect(groups[0]!.title).toBe("Races");
    expect(groups[0]!.subtitle).toContain("not grouped by block");
  });

  it("keeps a race whose block has since been removed from the program", () => {
    const groups = groupRacesBySlot([race("r-1", 1, "slot-deleted")], schedule);
    expect(groups.map((g) => g.key)).toEqual([OFF_PROGRAM_KEY]);
  });
});

describe("race naming and summaries", () => {
  it("names a race by its label, or by its position", () => {
    expect(raceTitle({ label: "Freestyle heat 3", sortOrder: 2 })).toBe("Freestyle heat 3");
    expect(raceTitle({ label: "  ", sortOrder: 4 })).toBe("Race 5");
    expect(raceTitle({ label: null, sortOrder: 4 })).toBe("Race 5");
    // The first race of a carnival is sortOrder 0, and "Race 0" is not a name.
    expect(raceTitle({ label: null, sortOrder: 0 })).toBe("Race 1");
    // Queued on this device: no server position, so no invented number.
    expect(raceTitle({ label: null, sortOrder: -1 })).toBe("New race");
  });

  it("reads lanes as a range, and a mass start as having none", () => {
    const entry = (lane: number | null): RaceEntry => ({
      entryId: `en-${lane}`,
      carnivalEventId: "ev-1",
      displayName: "Swimmer",
      houseCode: "BANK",
      heat: 1,
      lane,
      isTeam: false,
    });
    expect(laneSummary([entry(1), entry(6)])).toBe("lanes 1–6");
    expect(laneSummary([entry(3)])).toBe("lane 3");
    // A 1500m has no lanes at all and is an ordinary race, not an error.
    expect(laneSummary([entry(null), entry(null)])).toBe("no lanes");
  });

  it("names every event a race holds, because it may hold more than one", () => {
    const events = [
      { id: "ev-17", definitionName: "Freestyle 50m", ageGroup: "17", category: "M", tier: "Competitive", tags: [] },
      { id: "ev-18", definitionName: "Freestyle 50m", ageGroup: "18", category: "M", tier: "Competitive", tags: [] },
    ] as unknown as CarnivalEvent[];
    const withEntries: Race = {
      ...race("r-1", 1, "slot-free"),
      entries: [
        { entryId: "a", carnivalEventId: "ev-17", displayName: "A", houseCode: "B", heat: 1, lane: 1, isTeam: false },
        { entryId: "b", carnivalEventId: "ev-18", displayName: "B", houseCode: "B", heat: 1, lane: 2, isTeam: false },
        { entryId: "c", carnivalEventId: "ev-gone", displayName: "C", houseCode: "B", heat: 1, lane: 3, isTeam: false },
      ],
    };
    // The unknown event is skipped, never rendered as a placeholder.
    expect(raceSummary(withEntries, events)).toBe(
      "3 entries · lanes 1–3 · Freestyle 50m · 17 · Boys, Freestyle 50m · 18 · Boys",
    );
    expect(raceSummary(race("r-2", 2, null), events)).toBe("No entries yet");
  });
});

describe("program helpers", () => {
  it("takes the next running order within the block, and only within it", () => {
    const races = [race("r-1", 1, "slot-free"), race("r-2", 3, "slot-free"), race("r-3", 9, null)];
    expect(nextSortOrder(races, "slot-free")).toBe(4);
    expect(nextSortOrder(races, null)).toBe(10);
    expect(nextSortOrder(races, "slot-back")).toBe(1);
  });

  it("offers only blocks that can hold events", () => {
    // A non-competition item (buses, a novelty block) holds no events and the
    // server refuses a race under one.
    expect(slotOptions(schedule).map((s) => s.id)).toEqual(["slot-free", "slot-back"]);
    expect(slotOptions(schedule)[0]).toMatchObject({ streamName: "Pool", time: "09:45" });
    expect(slotOptions(undefined)).toEqual([]);
  });
});
