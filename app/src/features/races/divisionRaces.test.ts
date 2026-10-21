import { describe, expect, it } from "vitest";
import type { Race } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { deckRaces, optimisticMaterialisedRace, racesOfEvent } from "./divisionRaces";

/**
 * A division's races — its heats (ADR-0039 §1): found by the race's own
 * `eventId` stamp (ADR-0038) or by ENTRY MEMBERSHIP over the one races read,
 * plus whatever this device has queued but not yet delivered.
 */

const race = (
  id: string,
  entries: [string, string][],
  eventId: string | null = null,
  sortOrder = 0,
): Race => ({
  id,
  eventId,
  scheduleSlotId: null,
  sortOrder,
  label: null,
  entries: entries.map(([entryId, carnivalEventId]) => ({
    entryId,
    carnivalEventId,
    displayName: entryId,
    houseCode: "BANK",
    isTeam: false,
    // Materialising assigns no heat; membership is what the races read is for.
    heat: null,
    lane: null,
  })),
});

const races = [
  race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]]),
  race("r-2", [["en-3", "ev-1"]]),
  race("r-3", [["en-4", "ev-2"]]),
];

const materialiseItem = (id: string, eventId: string, scheduleSlotId: string | null = null) =>
  ({
    id,
    kind: "race.materialise",
    payload: { carnivalId: "c-1", eventId, scheduleSlotId, label: null, display: { title: "x" } },
    actorSubject: "marshal-1",
    capturedAt: "2026-08-17T09:00:00.000Z",
    status: "queued",
    attempts: 0,
  }) as OutboxItem;

describe("racesOfEvent", () => {
  it("finds them by entry membership", () => {
    expect(racesOfEvent(races, "ev-1").map((r) => r.id)).toEqual(["r-1", "r-2"]);
    expect(racesOfEvent(races, "ev-2").map((r) => r.id)).toEqual(["r-3"]);
    expect(racesOfEvent(races, "ev-9")).toEqual([]);
    expect(racesOfEvent(undefined, "ev-1")).toEqual([]);
  });

  it("finds an EMPTY race by its stamp, and only for the division it names", () => {
    // The deck draws a chip for that race, so anything reading this and
    // concluding "no heats" would deny a heat the marshal can see.
    expect(racesOfEvent([race("r-1", [], "ev-1")], "ev-1").map((r) => r.id)).toEqual(["r-1"]);
    expect(racesOfEvent([race("r-1", [], "ev-2")], "ev-1")).toEqual([]);
  });

  it("counts a race once when the stamp and the membership agree", () => {
    expect(racesOfEvent([race("r-1", [["en-1", "ev-1"]], "ev-1")], "ev-1")).toHaveLength(1);
  });
});

describe("optimisticMaterialisedRace", () => {
  it("takes the item's id as the race id, and carries the slot it asked for", () => {
    const optimistic = optimisticMaterialisedRace(materialiseItem("r-new", "ev-1", "slot-2"));
    expect(optimistic.id).toBe("r-new");
    expect(optimistic.scheduleSlotId).toBe("slot-2");
    expect(optimistic.eventId).toBe("ev-1");
    // No server position yet, which is what keeps a queued heat numbered last.
    expect(optimistic.sortOrder).toBe(-1);
    expect(optimistic.entries).toEqual([]);
  });
});

describe("deckRaces", () => {
  it("gives the division its own races and nobody else's", () => {
    expect(deckRaces("ev-1", races, []).map((r) => r.id)).toEqual(["r-1", "r-2"]);
  });

  it("adds a heat this device has minted and not yet delivered", () => {
    const found = deckRaces("ev-1", [races[0]!], [materialiseItem("r-new", "ev-1")]);
    expect(found.map((r) => r.id)).toEqual(["r-1", "r-new"]);
    expect(found[1]!.sortOrder).toBe(-1);
  });

  it("prefers the LANDED race over the optimistic shape, so it keeps its position", () => {
    const landed = race("r-new", [["en-9", "ev-1"]], "ev-1", 4);
    const found = deckRaces("ev-1", [landed], [materialiseItem("r-new", "ev-1")]);
    expect(found).toHaveLength(1);
    expect(found[0]!.sortOrder).toBe(4);
  });

  it("ignores a materialisation for another division", () => {
    expect(deckRaces("ev-1", [], [materialiseItem("r-new", "ev-2")])).toEqual([]);
  });
});
