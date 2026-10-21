import { beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import { ENTRY_ASSIGN_KIND, type EntryAssignPayload } from "../api/outboxTransport";

/**
 * ADR-0015 §5: marshalling rides the outbox, and the HEAT IS DERIVED. The
 * default matters more than it looks — a typed heat is wrong house points, so
 * the helper must send null unless a caller deliberately overrides it.
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

import { submitAssignmentViaOutbox, submitSwapViaOutbox } from "./assignEntry";

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  syncNow.mockClear();
});

describe("submitAssignmentViaOutbox", () => {
  it("queues the placement with heat null so the server derives it, then drains", async () => {
    const item = await submitAssignmentViaOutbox({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "r-1",
      lane: 4,
      entryDisplayName: "Sam Swimmer",
      actorSubject: "marshal-1",
      actorDisplayName: "Marnie Marshal",
    });

    const stored = await db.outbox.get(item.id);
    expect(stored?.kind).toBe(ENTRY_ASSIGN_KIND);
    expect(stored?.payload).toEqual({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "r-1",
      lane: 4,
      heat: null,
      display: { entryDisplayName: "Sam Swimmer" },
    } satisfies EntryAssignPayload);
    // The marshal, captured now: the heat this write assigns decides house
    // points, so a drain after a device handover must not re-attribute it.
    expect(stored?.actorSubject).toBe("marshal-1");
    expect(stored?.actorDisplayName).toBe("Marnie Marshal");
    expect(syncNow).toHaveBeenCalledTimes(1);
  });

  it("passes an explicit heat through for the odd case", async () => {
    const item = await submitAssignmentViaOutbox({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "r-1",
      lane: 4,
      heat: 2,
      entryDisplayName: "Sam Swimmer",
      actorSubject: "marshal-1",
    });

    expect((await db.outbox.get(item.id))?.payload).toMatchObject({ heat: 2 });
  });

  it("clears a placement with a null race, lane and derived heat", async () => {
    // Same verb, replace semantics: the marshal's undo is not a delete.
    const item = await submitAssignmentViaOutbox({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: null,
      lane: null,
      entryDisplayName: "Sam Swimmer",
      actorSubject: "marshal-1",
    });

    expect((await db.outbox.get(item.id))?.payload).toMatchObject({
      raceId: null,
      lane: null,
      heat: null,
    });
  });

  it("keeps a lane-less placement, because a mass start has no lanes", async () => {
    const item = await submitAssignmentViaOutbox({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "r-1",
      lane: null,
      entryDisplayName: "Sam Swimmer",
      actorSubject: "marshal-1",
    });

    expect((await db.outbox.get(item.id))?.payload).toMatchObject({ raceId: "r-1", lane: null });
  });
});

/**
 * ADR-0030. Each swimmer holds the lane the other wants, so the pair is
 * unsatisfiable as two writes: what is pinned here is the legal three, their
 * order, and that the heat is never in the write set.
 */
describe("submitSwapViaOutbox", () => {
  const swap = () =>
    submitSwapViaOutbox({
      carnivalId: "c-1",
      raceId: "r-1",
      raceTitle: "Race 2",
      moving: {
        entryId: "en-1",
        carnivalEventId: "ev-1",
        entryDisplayName: "Sam Swimmer",
        lane: 3,
      },
      occupant: {
        entryId: "en-2",
        carnivalEventId: "ev-1",
        entryDisplayName: "Marley Singh",
        lane: 4,
      },
      actorSubject: "marshal-1",
      actorDisplayName: "Marnie Marshal",
    });

  it("queues vacate → occupant across → tapped swimmer back, in that order", async () => {
    await swap();
    const queued = (await db.outbox.toArray()).sort((a, b) => (a.id < b.id ? -1 : 1));
    expect(queued.map((i) => i.payload as EntryAssignPayload)).toEqual([
      expect.objectContaining({ entryId: "en-1", raceId: "r-1", lane: null }),
      expect.objectContaining({ entryId: "en-2", raceId: "r-1", lane: 3 }),
      expect.objectContaining({ entryId: "en-1", raceId: "r-1", lane: 4 }),
    ]);
    // Capture order IS delivery order: `pendingInOrder` sorts on these ids and
    // UUIDv7 is monotonic per device even inside one millisecond.
    expect(queued.map((i) => i.id)).toEqual([...queued.map((i) => i.id)].sort());
  });

  it("keeps the race on the vacating write, so the swimmer keeps their heat", async () => {
    // The whole reason this needs no server change: `{raceId, lane: null}` is a
    // legal placement, and `DeriveHeat` joins the co-member's heat rather than
    // opening a field. Clearing the RACE would clear the heat with it.
    const [vacate] = await swap();
    expect(vacate!.payload).toMatchObject({ raceId: "r-1", lane: null, heat: null });
  });

  it("carries the marshal and the race title onto all three", async () => {
    const items = await swap();
    for (const item of items) {
      expect(item.actorSubject).toBe("marshal-1");
      expect(item.actorDisplayName).toBe("Marnie Marshal");
      expect((item.payload as EntryAssignPayload).display?.raceTitle).toBe("Race 2");
      expect((item.payload as EntryAssignPayload).heat).toBeNull();
    }
  });

  it("nudges the worker ONCE, after all three are queued", async () => {
    // A nudge per item lets a drain snapshot the queue between them; one nudge
    // at the end means the three ride a single pass FIFO.
    await swap();
    expect(syncNow).toHaveBeenCalledTimes(1);
  });
});
