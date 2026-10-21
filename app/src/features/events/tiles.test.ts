import { describe, expect, it } from "vitest";
import type { CarnivalEvent } from "../../api/types";
import { disciplineOf } from "./eventLabels";
import { assignedEvents, buildEventTiles, isAssignedTo } from "./tiles";

/**
 * Tiles are DERIVED (ADR-0014 §3, CLAUDE.md hard rule: school-specific rules
 * are configuration). Every assertion here is about the shape of the data, not
 * about swimming: no stroke, distance or sport appears in the code under test,
 * so athletics gets its grid from its own definitions and facets with no change
 * at all.
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
    scheduleSlotId: null,
    slotOrder: null,
    status: "Open",
    version: 1,
    entryCount: 0,
    resultCount: 0,
    complete: false,
    officials: [],
    tags: ["discipline:Freestyle", "distance:50m"],
    ...over,
  } as CarnivalEvent;
}

describe("buildEventTiles", () => {
  it("groups instances by event definition and sums the progress counts", () => {
    const tiles = buildEventTiles([
      event({ id: "e1", entryCount: 6, resultCount: 4 }),
      event({ id: "e2", ageGroup: "14", entryCount: 8, resultCount: 1 }),
      event({
        id: "e3",
        eventDefinitionId: "def-back",
        definitionName: "Backstroke 50m",
        tags: ["discipline:Backstroke"],
        entryCount: 5,
        resultCount: 5,
      }),
    ]);

    expect(tiles.map((t) => t.name)).toEqual(["Freestyle 50m", "Backstroke 50m"]);
    expect(tiles[0]!.events).toHaveLength(2);
    expect(tiles[0]!.progress.entryCount).toBe(14);
    expect(tiles[0]!.progress.expectedCount).toBe(14);
    expect(tiles[0]!.progress.resultCount).toBe(5);
    expect(tiles[1]!.progress.resultCount).toBe(5);
  });

  it("leaves a Social twin out of the results denominator, so the tile can finish", () => {
    // ADR-0014 §4: a Social-scored event's points come from ENTRIES and it needs
    // no result at all. Counted in, its entries would be permanent stragglers —
    // 40/48 forever on a definition whose 40 timed swims are all recorded.
    const tiles = buildEventTiles([
      event({ id: "c1", entryCount: 40, resultCount: 40 }),
      event({
        id: "s1",
        ageGroup: "15",
        category: null,
        tier: "Social",
        effectiveScoringMode: "Social",
        entryCount: 8,
        resultCount: 0,
      }),
    ]);
    expect(tiles[0]!.progress.expectedCount).toBe(40);
    expect(tiles[0]!.progress.resultCount).toBe(40);
    // The Social entries are not hidden — they are just not swims owing a time.
    expect(tiles[0]!.progress.entryCount).toBe(48);
  });

  it("keeps definitions apart even when they share a discipline facet", () => {
    // The relay and the individual race are both discipline:Freestyle, but the
    // division chips can only move WITHIN a definition — merging them would
    // build a tile whose chips cannot reach half of it.
    const tiles = buildEventTiles([
      event({ id: "e1" }),
      event({
        id: "e2",
        eventDefinitionId: "def-relay",
        definitionName: "Freestyle Relay 4x50m",
        tags: ["discipline:Freestyle"],
      }),
    ]);
    expect(tiles).toHaveLength(2);
  });

  it("orders tiles by first appearance, i.e. the program order the server sent", () => {
    const tiles = buildEventTiles([
      event({ id: "e1", eventDefinitionId: "def-back", definitionName: "Backstroke 50m" }),
      event({ id: "e2", eventDefinitionId: "def-free", definitionName: "Freestyle 50m" }),
      event({ id: "e3", eventDefinitionId: "def-back", definitionName: "Backstroke 50m" }),
    ]);
    expect(tiles.map((t) => t.name)).toEqual(["Backstroke 50m", "Freestyle 50m"]);
  });

  it("opens the first instance in program order and counts open and complete", () => {
    const tiles = buildEventTiles([
      event({ id: "e1", status: "Closed", complete: true, entryCount: 2, resultCount: 2 }),
      event({ id: "e2", status: "Open", ageGroup: "14"}),
      event({ id: "e3", status: "Open", ageGroup: "15"}),
    ]);
    expect(tiles[0]!.target.id).toBe("e1");
    expect(tiles[0]!.openCount).toBe(2);
    expect(tiles[0]!.completeCount).toBe(1);
  });

  it("counts completion off the server's flag, not the event status", () => {
    // ADR-0017 retired per-event finalisation, so `status === "Finalised"` is
    // permanently false for anything captured after it — a tile counting statuses
    // would read "0 complete" all day on a fully recorded carnival. It is also
    // why an OPEN instance can be complete: completion says the recording is
    // done, not that the event is shut.
    const tiles = buildEventTiles([
      event({ id: "e1", status: "Open", complete: true, entryCount: 3, resultCount: 3 }),
      event({ id: "e2", status: "Open", ageGroup: "14", entryCount: 3 }),
    ]);
    expect(tiles[0]!.completeCount).toBe(1);
  });

  it("does not count an instance nobody entered as complete", () => {
    // The server decides this (an empty event owes nothing but contested
    // nothing); the tile must not paper over it, because an unentered division
    // is the end-of-day straggler hunt's first question.
    const tiles = buildEventTiles([event({ id: "e1", entryCount: 0, complete: false })]);
    expect(tiles[0]!.completeCount).toBe(0);
  });

  it("counts a Social instance the server calls complete, which owes no results", () => {
    // ADR-0014 §4: a Social event scores a flat per-entry point and needs no
    // times at all, so 0 results over 4 entries is finished, not unfinished.
    const tiles = buildEventTiles([
      event({
        id: "e1", tier: "Social", effectiveScoringMode: "Social",
        entryCount: 4, resultCount: 0, complete: true,
      }),
    ]);
    expect(tiles[0]!.completeCount).toBe(1);
  });

  it("names a tile from the discipline facet when the list carries no definition name", () => {
    const tiles = buildEventTiles([
      event({ id: "e1", definitionName: null, definitionCode: null }),
    ]);
    expect(tiles[0]!.name).toBe("Freestyle");
  });

  it("never titles a tile with a blank, whatever the list carries", () => {
    // Every candidate empty — including a valueless "discipline:" tag, which is
    // a value `??` would happily accept.
    const tiles = buildEventTiles([
      event({ id: "e1", definitionName: "", definitionCode: null, tags: ["discipline:"] }),
    ]);
    expect(tiles[0]!.name).toBe("Event");
  });

  it("reads the discipline facet, and nothing when the definition is untagged", () => {
    expect(disciplineOf({ tags: ["distance:50m", "discipline:Butterfly"] })).toBe("Butterfly");
    expect(disciplineOf({ tags: ["distance:50m"] })).toBeNull();
    expect(disciplineOf({ tags: null })).toBeNull();
    // A facet with no value names nothing.
    expect(disciplineOf({ tags: ["discipline:"] })).toBeNull();
  });
});

describe("tile classification (a badge must be true of the whole tile)", () => {
  const competitive = event({ id: "c1" });
  const social = event({
    id: "s1",
    ageGroup: "15",
    tier: "Social",
    effectiveScoringMode: "Social",
  });

  it("does not badge a mixed tile from whichever instance is listed first", () => {
    // Program order decides `target`, so badging from it makes a four-division
    // Competitive definition read "Social" purely because its twin runs first.
    expect(buildEventTiles([social, competitive])[0]!.classification.tier).toBe("Competitive");
    expect(buildEventTiles([competitive, social])[0]!.classification.tier).toBe("Competitive");
  });

  it("badges a tile whose every instance is Social", () => {
    expect(buildEventTiles([social])[0]!.classification.tier).toBe("Social");
  });

  it("keeps 'no points' for tiles where no instance scores at all", () => {
    const excluded = event({ id: "x1", scoresPoints: false });
    expect(buildEventTiles([excluded])[0]!.classification.scoresPoints).toBe(false);
    expect(buildEventTiles([excluded, competitive])[0]!.classification.scoresPoints).toBe(true);
  });

  it("only reports a scoring-mode override when every instance overrides alike", () => {
    const flat = event({ id: "f1", scoringMode: "Social", effectiveScoringMode: "Social" });
    expect(buildEventTiles([flat])[0]!.classification.scoringMode).toBe("Social");
    expect(buildEventTiles([flat, competitive])[0]!.classification.scoringMode).toBeNull();
  });
});

describe("assignment (ADR-0009 — ordering, never access control)", () => {
  const assigned = event({
    id: "e1",
    officials: [{ subjectId: "olivia", subjectDisplayName: "Olivia Official" }],
  });
  const other = event({ id: "e2", eventDefinitionId: "def-back" });

  it("matches the signed-in subject against the event's officials", () => {
    expect(isAssignedTo(assigned, "olivia")).toBe(true);
    expect(isAssignedTo(assigned, "someone-else")).toBe(false);
    expect(isAssignedTo(assigned, null)).toBe(false);
  });

  it("returns only assigned events — the rest are still in the full list", () => {
    const events = [assigned, other];
    expect(assignedEvents(events, "olivia").map((e) => e.id)).toEqual(["e1"]);
    // No assignments (or signed out) = no block, never an empty screen.
    expect(assignedEvents(events, "unassigned")).toEqual([]);
    expect(buildEventTiles(events)).toHaveLength(2);
  });
});
