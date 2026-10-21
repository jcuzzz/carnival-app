import { describe, expect, it } from "vitest";
import { ENTRY_ASSIGN_KIND, type EntryAssignPayload } from "../../api/outboxTransport";
import type { Race, RaceEntry } from "../../api/types";
import type { OutboxItem, OutboxStatus } from "../../db/db";
import { acceptedAfter, laneOptions, placementsByEntry, raceRoster } from "./raceLanes";

/**
 * The lane model, away from the screen: what the recorder is shown is the
 * server's race with this device's undelivered marshalling folded in, and the
 * three properties below are the ones the race screen cannot recover from if
 * these functions get them wrong.
 */

function raceEntry(
  entryId: string,
  lane: number | null,
  displayName: string,
  carnivalEventId = "ev-17",
): RaceEntry {
  return { entryId, carnivalEventId, heat: 1, lane, displayName, houseCode: "BANK", isTeam: false };
}

function assignItem(
  id: string,
  status: OutboxStatus,
  payload: Partial<EntryAssignPayload> & { entryId: string },
): OutboxItem {
  return {
    id,
    kind: ENTRY_ASSIGN_KIND,
    status,
    attempts: 0,
    actorSubject: "official-1",
    capturedAt: "2026-08-13T09:48:00.000Z",
    payload: {
      carnivalId: "c-1",
      carnivalEventId: "ev-17",
      raceId: "r-1",
      lane: null,
      heat: null,
      display: { entryDisplayName: "Nina Novak" },
      ...payload,
    } satisfies EntryAssignPayload,
  };
}

describe("laneOptions", () => {
  it("offers lanes even to a mass start — it must be able to become laned", () => {
    expect(laneOptions([{ lane: null }, { lane: null }], 6)).toEqual([1, 2, 3, 4, 5, 6]);
  });

  it("falls back to a typical pool when no lane count is configured", () => {
    expect(laneOptions([], null)).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
  });

  it("always reaches past the highest lane in use, so an existing placement stays selectable", () => {
    expect(laneOptions([{ lane: 9 }], null)).toHaveLength(10);
    expect(laneOptions([{ lane: 9 }], 6)).toHaveLength(9);
  });
});

describe("raceRoster overlay", () => {
  const race: Race = {
    id: "r-1",
    eventId: null,
    scheduleSlotId: null,
    sortOrder: 1,
    label: "Race 1",
    entries: [raceEntry("en-1", 5, "Nina Novak")],
  };

  it("lets an ACCEPTED placement yield to the server list", () => {
    // Device A placed Nina in lane 2 and the server said yes. Device B has since
    // moved her to lane 5, and the races read says so. The accepted item lives
    // on this device for a WEEK (ADR-0013) — treating it as an overlay rewrote
    // her back to lane 2 for every one of those days.
    const accepted = assignItem("i-1", "accepted", { entryId: "en-1", lane: 2 });
    const rows = raceRoster(race, placementsByEntry([accepted], "c-1"));
    expect(rows).toHaveLength(1);
    expect(rows[0]!.lane).toBe(5);
    expect(rows[0]!.item).toBeUndefined();
  });

  it("still lets a QUEUED placement speak for the row", () => {
    const queued = assignItem("i-2", "queued", { entryId: "en-1", lane: 2 });
    const rows = raceRoster(race, placementsByEntry([queued], "c-1"));
    expect(rows[0]!.lane).toBe(2);
    expect(rows[0]!.item?.id).toBe("i-2");
  });

  it("does not let an accepted CLEAR keep a swimmer off the screen", () => {
    const cleared = assignItem("i-3", "accepted", { entryId: "en-1", raceId: null, lane: null });
    expect(raceRoster(race, placementsByEntry([cleared], "c-1"))).toHaveLength(1);
  });

  it("badges a refused move to ANOTHER race on the row the swimmer is still in", () => {
    // The server said no, so the entry never left — and this screen holds the
    // only row that exists for it. Silence here is a swimmer whose move the
    // marshal believes happened. (The destination race showing the same refusal
    // is a known gap, recorded in BACKLOG.)
    const refused = assignItem("i-4", "conflict", { entryId: "en-1", raceId: "r-2", lane: 3 });
    const rows = raceRoster(race, placementsByEntry([refused], "c-1"));
    expect(rows).toHaveLength(1);
    expect(rows[0]!.lane).toBe(5);
    expect(rows[0]!.item?.status).toBe("conflict");
  });

  it("lets a DISCARDED placement go entirely — no overlay, no badge, no lane held", () => {
    // The marshal let the refused lane tap go from the outbox. It must stop
    // speaking for the row exactly as a cancelled one does (ADR-0025 §2), or it
    // keeps overlaying the roster and occupying a lane nobody can claim.
    const letGo = assignItem("i-5", "discarded", { entryId: "en-1", lane: 2 });
    const rows = raceRoster(race, placementsByEntry([letGo], "c-1"));
    expect(rows).toHaveLength(1);
    expect(rows[0]!.lane).toBe(5);
    expect(rows[0]!.item).toBeUndefined();
  });

  it("bridges a FRESH accepted placement that still carries its display", () => {
    // The races refetch is a round trip away; the placed swimmer must not
    // vanish from the lane list in that window (ADR-0012 / ADR-0031).
    const fresh = assignItem("i-8", "accepted", { entryId: "en-9", lane: 4 });
    expect(raceRoster(race, placementsByEntry([fresh], "c-1"), () => true).map((r) => r.entryId))
      .toEqual(["en-9", "en-1"]);
  });

  it("invents no nameless row once the sweep has stripped an accepted placement", () => {
    // After the grace window the sweep strips the name (ADR-0031); the races
    // read owns the row by then, so the bridge stands down rather than
    // rendering a placement nobody can identify.
    const stripped = assignItem("i-7", "accepted", { entryId: "en-9", lane: 4 });
    stripped.payload = { ...(stripped.payload as EntryAssignPayload), display: undefined };
    expect(raceRoster(race, placementsByEntry([stripped], "c-1"), () => true).map((r) => r.entryId))
      .toEqual(["en-1"]);
  });

  it("does not invent a row for a discarded placement into this race", () => {
    const letGo = assignItem("i-6", "discarded", { entryId: "en-9", lane: 4 });
    expect(raceRoster(race, placementsByEntry([letGo], "c-1")).map((r) => r.entryId)).toEqual([
      "en-1",
    ]);
  });
});

/**
 * The window between an acknowledgement and the read that will carry it
 * (ADR-0012). Bounded by the READ, never by the item's age: that is what keeps
 * a week-old accepted placement from overwriting what everyone else can see.
 */
describe("acceptedAfter", () => {
  const at = (status: OutboxItem["status"], syncedAt?: string): OutboxItem =>
    ({ id: "i-1", kind: "entry.assign", payload: {}, actorSubject: "a", capturedAt: "x", attempts: 1, status, ...(syncedAt ? { syncedAt } : {}) }) as OutboxItem;

  it("takes an acknowledgement the read cannot know about yet", () => {
    const read = Date.parse("2026-08-17T09:00:00Z");
    expect(acceptedAfter(read)(at("accepted", "2026-08-17T09:00:01Z"))).toBe(true);
  });

  it("breaks a same-millisecond tie to the acknowledgement", () => {
    // A local drain can ack inside the millisecond the read landed in, and the
    // two stamps cannot be ordered. Losing the tie re-offers the mint for a race
    // that already exists, which is the one thing this gate is here to stop.
    const read = Date.parse("2026-08-17T09:00:00Z");
    expect(acceptedAfter(read)(at("accepted", "2026-08-17T09:00:00Z"))).toBe(true);
  });

  it("lets the read win once it is the newer of the two", () => {
    const read = Date.parse("2026-08-17T09:00:00Z");
    expect(acceptedAfter(read)(at("accepted", "2026-08-10T09:00:00Z"))).toBe(false);
  });

  it("trusts NO acknowledgement when there is no read at all", () => {
    // A cold or failed races read cannot make a week-old ack current — that is
    // the stale overlay this whole rule exists to prevent.
    expect(acceptedAfter(undefined)(at("accepted", "2026-08-17T09:00:01Z"))).toBe(false);
  });

  it("says nothing about items the server has not answered", () => {
    const read = Date.parse("2026-08-17T09:00:00Z");
    expect(acceptedAfter(read)(at("queued"))).toBe(false);
    expect(acceptedAfter(read)(at("accepted"))).toBe(false);
  });
});
