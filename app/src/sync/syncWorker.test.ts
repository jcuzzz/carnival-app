import { beforeEach, describe, expect, it } from "vitest";
import { db, type OutboxItem } from "../db/db";
import {
  cancelIfPending,
  dependentsOf,
  discard,
  enqueue,
  queuePauseState,
  requeue,
  resumeQueue,
  SYNCING_RECLAIM_MS,
  transition,
} from "./outbox";
import {
  backoffDelayMs,
  throttleDelayMs,
  createSyncWorker,
  drainOnce,
  type DrainResult,
  type SyncResponse,
  type SyncWorker,
  type Transport,
} from "./syncWorker";
import { _resetUuidv7StateForTests } from "./uuidv7";

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
});

/** Records every send; replies per a scripted handler. */
function fakeServer(handler: (item: OutboxItem, seen: string[]) => SyncResponse | Error): {
  transport: Transport;
  sent: OutboxItem[];
} {
  const sent: OutboxItem[] = [];
  const transport: Transport = async (item) => {
    sent.push(item);
    const out = handler(
      item,
      sent.map((s) => s.id),
    );
    if (out instanceof Error) throw out;
    return out;
  };
  return { transport, sent };
}

const online = () => true;

describe("drainOnce — happy path", () => {
  it("drains pending items in capture order and marks them accepted with the server response", async () => {
    const ids: string[] = [];
    for (let i = 0; i < 5; i++) {
      ids.push((await enqueue({ kind: "result.submit", payload: i, actorSubject: "a" })).id);
    }
    const server = fakeServer((item) => ({
      status: 201,
      body: { id: item.id, currentRevision: 1 },
    }));

    const result = await drainOnce({ transport: server.transport, isOnline: online });

    expect(result).toMatchObject({ stopReason: "drained", accepted: 5, rejected: 0, conflicts: 0 });
    expect(server.sent.map((s) => s.id)).toEqual(ids);
    for (const id of ids) {
      const row = await db.outbox.get(id);
      expect(row?.status).toBe("accepted");
      expect(row?.serverResponse).toEqual({ id, currentRevision: 1 });
      expect(row?.syncedAt).toBeTruthy();
      expect(row?.attempts).toBe(1);
    }
  });

  it("does nothing while offline", async () => {
    await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    const server = fakeServer(() => ({ status: 200 }));
    const result = await drainOnce({ transport: server.transport, isOnline: () => false });
    expect(result.stopReason).toBe("offline");
    expect(server.sent).toHaveLength(0);
  });

  it("requires an injected transport — there is no default and no generic /sync endpoint", async () => {
    await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await expect(
      drainOnce({ isOnline: online } as unknown as Parameters<typeof drainOnce>[0]),
    ).rejects.toThrow(/injected transport/);
  });
});

describe("idempotent retry", () => {
  it("resends the SAME id after a lost ack; a replayed 2xx stored response completes the item", async () => {
    const item = await enqueue({ kind: "result.submit", payload: { t: 1 }, actorSubject: "a" });

    // First attempt: server committed but the ack was lost (network error thrown
    // after send). Second attempt: server dedup replays the stored response.
    let calls = 0;
    const server = fakeServer(() => {
      calls += 1;
      if (calls === 1) return new TypeError("network dropped");
      return { status: 200, body: { id: item.id, currentRevision: 1 } };
    });

    const first = await drainOnce({ transport: server.transport, isOnline: online });
    expect(first.stopReason).toBe("network-error");
    expect((await db.outbox.get(item.id))?.status).toBe("queued");
    expect((await db.outbox.get(item.id))?.attempts).toBe(1);

    const second = await drainOnce({ transport: server.transport, isOnline: online });
    expect(second).toMatchObject({ stopReason: "drained", accepted: 1 });

    // Both sends carried the same idempotency key (the item id).
    expect(server.sent).toHaveLength(2);
    expect(server.sent[0]!.id).toBe(item.id);
    expect(server.sent[1]!.id).toBe(item.id);
    const row = await db.outbox.get(item.id);
    expect(row?.status).toBe("accepted");
    expect(row?.serverResponse).toEqual({ id: item.id, currentRevision: 1 });
    expect(row?.attempts).toBe(2);
  });

  it("keeps a 429 queued with nothing behind it overtaking, never a permanent rejection", async () => {
    const a = await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: 2, actorSubject: "a" });
    const server = fakeServer((item) =>
      item.id === a.id ? { status: 429, retryAfterMs: 5_000 } : { status: 200 },
    );

    const result = await drainOnce({ transport: server.transport, isOnline: online });

    expect(result.stopReason).toBe("throttled");
    expect(result.rejected).toBe(0);
    expect(result.retryAfterMs).toBe(5_000);
    expect(server.sent.map((s) => s.id)).toEqual([a.id]);
    expect((await db.outbox.get(a.id))?.status).toBe("queued");
    expect((await db.outbox.get(b.id))?.status).toBe("queued");
  });

  it("stops the drain at the first retryable failure so ordering is preserved", async () => {
    const a = await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: 2, actorSubject: "a" });
    const server = fakeServer((item) =>
      item.id === a.id ? { status: 503 } : { status: 200 },
    );

    const result = await drainOnce({ transport: server.transport, isOnline: online });
    expect(result.stopReason).toBe("network-error");
    // b was never attempted — it must not overtake a.
    expect(server.sent.map((s) => s.id)).toEqual([a.id]);
    expect((await db.outbox.get(a.id))?.status).toBe("queued");
    expect((await db.outbox.get(b.id))?.status).toBe("queued");
  });
});

describe("auth pause", () => {
  it("on 401: pauses the queue, requeues the item, drops nothing", async () => {
    const a = await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: 2, actorSubject: "a" });
    const server = fakeServer(() => ({ status: 401 }));

    const result = await drainOnce({ transport: server.transport, isOnline: online });

    expect(result.stopReason).toBe("paused");
    expect(server.sent).toHaveLength(1); // stopped immediately
    expect((await db.outbox.get(a.id))?.status).toBe("queued");
    expect((await db.outbox.get(b.id))?.status).toBe("queued");
    expect(await db.outbox.count()).toBe(2); // nothing dropped
    expect(await queuePauseState()).not.toBeNull();
  });

  it("stays paused (sends nothing) until resumed, then drains", async () => {
    const item = await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const failing = fakeServer(() => ({ status: 401 }));
    await drainOnce({ transport: failing.transport, isOnline: online });

    const server = fakeServer(() => ({ status: 200 }));
    const whilePaused = await drainOnce({ transport: server.transport, isOnline: online });
    expect(whilePaused.stopReason).toBe("paused");
    expect(server.sent).toHaveLength(0);

    await resumeQueue(); // e.g. after successful re-authentication
    const afterResume = await drainOnce({ transport: server.transport, isOnline: online });
    expect(afterResume).toMatchObject({ stopReason: "drained", accepted: 1 });
    expect((await db.outbox.get(item.id))?.status).toBe("accepted");
  });
});

describe("conflict and rejection handling", () => {
  it("409 (optimistic-concurrency clash) -> conflict state, drain continues", async () => {
    const a = await enqueue({ kind: "result.correct", payload: {}, actorSubject: "a", baseVersion: 3 });
    const b = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    const server = fakeServer((item) =>
      item.id === a.id
        ? { status: 409, body: { error: "CONCURRENCY_CONFLICT", detail: "Revision moved on." } }
        : { status: 200 },
    );

    const result = await drainOnce({ transport: server.transport, isOnline: online });
    expect(result).toMatchObject({ stopReason: "drained", accepted: 1, conflicts: 1 });

    const row = await db.outbox.get(a.id);
    expect(row?.status).toBe("conflict");
    // The refusal is retained for manual resolution by an administrator.
    expect(row?.serverResponse).toEqual({
      error: "CONCURRENCY_CONFLICT",
      detail: "Revision moved on.",
    });
    expect((await db.outbox.get(b.id))?.status).toBe("accepted");
  });

  it("422 (same-key/different-payload mismatch) -> conflict state", async () => {
    const item = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    const server = fakeServer(() => ({ status: 422, body: { error: "payload-mismatch" } }));
    const result = await drainOnce({ transport: server.transport, isOnline: online });
    expect(result.conflicts).toBe(1);
    expect((await db.outbox.get(item.id))?.status).toBe("conflict");
  });

  it("other 4xx -> rejected (terminal, retained for audit)", async () => {
    const item = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    const server = fakeServer(() => ({ status: 403, body: { error: "role" } }));
    const result = await drainOnce({ transport: server.transport, isOnline: online });
    expect(result.rejected).toBe(1);
    const row = await db.outbox.get(item.id);
    expect(row?.status).toBe("rejected");
    expect(row?.lastError).toContain("403");
    expect(await db.outbox.count()).toBe(1); // never deleted
  });
});

/**
 * ADR-0039 review A2: a create names the heat the marshal was standing at, and
 * a heat this device minted exists nowhere else until that mint is accepted.
 * Sending the create first is a swimmer nobody entered — RACE_NOT_IN_EVENT is
 * terminal, and Requeue only re-sends the same dead id.
 */
describe("a write waiting on a race this device has only queued", () => {
  const mintAndCreate = async () => {
    const mint = await enqueue({
      kind: "race.materialise",
      payload: { carnivalId: "c-1", eventId: "ev-1" },
      actorSubject: "a",
    });
    const create = await enqueue({
      kind: "entry.create",
      payload: { carnivalId: "c-1", carnivalEventId: "ev-1", studentId: "st-1", raceId: mint.id },
      actorSubject: "a",
    });
    return { mint, create };
  };

  it("holds the create back while the mint is PARKED, and drains everything else", async () => {
    const { mint, create } = await mintAndCreate();
    const unrelated = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    const server = fakeServer((item) =>
      item.id === mint.id ? { status: 409, body: { error: "CARNIVAL_NOT_OPEN" } } : { status: 200 },
    );

    const result = await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([mint.id, unrelated.id]);
    expect((await db.outbox.get(create.id))?.status).toBe("queued");
    expect((await db.outbox.get(create.id))?.attempts).toBe(0);
    expect((await db.outbox.get(unrelated.id))?.status).toBe("accepted");
    expect(result.stopReason).toBe("drained");
  });

  it("sends it the moment the mint is accepted — the ordinary path is untouched", async () => {
    const { mint, create } = await mintAndCreate();
    const server = fakeServer(() => ({ status: 201 }));

    await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([mint.id, create.id]);
    expect((await db.outbox.get(create.id))?.status).toBe("accepted");
  });

  it("drains a requeued mint and the creates behind it, with no cascade needed", async () => {
    const { mint, create } = await mintAndCreate();
    const refusing = fakeServer((item) =>
      item.id === mint.id ? { status: 409, body: {} } : { status: 201 },
    );
    await drainOnce({ transport: refusing.transport, isOnline: online });
    await requeue(mint.id);

    const accepting = fakeServer(() => ({ status: 201 }));
    await drainOnce({ transport: accepting.transport, isOnline: online });

    expect(accepting.sent.map((s) => s.id)).toEqual([mint.id, create.id]);
    expect(
      (await db.outbox.get(create.id))?.payload as { raceId: string | null },
    ).toMatchObject({ raceId: mint.id });
  });

  it("re-points the waiting creates at NO heat when the mint is discarded", async () => {
    // The swimmer is real; the heat is not. The server's unrequested placement
    // (ADR-0038 §2) is the only heat left to give them.
    const { mint, create } = await mintAndCreate();
    const server = fakeServer((item) =>
      item.id === mint.id ? { status: 409, body: {} } : { status: 201 },
    );
    await drainOnce({ transport: server.transport, isOnline: online });

    expect(await dependentsOf(mint.id)).toHaveLength(1);
    await discard(mint.id, "official-1");

    expect((await db.outbox.get(create.id))?.payload).toMatchObject({ raceId: null });
    const after = fakeServer(() => ({ status: 201 }));
    await drainOnce({ transport: after.transport, isOnline: online });
    expect((await db.outbox.get(create.id))?.status).toBe("accepted");
  });

  it("withdraws a queued PLACEMENT into the discarded heat instead", async () => {
    // Re-pointing it would be "remove from race, keep lane 3" — a write the
    // server refuses and a state the entry was never in.
    const mint = await enqueue({
      kind: "race.materialise",
      payload: { carnivalId: "c-1", eventId: "ev-1" },
      actorSubject: "a",
    });
    const assign = await enqueue({
      kind: "entry.assign",
      payload: { carnivalId: "c-1", entryId: "en-1", raceId: mint.id, lane: 3, heat: null },
      actorSubject: "a",
    });
    const server = fakeServer((item) =>
      item.id === mint.id ? { status: 409, body: {} } : { status: 200 },
    );
    await drainOnce({ transport: server.transport, isOnline: online });
    expect(server.sent.map((s) => s.id)).toEqual([mint.id]);

    await discard(mint.id, "official-1");
    expect((await db.outbox.get(assign.id))?.status).toBe("cancelled");
  });
});

describe("a parked mark on a high-jump card", () => {
  const mark = (entryId: string, height: number, value: "O" | "X" | "-") =>
    enqueue({
      kind: "result.jump",
      payload: { carnivalId: "c-1", carnivalEventId: "ev-1", entryId, height, mark: value },
      actorSubject: "a",
    });

  it("holds every later mark on the same athlete, and drains another athlete's", async () => {
    // The PUT names only the height and the server APPENDS: sending the third
    // tap past a parked second would store the bar as "XO" (ADR-0056 §2).
    const first = await mark("en-1", 1.4, "X");
    const second = await mark("en-1", 1.4, "X");
    const third = await mark("en-1", 1.4, "O");
    const other = await mark("en-2", 1.4, "O");
    const server = fakeServer((item) =>
      item.id === first.id ? { status: 409, body: { error: "ATHLETE_ELIMINATED" } } : { status: 200 },
    );

    const result = await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([first.id, other.id]);
    expect((await db.outbox.get(first.id))?.status).toBe("conflict");
    for (const held of [second, third]) {
      expect((await db.outbox.get(held.id))?.status).toBe("queued");
      expect((await db.outbox.get(held.id))?.attempts).toBe(0);
    }
    expect((await db.outbox.get(other.id))?.status).toBe("accepted");
    expect(result.stopReason).toBe("drained");
  });

  it("holds a card correction for that athlete too, but not a typed status", async () => {
    const parked = await mark("en-1", 1.4, "X");
    const card = await enqueue({
      kind: "result.correct",
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        value: null,
        status: null,
        jumps: [{ height: 1.4, marks: "XO" }],
        reason: "Read back",
      },
      actorSubject: "a",
    });
    const dns = await enqueue({
      kind: "result.correct",
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        value: null,
        status: "Dns",
        reason: "Never started",
      },
      actorSubject: "a",
    });
    const server = fakeServer((item) =>
      item.id === parked.id ? { status: 409, body: {} } : { status: 200 },
    );

    await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([parked.id, dns.id]);
    expect((await db.outbox.get(card.id))?.status).toBe("queued");
  });

  it("keeps holding them on a SECOND pass, where the parked row is no longer pending", async () => {
    // The hold is read off the queue, not off this pass's refusals: the parked
    // mark is not in `pendingInOrder`, so a pass that re-derived it would send
    // the marks the first pass shielded and the bar would store "XO".
    const first = await mark("en-1", 1.4, "X");
    const second = await mark("en-1", 1.4, "X");
    const third = await mark("en-1", 1.4, "O");
    const server = fakeServer((item) =>
      item.id === first.id ? { status: 409, body: {} } : { status: 200 },
    );

    await drainOnce({ transport: server.transport, isOnline: online });
    await drainOnce({ transport: server.transport, isOnline: online });
    await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([first.id]);
    for (const held of [second, third]) {
      expect((await db.outbox.get(held.id))?.status).toBe("queued");
    }
  });

  it("keeps holding them across the worker's own follow-up pass and its next poll", async () => {
    const first = await mark("en-1", 1.4, "X");
    const second = await mark("en-1", 1.4, "O");
    const server = fakeServer((item) =>
      item.id === first.id ? { status: 409, body: {} } : { status: 200 },
    );
    const worker = createSyncWorker({
      transport: server.transport,
      isOnline: online,
      sleep: async () => undefined,
    });

    await worker.syncNow();
    await worker.syncNow();

    expect(server.sent.map((s) => s.id)).toEqual([first.id]);
    expect((await db.outbox.get(second.id))?.status).toBe("queued");
  });

  it("drains the held marks in capture order once the parked one is requeued", async () => {
    const first = await mark("en-1", 1.4, "X");
    const second = await mark("en-1", 1.4, "X");
    const third = await mark("en-1", 1.4, "O");
    const refusing = fakeServer((item) =>
      item.id === first.id ? { status: 409, body: {} } : { status: 200 },
    );
    await drainOnce({ transport: refusing.transport, isOnline: online });
    await requeue(first.id);

    const accepting = fakeServer(() => ({ status: 200 }));
    await drainOnce({ transport: accepting.transport, isOnline: online });

    expect(accepting.sent.map((s) => s.id)).toEqual([first.id, second.id, third.id]);
    for (const item of [first, second, third]) {
      expect((await db.outbox.get(item.id))?.status).toBe("accepted");
    }
  });

  it("frees the held marks when the parked one is let go", async () => {
    const first = await mark("en-1", 1.4, "X");
    const second = await mark("en-1", 1.4, "O");
    const refusing = fakeServer((item) =>
      item.id === first.id ? { status: 409, body: {} } : { status: 200 },
    );
    await drainOnce({ transport: refusing.transport, isOnline: online });
    await discard(first.id, "official-1");

    const accepting = fakeServer(() => ({ status: 200 }));
    await drainOnce({ transport: accepting.transport, isOnline: online });

    expect(accepting.sent.map((s) => s.id)).toEqual([second.id]);
    expect((await db.outbox.get(second.id))?.status).toBe("accepted");
  });
});

describe("createSyncWorker — a capture chained onto a drain in flight", () => {
  it("drains an item enqueued mid-pass in the same wake, not at the next poll", async () => {
    const create = await enqueue({ kind: "race.create", payload: {}, actorSubject: "a" });
    const sent: string[] = [];
    let chained: OutboxItem | undefined;
    const ref: { worker?: SyncWorker } = {};
    const transport: Transport = async (item) => {
      sent.push(item.id);
      if (item.id === create.id) {
        // The marshalling screen chains the assign off the create and calls
        // syncNow — which coalesces onto this very drain.
        chained = await enqueue({ kind: "entry.assign", payload: {}, actorSubject: "a" });
        void ref.worker!.syncNow();
      }
      return { status: 200 };
    };
    ref.worker = createSyncWorker({ transport, isOnline: online });

    const result = await ref.worker.syncNow();

    expect(sent).toEqual([create.id, chained!.id]);
    expect(result).toMatchObject({ stopReason: "drained", accepted: 2 });
    expect((await db.outbox.get(chained!.id))?.status).toBe("accepted");
  });

  it("runs no follow-up pass when nothing arrived during the drain", async () => {
    await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const passes: DrainResult[] = [];
    const server = fakeServer(() => ({ status: 200 }));
    const worker = createSyncWorker({
      transport: server.transport,
      isOnline: online,
      afterDrain: (result) => {
        passes.push(result);
      },
    });

    await worker.syncNow();

    expect(server.sent).toHaveLength(1);
    expect(passes).toHaveLength(1);
  });

  it("bounds the follow-up at one pass, so a steady producer cannot hold the loop open", async () => {
    await enqueue({ kind: "k", payload: 0, actorSubject: "a" });
    const sent: string[] = [];
    const transport: Transport = async (item) => {
      sent.push(item.id);
      await enqueue({ kind: "k", payload: sent.length, actorSubject: "a" });
      return { status: 200 };
    };
    const worker = createSyncWorker({ transport, isOnline: online });

    const result = await worker.syncNow();

    expect(sent).toHaveLength(2); // first pass + exactly one follow-up
    expect(result).toMatchObject({ stopReason: "drained", accepted: 2 });
    // The producer's latest item waits for the poll rather than a third pass.
    expect(await db.outbox.where("status").equals("queued").count()).toBe(1);
  });

  it("a 401 pause stops the drain and skips the follow-up, dropping nothing", async () => {
    const a = await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: 2, actorSubject: "a" });
    const server = fakeServer(() => ({ status: 401 }));
    const worker = createSyncWorker({ transport: server.transport, isOnline: online });

    const result = await worker.syncNow();

    expect(result.stopReason).toBe("paused");
    expect(server.sent).toHaveLength(1); // no follow-up re-attempt
    expect((await db.outbox.get(a.id))?.status).toBe("queued");
    expect((await db.outbox.get(b.id))?.status).toBe("queued");
    expect(await db.outbox.count()).toBe(2);
    expect(await queuePauseState()).not.toBeNull();
  });

  it("a 401 in the follow-up pass pauses, and the merged result keeps the first pass's tally", async () => {
    const create = await enqueue({ kind: "race.create", payload: {}, actorSubject: "a" });
    let chained: OutboxItem | undefined;
    const transport: Transport = async (item) => {
      if (item.id === create.id) {
        chained = await enqueue({ kind: "entry.assign", payload: {}, actorSubject: "a" });
        return { status: 200 };
      }
      return { status: 401 };
    };
    const worker = createSyncWorker({ transport, isOnline: online });

    const result = await worker.syncNow();

    expect(result).toMatchObject({ stopReason: "paused", accepted: 1 });
    expect((await db.outbox.get(create.id))?.status).toBe("accepted");
    expect((await db.outbox.get(chained!.id))?.status).toBe("queued");
    expect(await queuePauseState()).not.toBeNull();
  });
});

describe("backoffDelayMs", () => {
  it("grows exponentially and caps", () => {
    const noJitter = () => 0.999999;
    expect(backoffDelayMs(1, 1000, 60000, noJitter)).toBeLessThanOrEqual(1000);
    expect(backoffDelayMs(2, 1000, 60000, noJitter)).toBeLessThanOrEqual(2000);
    expect(backoffDelayMs(2, 1000, 60000, noJitter)).toBeGreaterThan(1000);
    expect(backoffDelayMs(10, 1000, 60000, noJitter)).toBeLessThanOrEqual(60000);
  });

  it("backs a throttled drain off from the poll interval, honouring Retry-After", () => {
    const lowJitter = () => 0;
    const highJitter = () => 0.999999;
    expect(throttleDelayMs(1, 15_000, 60_000, 0, lowJitter)).toBe(15_000);
    expect(throttleDelayMs(3, 15_000, 60_000, 0, highJitter)).toBe(60_000);
    expect(throttleDelayMs(1, 15_000, 60_000, 90_000, lowJitter)).toBe(90_000);
  });

  it("jitters within [1, cap]", () => {
    for (let i = 0; i < 100; i++) {
      const d = backoffDelayMs(3, 1000, 60000);
      expect(d).toBeGreaterThanOrEqual(1);
      expect(d).toBeLessThanOrEqual(4000);
    }
  });
});

/**
 * H3: `transition(…, "syncing")` sat outside the try, so a row somebody moved
 * after the snapshot threw InvalidTransitionError all the way out through
 * `tick`, which re-armed its timer only on success — one Undo killed the poll.
 */
describe("a row that leaves `queued` between the snapshot and its send", () => {
  it("steps over a capture Undone mid-pass and drains the rest", async () => {
    const [a, b, c] = [
      await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" }),
      await enqueue({ kind: "result.submit", payload: 2, actorSubject: "a" }),
      await enqueue({ kind: "result.submit", payload: 3, actorSubject: "a" }),
    ];
    const sent: string[] = [];
    const transport: Transport = async (item) => {
      sent.push(item.id);
      // The official taps Undo on the next capture while this one is on the wire.
      if (item.id === a.id) await cancelIfPending(b.id);
      return { status: 200 };
    };

    const result = await drainOnce({ transport, isOnline: online });

    expect(sent).toEqual([a.id, c.id]);
    expect(result).toMatchObject({ stopReason: "drained", accepted: 2 });
    expect((await db.outbox.get(b.id))?.status).toBe("cancelled");
    expect((await db.outbox.get(c.id))?.status).toBe("accepted");
  });

  it("steps over one another tab already picked up, without re-sending it", async () => {
    const a = await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" });
    const b = await enqueue({ kind: "result.submit", payload: 2, actorSubject: "a" });
    const sent: string[] = [];
    const transport: Transport = async (item) => {
      sent.push(item.id);
      if (item.id === a.id) await transition(b.id, "syncing");
      return { status: 200 };
    };

    const result = await drainOnce({ transport, isOnline: online });

    expect(sent).toEqual([a.id]);
    expect(result.stopReason).toBe("drained");
  });
});

/**
 * H4: the drain reads `queued` only, so an item the app died on stayed
 * `syncing` for good — and anything queued behind it (every entry behind a
 * stranded mint) with it.
 */
describe("a row stranded in `syncing` by an interrupted app", () => {
  it("is reclaimed by the next drain and sent, with everything behind it", async () => {
    const stranded = await enqueue({ kind: "race.materialise", payload: {}, actorSubject: "a" });
    const behind = await enqueue({
      kind: "entry.create",
      payload: { carnivalId: "c-1", carnivalEventId: "ev-1", raceId: stranded.id },
      actorSubject: "a",
    });
    const start = Date.parse("2026-09-20T09:00:00.000Z");
    await transition(stranded.id, "syncing", {
      syncingAt: new Date(start - SYNCING_RECLAIM_MS - 1).toISOString(),
    });
    const server = fakeServer(() => ({ status: 201 }));

    const result = await drainOnce({
      transport: server.transport,
      isOnline: online,
      now: () => new Date(start).toISOString(),
    });

    expect(server.sent.map((s) => s.id)).toEqual([stranded.id, behind.id]);
    expect(result).toMatchObject({ stopReason: "drained", accepted: 2 });
  });

  it("leaves a row the current pass put in flight alone", async () => {
    const a = await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" });
    const b = await enqueue({ kind: "result.submit", payload: 2, actorSubject: "a" });
    const server = fakeServer(() => ({ status: 200 }));

    await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([a.id, b.id]);
    expect((await db.outbox.get(a.id))?.attempts).toBe(1);
  });
});

/**
 * H2: a result names its entry, and an entry this device minted exists nowhere
 * else until the create is accepted (ADR-0010). Sent early it 404s
 * ENTRY_NOT_FOUND, which used to land the swum time on the terminal pile.
 */
describe("a result waiting on an entry this device has only queued", () => {
  const entryAndResult = async () => {
    const entry = await enqueue({
      kind: "entry.create",
      payload: { carnivalId: "c-1", carnivalEventId: "ev-1", studentId: "st-1" },
      actorSubject: "a",
    });
    const result = await enqueue({
      kind: "result.submit",
      payload: { carnivalId: "c-1", carnivalEventId: "ev-1", entryId: entry.id, value: "31.20" },
      actorSubject: "a",
    });
    return { entry, result };
  };

  it("holds the result while the create is PARKED, and drains everything else", async () => {
    const { entry, result } = await entryAndResult();
    const unrelated = await enqueue({ kind: "adjustment.create", payload: {}, actorSubject: "a" });
    const server = fakeServer((item) =>
      item.id === entry.id ? { status: 409, body: { error: "ENTRY_EXISTS" } } : { status: 201 },
    );

    const pass = await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([entry.id, unrelated.id]);
    const held = await db.outbox.get(result.id);
    expect(held?.status).toBe("queued");
    expect(held?.attempts).toBe(0);
    expect(pass.stopReason).toBe("drained");
  });

  it("sends it the moment the create is accepted", async () => {
    const { entry, result } = await entryAndResult();
    const server = fakeServer(() => ({ status: 201 }));

    await drainOnce({ transport: server.transport, isOnline: online });

    expect(server.sent.map((s) => s.id)).toEqual([entry.id, result.id]);
    expect((await db.outbox.get(result.id))?.status).toBe("accepted");
  });

  it("parks the time as a conflict when the parked create is let go", async () => {
    // Never `rejected`, whose only exit is `discarded`: the swimmer swam, and
    // the time is retyped against a new entry from the deck.
    const { entry, result } = await entryAndResult();
    const server = fakeServer((item) =>
      item.id === entry.id ? { status: 409, body: { error: "ENTRY_EXISTS" } } : { status: 201 },
    );
    await drainOnce({ transport: server.transport, isOnline: online });

    await discard(entry.id, "official-1");

    expect((await db.outbox.get(result.id))?.status).toBe("conflict");
    const after = fakeServer(() => ({ status: 201 }));
    await drainOnce({ transport: after.transport, isOnline: online });
    expect(after.sent).toHaveLength(0);
  });
});
