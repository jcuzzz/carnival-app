import { beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import {
  RACE_MATERIALISE_KIND,
  TEAM_CREATE_KIND,
  type TeamCreatePayload,
} from "../api/outboxTransport";
import { blockedByDependency, dependencyIdsOf, discard, enqueue } from "../sync/outbox";
import type { EligibleStudent } from "../api/types";

/**
 * ADR-0039 note A4: a relay is a capture like any other, on the ADR-0010 shape.
 * The property that closes the gap is ORDER — a queued relay naming a heat this
 * device minted waits behind the mint instead of overtaking it.
 */

const syncNow = vi.fn().mockResolvedValue({
  stopReason: "offline",
  accepted: 0,
  rejected: 0,
  conflicts: 0,
});

vi.mock("../api/syncSetup", () => ({
  syncWorker: { start: vi.fn(), stop: vi.fn(), syncNow: () => syncNow() },
  resumeAfterSignIn: vi.fn(),
}));

import { optimisticTeamEntry, submitTeamViaOutbox, teamItemsForEvent } from "./submitTeam";

const swimmer = (n: number): EligibleStudent => ({
  studentId: `s-${n}`,
  displayName: `Swimmer ${n}`,
  houseCode: "BANKSIA",
  yearLevel: 8 + n,
  ageGroup: "14",
});

const team = [swimmer(1), swimmer(2), swimmer(3), swimmer(4)];

function submit(overrides: Partial<Parameters<typeof submitTeamViaOutbox>[0]> = {}) {
  return submitTeamViaOutbox({
    carnivalId: "c-1",
    carnivalEventId: "ev-1",
    houseCode: "BANKSIA",
    swimmers: team,
    strokes: ["Backstroke", "Breaststroke", "Butterfly", "Freestyle"],
    ageGroup: "14",
    actorSubject: "marshal-1",
    actorDisplayName: "Marnie Marshal",
    raceId: "r-1",
    ...overrides,
  });
}

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  syncNow.mockClear();
});

describe("submitTeamViaOutbox", () => {
  it("queues the relay in leg order with the heat it was captured against", async () => {
    const item = await submit();

    const stored = await db.outbox.get(item.id);
    expect(stored?.kind).toBe(TEAM_CREATE_KIND);
    expect(stored?.payload).toEqual({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      houseCode: "BANKSIA",
      studentIds: ["s-1", "s-2", "s-3", "s-4"],
      raceId: "r-1",
      display: {
        ageGroup: "14",
        legs: [
          { displayName: "Swimmer 1", yearLevel: 9, stroke: "Backstroke" },
          { displayName: "Swimmer 2", yearLevel: 10, stroke: "Breaststroke" },
          { displayName: "Swimmer 3", yearLevel: 11, stroke: "Butterfly" },
          { displayName: "Swimmer 4", yearLevel: 12, stroke: "Freestyle" },
        ],
      },
    } satisfies TeamCreatePayload);
    // The marshal at the deck, captured now (ADR-0002/0006).
    expect(stored?.actorSubject).toBe("marshal-1");
    expect(stored?.actorDisplayName).toBe("Marnie Marshal");
    expect(syncNow).toHaveBeenCalledTimes(1);
  });

  it("omits the heat entirely where the deck could not name one", async () => {
    const item = await submit({ raceId: null, strokes: null });
    const payload = (await db.outbox.get(item.id))!.payload as TeamCreatePayload;
    expect("raceId" in payload).toBe(false);
    expect(payload.display?.legs.every((l) => l.stroke === null)).toBe(true);
  });

  it("waits behind this device's own queued heat mint (the A4 gap)", async () => {
    // The whole reason the relay could not name a queued heat: an online POST
    // overtakes the mint. Queued, `blockedByDependency` holds it until the mint
    // is accepted — the rule `entry.create` already lives under.
    const mint = await enqueue({
      kind: RACE_MATERIALISE_KIND,
      payload: { carnivalId: "c-1", eventId: "ev-1" },
      actorSubject: "marshal-1",
    });
    const item = await submit({ raceId: mint.id });
    expect(dependencyIdsOf(item)).toContain(mint.id);
    expect(await blockedByDependency(item)).toBe(true);

    await db.outbox.update(mint.id, { status: "accepted" });
    expect(await blockedByDependency(item)).toBe(false);
  });

  it("survives a discarded mint as a capture, not as a cancellation", async () => {
    // ADR-0025 / ADR-0039 A2: a relay is a real team the marshal assembled, so
    // letting its heat go re-points it at no heat rather than withdrawing it.
    const mint = await enqueue({
      kind: RACE_MATERIALISE_KIND,
      payload: { carnivalId: "c-1", eventId: "ev-1" },
      actorSubject: "marshal-1",
    });
    const item = await submit({ raceId: mint.id });
    await db.outbox.update(mint.id, { status: "conflict" }); // the mint parked
    await discard(mint.id, "admin-1");

    const after = (await db.outbox.get(item.id))!;
    expect(after.status).toBe("queued");
    expect((after.payload as TeamCreatePayload).raceId).toBeNull();
  });
});

describe("teamItemsForEvent", () => {
  it("selects this event's team creates and nothing else", async () => {
    const mine = await submit();
    await submit({ carnivalEventId: "ev-2", houseCode: "WATTLE" });

    const items = await db.outbox.toArray();
    expect(teamItemsForEvent(items, "ev-1").map((i) => i.id)).toEqual([mine.id]);
    expect(teamItemsForEvent(undefined, "ev-1")).toEqual([]);
  });
});

describe("optimisticTeamEntry", () => {
  it("shapes the row the way the server synthesises a team entry", async () => {
    const item = await submit();
    const entry = optimisticTeamEntry((await db.outbox.get(item.id))!)!;

    expect(entry.id).toBe(item.id);
    expect(entry.isTeam).toBe(true);
    expect(entry.studentId).toBeNull();
    expect(entry.displayName).toBe("BANKSIA team");
    expect(entry.teamHouseCode).toBe("BANKSIA");
    expect(entry.yearLevel).toBe(0);
    expect(entry.ageGroup).toBe("14");
    expect(entry.raceId).toBe("r-1");
    expect(entry.heat).toBeNull();
    expect(entry.lane).toBeNull();
    expect(entry.legs.map((l) => [l.legOrder, l.studentId, l.stroke])).toEqual([
      [1, "s-1", "Backstroke"],
      [2, "s-2", "Breaststroke"],
      [3, "s-3", "Butterfly"],
      [4, "s-4", "Freestyle"],
    ]);
  });

  it("renders no row once the retention sweep has stripped the display (ADR-0031)", async () => {
    const item = await submit();
    const stored = (await db.outbox.get(item.id))!;
    const bare = { ...(stored.payload as TeamCreatePayload) };
    delete bare.display;
    expect(optimisticTeamEntry({ ...stored, payload: bare })).toBeNull();
  });
});
