import { describe, expect, it } from "vitest";
import type { Race } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { placementOf, requestedSlotId, vacantLanes } from "./deckPlacement";

/**
 * Where the deck believes each swimmer is, and which lanes it may offer them.
 * Placement is race-scoped and the outbox overlays the server (ADR-0015 §4).
 */

const race = (
  id: string,
  members: [string, string, number | null][],
  eventId: string | null = null,
): Race => ({
  id,
  eventId,
  scheduleSlotId: null,
  sortOrder: 0,
  label: null,
  entries: members.map(([entryId, carnivalEventId, lane]) => ({
    entryId,
    carnivalEventId,
    displayName: entryId,
    houseCode: "BANK",
    isTeam: false,
    heat: null,
    lane,
  })),
});

const item = (over: Partial<OutboxItem> & Pick<OutboxItem, "id" | "kind" | "payload">): OutboxItem =>
  ({
    actorSubject: "marshal-1",
    capturedAt: "2026-08-17T09:00:00.000Z",
    status: "queued",
    attempts: 0,
    ...over,
  }) as OutboxItem;

const materialiseItem = (id: string, eventId: string, scheduleSlotId: string | null = null) =>
  item({
    id,
    kind: "race.materialise",
    payload: { carnivalId: "c-1", eventId, scheduleSlotId, label: null, display: { title: "x" } },
  });

describe("requestedSlotId", () => {
  const unscheduled = { id: "r-1", scheduleSlotId: null };

  it("names the block a race asked for when it ended up on none", () => {
    expect(requestedSlotId([materialiseItem("r-1", "ev-1", "slot-7")], unscheduled)).toBe("slot-7");
  });

  it("covers the admin create as well as the materialisation", () => {
    const created = item({
      id: "r-1",
      kind: "race.create",
      payload: { carnivalId: "c-1", scheduleSlotId: "slot-3", sortOrder: null, label: null },
    });
    expect(requestedSlotId([created], unscheduled)).toBe("slot-3");
  });

  it("says nothing when the race is on the program, or asked for nowhere", () => {
    expect(requestedSlotId([materialiseItem("r-1", "ev-1", "slot-7")], {
      id: "r-1",
      scheduleSlotId: "slot-7",
    })).toBeNull();
    expect(requestedSlotId([materialiseItem("r-1", "ev-1", null)], unscheduled)).toBeNull();
    expect(requestedSlotId([], unscheduled)).toBeNull();
    expect(requestedSlotId(undefined, unscheduled)).toBeNull();
  });
});

describe("placementOf", () => {
  const races = [race("r-1", [["en-1", "ev-1", 3]])];

  it("reads the server when this device holds nothing newer", () => {
    expect(placementOf(races, new Map(), "en-1")).toEqual({ raceId: "r-1", lane: 3 });
    expect(placementOf(races, new Map(), "en-none")).toEqual({ raceId: null, lane: null });
  });

  it("lets an undelivered placement overrule it", () => {
    const queued = new Map([
      ["en-1", item({ id: "i-1", kind: "entry.assign", payload: { raceId: "r-2", lane: 5 } })],
    ]);
    expect(placementOf(races, queued, "en-1")).toEqual({ raceId: "r-2", lane: 5 });
  });

  it("ignores an item the server has already answered", () => {
    // An accepted placement lives for days (ADR-0013) and the races read is the
    // fresher truth the moment the drain invalidates it.
    const accepted = new Map([
      [
        "en-1",
        item({ id: "i-1", kind: "entry.assign", status: "accepted", payload: { raceId: "r-2", lane: 5 } }),
      ],
    ]);
    expect(placementOf(races, accepted, "en-1")).toEqual({ raceId: "r-1", lane: 3 });
  });
});

describe("vacantLanes", () => {
  it("offers the free lanes and the swimmer's own, never anybody else's", () => {
    expect(vacantLanes([1, 2, 3, 4], new Set([1, 3]), 3)).toEqual([2, 3, 4]);
    expect(vacantLanes([1, 2, 3, 4], new Set([1, 3]), null)).toEqual([2, 4]);
  });
});
