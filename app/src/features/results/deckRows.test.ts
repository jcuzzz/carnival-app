import { describe, expect, it } from "vitest";
import {
  ENTRY_CREATE_KIND,
  TEAM_CREATE_KIND,
  type EntryCreatePayload,
  type TeamCreatePayload,
} from "../../api/outboxTransport";
import type { OutboxItem, OutboxStatus } from "../../db/db";
import { deckRows } from "./deckRows";

/**
 * The optimistic add bridges the gap between a queued `entry.create` and the
 * entries read that will carry it (ADR-0010). A fresh acceptance still bridges
 * (the refetch is a round trip away); the bridge stops only once the retention
 * sweep has stripped `display` after its grace window (ADR-0031).
 */

function createItem(id: string, status: OutboxStatus, display: unknown): OutboxItem {
  return {
    id,
    kind: ENTRY_CREATE_KIND,
    status,
    attempts: 0,
    actorSubject: "official-1",
    capturedAt: "2026-08-22T09:48:00.000Z",
    payload: {
      carnivalId: "c-1",
      carnivalEventId: "ev-17",
      studentId: "st-1",
      ...(display === undefined ? {} : { display }),
    } as EntryCreatePayload,
  };
}

const nina = { displayName: "Nina Novak", yearLevel: 7, houseCode: "BANK", ageGroup: "U13" };

describe("deckRows optimistic add", () => {
  it("shows a queued add as a row before the server has it", () => {
    const rows = deckRows([], [createItem("i-1", "queued", nina)]);
    expect(rows.map((r) => r.entry.displayName)).toEqual(["Nina Novak"]);
  });

  it("keeps rendering a fresh acceptance that still carries its display", () => {
    // The refetch is a round trip away; the row must not vanish in that window.
    const rows = deckRows([], [createItem("i-3", "accepted", nina)]);
    expect(rows.map((r) => r.entry.displayName)).toEqual(["Nina Novak"]);
  });

  it("renders no nameless row once the sweep has stripped an accepted add", () => {
    // Without this the deck threw on `p.display.displayName` — caught by the
    // e2e social-event journey, not by a unit test.
    const rows = deckRows([], [createItem("i-2", "accepted", undefined)]);
    expect(rows).toHaveLength(0);
  });
});

function teamItem(id: string, status: OutboxStatus, houseCode: string): OutboxItem {
  return {
    id,
    kind: TEAM_CREATE_KIND,
    status,
    attempts: 0,
    actorSubject: "official-1",
    capturedAt: "2026-09-04T09:48:00.000Z",
    payload: {
      carnivalId: "c-1",
      carnivalEventId: "ev-17",
      houseCode,
      studentIds: ["st-1"],
      display: {
        ageGroup: "14",
        legs: [{ displayName: "Ari Swift", yearLevel: 9, stroke: "Freestyle" }],
      },
    } as TeamCreatePayload,
  };
}

/** A landed team entry, as the entries read carries it. */
const landedTeam = {
  id: "en-team",
  carnivalEventId: "ev-17",
  studentId: null,
  raceId: null,
  heat: null,
  lane: null,
  displayName: "BANKSIA team",
  yearLevel: 0,
  houseCode: "BANKSIA",
  ageGroup: "14",
  overrideApplied: false,
  isTeam: true,
  teamHouseCode: "BANKSIA",
  legs: [],
};

describe("deckRows optimistic relay (ADR-0039 A4)", () => {
  it("shows a queued relay as a row before the server has it", () => {
    const rows = deckRows([], [], [teamItem("t-1", "queued", "BANKSIA")]);
    expect(rows.map((r) => r.entry.displayName)).toEqual(["BANKSIA team"]);
    expect(rows[0]!.entry.isTeam).toBe(true);
    expect(rows[0]!.createItem?.id).toBe("t-1");
  });

  it("does not double a relay the entries read already carries", () => {
    // The house is the identity, exactly as the server's TEAM_EXISTS rule says.
    const rows = deckRows([landedTeam], [], [teamItem("t-1", "accepted", "BANKSIA")]);
    expect(rows).toHaveLength(1);
    expect(rows[0]!.entry.id).toBe("en-team");
  });

  it("hands the house back when the capture was let go", () => {
    const rows = deckRows([], [], [teamItem("t-1", "discarded", "BANKSIA")]);
    expect(rows).toHaveLength(0);
  });
});
