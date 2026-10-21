import Dexie from "dexie";
import { beforeEach, describe, expect, it } from "vitest";
import { db, type OutboxStatus } from "../db/db";
import {
  cancelIfPending,
  dependentsOf,
  discard,
  editAndRequeue,
  enqueue,
  InvalidTransitionError,
  isAbandoned,
  itemsByStatus,
  oldestCapturedAt,
  ORPHANED_RESULT_MESSAGE,
  OUTBOX_STATUSES,
  PARKED_STATUSES,
  pauseQueue,
  pendingInOrder,
  queuePauseState,
  queueStats,
  recentlyAccepted,
  reclaimStuckSyncing,
  requeue,
  resumeQueue,
  StaleCorrectionError,
  SYNCING_RECLAIM_MS,
  transition,
  UNDELIVERED_STATUSES,
} from "./outbox";
import { _resetUuidv7StateForTests } from "./uuidv7";

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
});

describe("enqueue", () => {
  it("persists items with a UUIDv7 id, per-item actor, and queued status", async () => {
    const item = await enqueue({
      kind: "result.submit",
      payload: { entryId: "e1", timeMs: 31_500 },
      actorSubject: "official-alice",
    });
    expect(item.id).toMatch(/^[0-9a-f-]{36}$/);
    expect(item.status).toBe("queued");
    expect(item.attempts).toBe(0);

    const stored = await db.outbox.get(item.id);
    expect(stored).toEqual(item);
  });

  it("captures the actor at entry time — handover does not re-attribute earlier items", async () => {
    const first = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "alice" });
    // Device handed over; a different official signs in and enqueues.
    const second = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "bob" });

    expect((await db.outbox.get(first.id))?.actorSubject).toBe("alice");
    expect((await db.outbox.get(second.id))?.actorSubject).toBe("bob");
  });

  it("returns pending items in capture order", async () => {
    const ids: string[] = [];
    for (let i = 0; i < 25; i++) {
      ids.push((await enqueue({ kind: "k", payload: i, actorSubject: "a" })).id);
    }
    const pending = await pendingInOrder();
    expect(pending.map((p) => p.id)).toEqual(ids);
  });
});

describe("status transitions", () => {
  it("allows queued -> syncing -> accepted", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(id, "syncing", { attempts: 1 });
    const done = await transition(id, "accepted", {
      serverResponse: { id, currentRevision: 1 },
    });
    expect(done.status).toBe("accepted");
    expect(done.serverResponse).toEqual({ id, currentRevision: 1 });
  });

  it("allows syncing -> rejected and syncing -> conflict", async () => {
    const a = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(a.id, "syncing");
    await transition(b.id, "syncing");
    expect((await transition(a.id, "rejected")).status).toBe("rejected");
    expect((await transition(b.id, "conflict")).status).toBe("conflict");
  });

  it("allows conflict -> queued (manual resolution re-queues)", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "conflict");
    expect((await transition(id, "queued")).status).toBe("queued");
  });

  it("refuses to leave terminal states — items are never resurrected or dropped", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "accepted");
    await expect(transition(id, "queued")).rejects.toThrow(InvalidTransitionError);
    await expect(transition(id, "syncing")).rejects.toThrow(InvalidTransitionError);
  });

  it("refuses skipping queued -> accepted", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await expect(transition(id, "accepted")).rejects.toThrow(InvalidTransitionError);
  });

  it("allows conflict -> discarded and rejected -> discarded (ADR-0025)", async () => {
    const a = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(a.id, "syncing");
    await transition(a.id, "conflict");
    await transition(b.id, "syncing");
    await transition(b.id, "rejected");
    expect((await transition(a.id, "discarded")).status).toBe("discarded");
    expect((await transition(b.id, "discarded")).status).toBe("discarded");
  });

  it("refuses rejected -> queued — a definitive refusal is never retried", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "rejected");
    await expect(transition(id, "queued")).rejects.toThrow(InvalidTransitionError);
    await expect(transition(id, "syncing")).rejects.toThrow(InvalidTransitionError);
    await expect(requeue(id)).rejects.toThrow(InvalidTransitionError);
    await expect(editAndRequeue(id, { fixed: true })).rejects.toThrow(InvalidTransitionError);
  });

  it("refuses to leave discarded — it is terminal", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "conflict");
    await discard(id, "official-bob");
    await expect(transition(id, "queued")).rejects.toThrow(InvalidTransitionError);
    await expect(transition(id, "syncing")).rejects.toThrow(InvalidTransitionError);
  });
});

describe("isAbandoned", () => {
  it("is exactly the two statuses where a person took the capture back", () => {
    // Pinned against the full list: a sixth status added later must be ruled
    // in or out here, not silently missed by half the consumers (ADR-0025 §2).
    expect(OUTBOX_STATUSES.filter(isAbandoned)).toEqual(["cancelled", "discarded"]);
  });
});

describe("recovery actions (ADR-0025)", () => {
  /** Enqueue and park an item on `conflict`, the state recovery acts on. */
  async function parked(payload: unknown = { timeMs: 31_500 }) {
    const item = await enqueue({ kind: "result.submit", payload, actorSubject: "official-alice" });
    await transition(item.id, "syncing", { attempts: 1 });
    await transition(item.id, "conflict", { lastError: "conflict: HTTP 409" });
    return item;
  }

  it("requeues a parked capture unchanged, keeping its id and payload", async () => {
    const item = await parked();

    const back = await requeue(item.id);
    expect(back.id).toBe(item.id);
    expect(back.status).toBe("queued");
    expect(back.payload).toEqual({ timeMs: 31_500 });
    expect((await pendingInOrder()).map((p) => p.id)).toEqual([item.id]);
  });

  it("edit-and-requeue replaces the payload and KEEPS the item's own id", async () => {
    const item = await parked({ timeMs: 3_150 });

    const fixed = await editAndRequeue(item.id, { timeMs: 31_500 });
    // The id IS the idempotency key: a fresh one would re-park as a 409 if the
    // original POST had in fact landed.
    expect(fixed.id).toBe(item.id);
    expect(fixed.status).toBe("queued");
    expect(fixed.payload).toEqual({ timeMs: 31_500 });
    expect((await db.outbox.get(item.id))?.payload).toEqual({ timeMs: 31_500 });
    expect(await db.outbox.count()).toBe(1);
  });

  it("keeps queue position on requeue, so a dependent capture still drains after", async () => {
    const create = await parked({ what: "create" });
    const assign = await enqueue({ kind: "entry.assign", payload: {}, actorSubject: "a" });

    await requeue(create.id);
    expect((await pendingInOrder()).map((p) => p.id)).toEqual([create.id, assign.id]);
  });

  it("discard records the resolving subject and leaves the capture-time actor", async () => {
    const item = await parked();

    const gone = await discard(item.id, "official-bob");
    expect(gone.status).toBe("discarded");
    expect(gone.resolvedBy).toBe("official-bob");
    expect(gone.resolvedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    // Both readable afterwards — who captured it, and who let it go.
    const stored = await db.outbox.get(item.id);
    expect(stored?.actorSubject).toBe("official-alice");
    expect(stored?.resolvedBy).toBe("official-bob");
  });

  it("discards a rejected item too — dismissal is an acknowledgement, not a retry", async () => {
    const item = await enqueue({ kind: "k", payload: {}, actorSubject: "official-alice" });
    await transition(item.id, "syncing");
    await transition(item.id, "rejected", { lastError: "rejected: HTTP 422" });

    const gone = await discard(item.id, "official-bob");
    expect(gone.status).toBe("discarded");
    expect(gone.resolvedBy).toBe("official-bob");
    // Kept, not deleted: the device row is the only evidence it existed.
    expect(await db.outbox.count()).toBe(1);
  });

  /** A correction parked because its revision lost the optimistic race. */
  async function parkedCorrection(baseVersion: number) {
    const item = await enqueue({
      kind: "result.correct",
      payload: { resultId: "res-1", value: "34.56" },
      actorSubject: "official-alice",
      baseVersion,
    });
    await transition(item.id, "syncing", { attempts: 1 });
    await transition(item.id, "conflict", {
      lastError: "conflict: HTTP 409",
      serverResponse: { error: "CONCURRENCY_CONFLICT", detail: "Revision moved on." },
    });
    return item;
  }

  it("re-bases a correction's baseVersion, which is not part of the payload", async () => {
    const item = await parkedCorrection(2);

    const fixed = await editAndRequeue(
      item.id,
      { resultId: "res-1", value: "35.12" },
      { baseVersion: 5 },
    );
    expect(fixed.status).toBe("queued");
    expect(fixed.baseVersion).toBe(5);
    expect((await db.outbox.get(item.id))?.baseVersion).toBe(5);
  });

  it("refuses an edit that would re-send the revision the server just refused", async () => {
    const item = await parkedCorrection(2);

    await expect(editAndRequeue(item.id, { resultId: "res-1", value: "35.12" })).rejects.toThrow(
      StaleCorrectionError,
    );
    // Still parked, and still carrying what was typed — nothing is lost by the
    // refusal, and nothing re-parks in an unbounded loop either.
    const stored = await db.outbox.get(item.id);
    expect(stored?.status).toBe("conflict");
    expect(stored?.baseVersion).toBe(2);
  });

  it("re-bases nothing for a first capture — only a concurrency loser needs it", async () => {
    const item = await parked({ timeMs: 3_150 });
    const fixed = await editAndRequeue(item.id, { timeMs: 31_500 });
    expect(fixed.status).toBe("queued");
    expect(fixed.baseVersion).toBeUndefined();
  });

  it("drops the stale refusal body on the way back into the queue", async () => {
    const refusal = {
      lastError: "conflict: HTTP 409",
      serverResponse: { error: "RESULT_EXISTS", detail: "Already recorded." },
    };
    const one = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    const two = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    for (const { id } of [one, two]) {
      await transition(id, "syncing");
      await transition(id, "conflict", refusal);
    }

    // A kept body describes a refusal the requeued item no longer carries, and
    // `conflictDetail` reads it straight back out onto the deck row.
    expect((await requeue(one.id)).serverResponse).toBeUndefined();
    expect((await editAndRequeue(two.id, { timeMs: 1 })).serverResponse).toBeUndefined();
    expect((await db.outbox.get(one.id))?.lastError).toBeUndefined();
  });

  it("stamps resolvedAt on any route to discarded, so the sweep's index sees it", async () => {
    const item = await parked();
    // The unguarded exported transition, not `discard` — an unstamped row is
    // omitted from [status+resolvedAt] entirely and never ages out.
    const gone = await transition(item.id, "discarded", { resolvedBy: "official-bob" });
    expect(gone.resolvedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    const indexed = await db.outbox.where("[status+resolvedAt]").between(
      ["discarded", Dexie.minKey],
      ["discarded", Dexie.maxKey],
    ).toArray();
    expect(indexed.map((i) => i.id)).toEqual([item.id]);
  });

  it("refuses recovery on an item that is not parked", async () => {
    const { id } = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await expect(requeue(id)).rejects.toThrow(InvalidTransitionError);
    await expect(discard(id, "official-bob")).rejects.toThrow(InvalidTransitionError);
  });
});

describe("cancelIfPending (undo window, U3)", () => {
  it("withdraws a still-queued capture so the drain never sees it", async () => {
    const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });

    const cancelled = await cancelIfPending(id);
    expect(cancelled?.status).toBe("cancelled");
    expect((await pendingInOrder()).map((p) => p.id)).toEqual([]);
    // Kept, not deleted: the row is local evidence of a capture taken back.
    expect(await db.outbox.count()).toBe(1);
  });

  it("refuses once the worker has picked the item up — the caller corrects instead", async () => {
    const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");

    expect(await cancelIfPending(id)).toBeNull();
    expect((await db.outbox.get(id))?.status).toBe("syncing");
  });

  it("refuses on a terminal item and is a no-op for an unknown id", async () => {
    const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "accepted");

    expect(await cancelIfPending(id)).toBeNull();
    expect(await cancelIfPending("no-such-item")).toBeNull();
  });

  it("cannot be resurrected once cancelled", async () => {
    const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await cancelIfPending(id);
    await expect(transition(id, "queued")).rejects.toThrow(InvalidTransitionError);
    await expect(transition(id, "syncing")).rejects.toThrow(InvalidTransitionError);
  });
});

describe("pause / resume", () => {
  it("records and clears the pause flag without touching items", async () => {
    await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await pauseQueue("auth expired");
    expect((await queuePauseState())?.reason).toBe("auth expired");
    expect(await db.outbox.count()).toBe(1);
    await resumeQueue();
    expect(await queuePauseState()).toBeNull();
    expect(await db.outbox.count()).toBe(1);
  });
});

describe("queueStats", () => {
  it("aggregates counts by status and exposes the pause state", async () => {
    const a = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    const b = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(a.id, "syncing");
    await transition(a.id, "accepted");
    await transition(b.id, "syncing");
    await transition(b.id, "conflict");
    await pauseQueue("auth");

    const stats = await queueStats();
    expect(stats.total).toBe(3);
    expect(stats.byStatus).toEqual({
      queued: 1,
      syncing: 0,
      accepted: 1,
      rejected: 0,
      conflict: 1,
      cancelled: 0,
      discarded: 0,
    });
    expect(stats.paused).toBe(true);
    expect(stats.pausedReason).toBe("auth");
  });

  it("counts every status off the index, including a large accepted tail (P3)", async () => {
    for (let i = 0; i < 40; i++) {
      const { id } = await enqueue({ kind: "k", payload: i, actorSubject: "a" });
      await transition(id, "syncing");
      await transition(id, "accepted", { syncedAt: new Date().toISOString() });
    }
    await enqueue({ kind: "k", payload: "waiting", actorSubject: "a" });

    const stats = await queueStats();
    expect(stats.total).toBe(41);
    expect(stats.byStatus.accepted).toBe(40);
    expect(stats.byStatus.queued).toBe(1);
  });
});

describe("index-scoped reads (P3 / ADR-0013)", () => {
  it("returns only the requested statuses, in capture order", async () => {
    const first = await enqueue({ kind: "k", payload: 1, actorSubject: "a" });
    const second = await enqueue({ kind: "k", payload: 2, actorSubject: "a" });
    const third = await enqueue({ kind: "k", payload: 3, actorSubject: "a" });
    await transition(second.id, "syncing");
    await transition(third.id, "syncing");
    await transition(third.id, "accepted", { syncedAt: new Date().toISOString() });

    const active = await itemsByStatus(["queued", "syncing"]);
    expect(active.map((i) => i.id)).toEqual([first.id, second.id]);
    expect(await itemsByStatus(["conflict"])).toEqual([]);
  });

  it("windows the synced tail by sync time, not by capture order", async () => {
    // The early capture parked, or the device was offline, and it drained last.
    // Windowing on the primary key would drop it out of a tail it belongs at
    // the head of.
    const early = await enqueue({ kind: "k", payload: "early", actorSubject: "a" });
    for (let i = 0; i < 3; i++) {
      const { id } = await enqueue({ kind: "k", payload: i, actorSubject: "a" });
      await transition(id, "syncing");
      await transition(id, "accepted", { syncedAt: "2026-08-20T01:00:00.000Z" });
    }
    await transition(early.id, "syncing");
    await transition(early.id, "accepted", { syncedAt: "2026-08-20T09:00:00.000Z" });

    expect((await recentlyAccepted(1)).map((i) => i.id)).toEqual([early.id]);
  });

  it("reads the accepted tail newest-first and bounded by the limit", async () => {
    const ids: string[] = [];
    for (let i = 0; i < 5; i++) {
      const { id } = await enqueue({ kind: "k", payload: i, actorSubject: "a" });
      await transition(id, "syncing");
      await transition(id, "accepted", { syncedAt: new Date().toISOString() });
      ids.push(id);
    }
    // A still-queued item must never show up in the synced tail.
    await enqueue({ kind: "k", payload: "waiting", actorSubject: "a" });

    const recent = await recentlyAccepted(3);
    expect(recent.map((i) => i.id)).toEqual([ids[4], ids[3], ids[2]]);
  });
});

describe("oldestCapturedAt (ADR-0029)", () => {
  /** Enqueue with a chosen capture time — the age the stuck rule reads. */
  async function captureAt(capturedAt: string, status: OutboxStatus = "queued") {
    const item = await enqueue({ kind: "k", payload: capturedAt, actorSubject: "a" });
    await db.outbox.put({ ...item, capturedAt, status });
    return item.id;
  }

  it("is null when nothing is in those statuses", async () => {
    await captureAt("2027-03-06T09:00:00.000Z", "accepted");
    expect(await oldestCapturedAt(UNDELIVERED_STATUSES)).toBeNull();
  });

  it("returns the head item's capture time, not the newest", async () => {
    await captureAt("2027-03-06T09:00:00.000Z");
    await captureAt("2027-03-06T11:00:00.000Z");
    expect(await oldestCapturedAt(UNDELIVERED_STATUSES)).toBe("2027-03-06T09:00:00.000Z");
  });

  it("spans the statuses given — an item in flight is still undelivered", async () => {
    await captureAt("2027-03-06T10:00:00.000Z", "syncing");
    await captureAt("2027-03-06T10:30:00.000Z", "queued");
    expect(await oldestCapturedAt(UNDELIVERED_STATUSES)).toBe("2027-03-06T10:00:00.000Z");
  });

  it("does not let a resolved or parked item age the live queue", async () => {
    await captureAt("2027-03-06T08:00:00.000Z", "conflict");
    await captureAt("2027-03-06T08:15:00.000Z", "cancelled");
    await captureAt("2027-03-06T10:45:00.000Z", "queued");
    expect(await oldestCapturedAt(UNDELIVERED_STATUSES)).toBe("2027-03-06T10:45:00.000Z");
    expect(await oldestCapturedAt(PARKED_STATUSES)).toBe("2027-03-06T08:00:00.000Z");
  });

  it("reads parked work across both refusal statuses", async () => {
    await captureAt("2027-03-06T09:30:00.000Z", "rejected");
    await captureAt("2027-03-06T09:45:00.000Z", "conflict");
    expect(await oldestCapturedAt(PARKED_STATUSES)).toBe("2027-03-06T09:30:00.000Z");
  });
});

/**
 * ADR-0031: `payload.display` is the student's name, held only to render the
 * outbox row and never sent. Acceptance means the server holds the record, so
 * the name has nothing left to do on the device.
 */
describe("accepted rows shed the student name (ADR-0031, C6)", () => {
  async function delivered(status: OutboxStatus): Promise<string> {
    const { id } = await enqueue({
      kind: "result.submit",
      payload: { entryId: "e-1", value: "35.67", display: { entryDisplayName: "Nina Novak" } },
      actorSubject: "alice",
    });
    await transition(id, "syncing");
    await transition(id, status, { syncedAt: "2026-08-22T01:00:00.000Z" });
    return id;
  }

  it("KEEPS display through the accepted transition — the optimistic bridge still reads it", async () => {
    // The strip is the retention sweep's, an hour later (retention.test.ts):
    // stripping here made a just-placed swimmer vanish for the refetch round trip.
    const id = await delivered("accepted");
    const payload = (await db.outbox.get(id))?.payload as Record<string, unknown>;
    expect(payload.display).toEqual({ entryDisplayName: "Nina Novak" });
    expect(payload.entryId).toBe("e-1");
    expect(payload.value).toBe("35.67");
  });

  it("KEEPS display on a conflict — a human has to know whose capture it is", async () => {
    const id = await delivered("conflict");
    const payload = (await db.outbox.get(id))?.payload as { display?: unknown };
    expect(payload.display).toEqual({ entryDisplayName: "Nina Novak" });
  });

  it("keeps display on a rejection, for the same reason", async () => {
    const id = await delivered("rejected");
    const payload = (await db.outbox.get(id))?.payload as { display?: unknown };
    expect(payload.display).toEqual({ entryDisplayName: "Nina Novak" });
  });

  it("is a safe no-op on a payload that never carried one", async () => {
    const { id } = await enqueue({
      kind: "race.create",
      payload: { carnivalId: "c-1" },
      actorSubject: "a",
    });
    await transition(id, "syncing");
    await transition(id, "accepted", { syncedAt: "2026-08-22T01:00:00.000Z" });
    expect((await db.outbox.get(id))?.payload).toEqual({ carnivalId: "c-1" });
  });
});

/**
 * ADR-0031: a second official may fix a parked capture, but the capture stays
 * the FIRST official's — it drains under their X-Captured-Actor (ADR-0002). The
 * edit is recorded beside the capture, never over it.
 */
describe("edit attribution (ADR-0031)", () => {
  async function parkedByAlice() {
    const item = await enqueue({
      kind: "result.submit",
      payload: { entryId: "e-1", value: "35.67" },
      actorSubject: "alice",
      actorDisplayName: "Alice Official",
    });
    await transition(item.id, "syncing");
    await transition(item.id, "conflict", { lastError: "conflict: HTTP 409" });
    return item;
  }

  it("records a DIFFERENT official's edit without re-attributing the capture", async () => {
    const item = await parkedByAlice();

    const fixed = await editAndRequeue(
      item.id,
      { entryId: "e-1", value: "35.12" },
      { editedBy: { subject: "bob", displayName: "Bob Marshal" } },
    );

    expect(fixed.actorSubject).toBe("alice");
    expect(fixed.editedBySubject).toBe("bob");
    expect(fixed.editedByDisplayName).toBe("Bob Marshal");
  });

  it("records nothing when the capturer fixes their own capture", async () => {
    const item = await parkedByAlice();

    const fixed = await editAndRequeue(
      item.id,
      { entryId: "e-1", value: "35.12" },
      { editedBy: { subject: "alice", displayName: "Alice Official" } },
    );

    expect(fixed.editedBySubject).toBeUndefined();
  });

  it("still refuses a stale correction when only an editor is supplied", async () => {
    // `editedBy` is attribution, not a re-base: it must not smuggle a
    // concurrency loser back into the queue unchanged.
    const item = await enqueue({
      kind: "result.correct",
      payload: { resultId: "res-1", value: "34.56" },
      actorSubject: "alice",
      baseVersion: 2,
    });
    await transition(item.id, "syncing");
    await transition(item.id, "conflict", {
      serverResponse: { error: "CONCURRENCY_CONFLICT", detail: "Revision moved on." },
    });

    await expect(
      editAndRequeue(
        item.id,
        { resultId: "res-1", value: "35.12" },
        { editedBy: { subject: "bob" } },
      ),
    ).rejects.toThrow(StaleCorrectionError);
  });
});

/**
 * H4: nothing but this ever moves a row out of `syncing`. An app reaped
 * mid-request left the capture invisible to the drain and unrecoverable from
 * the screen, while the ADR-0029 banner counted it as pending forever.
 */
describe("reclaiming a row an interrupted app left in `syncing`", () => {
  const stranded = async (syncingAt: string) => {
    const item = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await transition(item.id, "syncing", { syncingAt });
    return item;
  };
  const now = Date.parse("2026-09-20T09:00:00.000Z");
  const iso = (msAgo: number) => new Date(now - msAgo).toISOString();

  it("requeues one older than the threshold and leaves a fresh one in flight", async () => {
    const old = await stranded(iso(SYNCING_RECLAIM_MS + 1_000));
    const fresh = await stranded(iso(1_000));

    expect(await reclaimStuckSyncing(now)).toBe(1);

    const reclaimed = await db.outbox.get(old.id);
    expect(reclaimed?.status).toBe("queued");
    expect(reclaimed?.lastError).toBeTruthy();
    expect((await db.outbox.get(fresh.id))?.status).toBe("syncing");
  });

  it("puts the reclaimed item back in the drain's own read, in capture order", async () => {
    const first = await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" });
    await transition(first.id, "syncing", { syncingAt: iso(SYNCING_RECLAIM_MS * 2) });
    const second = await enqueue({ kind: "result.submit", payload: 2, actorSubject: "a" });

    await reclaimStuckSyncing(now);

    expect((await pendingInOrder()).map((i) => i.id)).toEqual([first.id, second.id]);
  });

  it("reclaims a row stamped by a build that wrote no syncingAt", async () => {
    // The stamp is new, so a row stranded before this build carries none — and
    // such a row cannot be in flight.
    const item = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await db.outbox.put({
      ...(await db.outbox.get(item.id))!,
      status: "syncing",
      capturedAt: iso(SYNCING_RECLAIM_MS + 1),
    });

    expect(await reclaimStuckSyncing(now)).toBe(1);
    expect((await db.outbox.get(item.id))?.status).toBe("queued");
  });

  it("touches nothing in any other status", async () => {
    const queued = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    const accepted = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(accepted.id, "syncing", { syncingAt: iso(SYNCING_RECLAIM_MS * 3) });
    await transition(accepted.id, "accepted");

    expect(await reclaimStuckSyncing(now)).toBe(0);
    expect((await db.outbox.get(queued.id))?.status).toBe("queued");
    expect((await db.outbox.get(accepted.id))?.status).toBe("accepted");
  });
});

/**
 * H2, second door: `dependentsOf` used to read `raceId` only, so a result
 * captured against a locally minted entry was invisible to the discard and sat
 * queued against an entry that would never exist.
 */
describe("letting a queued entry.create go", () => {
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

  it("counts the results captured against it as dependants", async () => {
    const { entry, result } = await entryAndResult();
    expect((await dependentsOf(entry.id)).map((i) => i.id)).toEqual([result.id]);
  });

  it("parks the swum time for a person instead of dropping or sending it", async () => {
    // It cannot be re-pointed (there is no "no entry" to point at) and it must
    // not be cancelled: nobody asked for the time to go, only the entry.
    const { entry, result } = await entryAndResult();
    await transition(entry.id, "syncing");
    await transition(entry.id, "conflict", { serverResponse: { error: "ENTRY_EXISTS" } });

    await discard(entry.id, "official-1");

    const parked = await db.outbox.get(result.id);
    expect(parked?.status).toBe("conflict");
    expect(parked?.lastError).toBe(ORPHANED_RESULT_MESSAGE);
    expect((await db.outbox.get(entry.id))?.status).toBe("discarded");
  });
});
