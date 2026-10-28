// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { ROSTER_RETENTION_HOURS } from "../db/roster";
import { cancelIfPending, discard, enqueue, transition } from "./outbox";
import {
  ACCEPTED_RETENTION_DAYS,
  ACCEPTED_DISPLAY_GRACE_MS,
  LAST_PRUNE_KEY,
  markPruneSeen,
  pruneResolvedOutboxItems,
  unseenPrune,
  type LastPrune,
} from "./retention";
import { _resetUuidv7StateForTests } from "./uuidv7";

const NOW = new Date("2026-07-31T09:00:00.000Z");
const DAY_MS = 24 * 60 * 60 * 1000;

function daysAgo(days: number): string {
  return new Date(NOW.getTime() - days * DAY_MS).toISOString();
}

/** Enqueue and drive an item to `accepted` with a chosen acceptance time. */
async function accepted(syncedAt: string): Promise<string> {
  const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
  await transition(id, "syncing");
  await transition(id, "accepted", { syncedAt });
  return id;
}

/** Enqueue and drive an item to `discarded` at a chosen resolution time. */
async function discarded(resolvedAt: string): Promise<string> {
  const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "alice" });
  await transition(id, "syncing");
  await transition(id, "conflict", { lastError: "conflict: HTTP 409" });
  vi.setSystemTime(new Date(resolvedAt));
  await discard(id, "bob");
  vi.setSystemTime(NOW);
  return id;
}

/** Enqueue and cancel an item in its undo window at a chosen time. */
async function cancelled(at: string): Promise<string> {
  vi.setSystemTime(new Date(at));
  const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
  await cancelIfPending(id);
  vi.setSystemTime(NOW);
  return id;
}

/** A cached roster row fetched a chosen number of hours before NOW. */
async function roster(eventId: string, hoursAgo: number): Promise<void> {
  await db.eventRosters.put({
    carnivalEventId: eventId,
    students: [{ studentId: "st-a", displayName: "Ana Aardvark" }],
    fetchedAt: new Date(NOW.getTime() - hoursAgo * 60 * 60 * 1000).toISOString(),
  });
}

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
  await db.eventRosters.clear();
  localStorage.removeItem("carnival.deviceClearPending");
  // Date only: fake-indexeddb drives its transactions off setImmediate, so
  // faking the timer functions would deadlock every Dexie call in this file.
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
});

afterEach(() => {
  vi.useRealTimers();
});

describe("pruneResolvedOutboxItems (ADR-0013)", () => {
  it(`removes accepted items older than ${ACCEPTED_RETENTION_DAYS} days and keeps newer ones`, async () => {
    const old = await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    const recent = await accepted(daysAgo(ACCEPTED_RETENTION_DAYS - 1));

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect(await db.outbox.get(old)).toBeUndefined();
    expect(await db.outbox.get(recent)).toBeDefined();
  });

  it("keeps an item accepted exactly on the boundary", async () => {
    const boundary = await accepted(daysAgo(ACCEPTED_RETENTION_DAYS));

    expect(await pruneResolvedOutboxItems()).toBe(0);
    expect(await db.outbox.get(boundary)).toBeDefined();
  });

  it("keeps conflicts forever — unfinished work is never aged out", async () => {
    const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "conflict", { lastError: "conflict: HTTP 409", syncedAt: daysAgo(400) });

    await pruneResolvedOutboxItems();
    expect((await db.outbox.get(id))?.status).toBe("conflict");
  });

  it(`removes discarded items resolved over ${ACCEPTED_RETENTION_DAYS} days ago`, async () => {
    const old = await discarded(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    const recent = await discarded(daysAgo(ACCEPTED_RETENTION_DAYS - 1));

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect(await db.outbox.get(old)).toBeUndefined();
    expect(await db.outbox.get(recent)).toBeDefined();
  });

  it("ages a discarded item on when it was let go, not on when it was captured", async () => {
    // Captured long ago, discarded moments ago: it stays for the full window.
    const id = await discarded(daysAgo(1));
    await db.outbox.put({ ...(await db.outbox.get(id))!, capturedAt: daysAgo(400) });

    expect(await pruneResolvedOutboxItems()).toBe(0);
    expect(await db.outbox.get(id)).toBeDefined();
  });

  it("ages accepted and discarded work out in the same sweep", async () => {
    await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    await discarded(daysAgo(ACCEPTED_RETENTION_DAYS + 2));
    const survivor = await discarded(daysAgo(1));

    expect(await pruneResolvedOutboxItems()).toBe(2);
    expect(await db.outbox.count()).toBe(1);
    expect((await db.outbox.get(survivor))?.status).toBe("discarded");
    expect((await unseenPrune())?.prunedCount).toBe(2);
  });

  it("keeps an ancient conflict even while discarded siblings age out", async () => {
    const { id } = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "a" });
    await transition(id, "syncing");
    await transition(id, "conflict", { lastError: "conflict: HTTP 409" });
    await db.outbox.put({ ...(await db.outbox.get(id))!, capturedAt: daysAgo(400) });
    await discarded(daysAgo(ACCEPTED_RETENTION_DAYS + 5));

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect((await db.outbox.get(id))?.status).toBe("conflict");
  });

  it("never touches undelivered or rejected work", async () => {
    const queued = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    const syncing = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(syncing.id, "syncing");
    const rejected = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await transition(rejected.id, "syncing");
    await transition(rejected.id, "rejected", { syncedAt: daysAgo(90) });

    expect(await pruneResolvedOutboxItems()).toBe(0);
    expect(await db.outbox.count()).toBe(3);
    expect((await db.outbox.get(queued.id))?.status).toBe("queued");
  });

  it(`removes cancelled items over ${ACCEPTED_RETENTION_DAYS} days after cancellation (ADR-0062)`, async () => {
    const old = await cancelled(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    const recent = await cancelled(daysAgo(ACCEPTED_RETENTION_DAYS - 1));
    const boundary = await cancelled(daysAgo(ACCEPTED_RETENTION_DAYS));

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect(await db.outbox.get(old)).toBeUndefined();
    expect(await db.outbox.get(recent)).toBeDefined();
    expect(await db.outbox.get(boundary)).toBeDefined();
  });

  it("ages a cancelled row an older build left unstamped on its capture time", async () => {
    const old = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await db.outbox.put({ ...old, status: "cancelled", capturedAt: daysAgo(ACCEPTED_RETENTION_DAYS + 1) });
    const recent = await enqueue({ kind: "k", payload: {}, actorSubject: "a" });
    await db.outbox.put({ ...recent, status: "cancelled", capturedAt: daysAgo(1) });

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect(await db.outbox.get(old.id)).toBeUndefined();
    expect(await db.outbox.get(recent.id)).toBeDefined();
  });

  it("stamps a placement withdrawn with its let-go race, so it ages out too", async () => {
    const { id: raceId } = await enqueue({
      kind: "race.materialise",
      payload: {},
      actorSubject: "a",
    });
    await transition(raceId, "syncing");
    await transition(raceId, "conflict", { lastError: "conflict: HTTP 409" });
    const placement = await enqueue({
      kind: "entry.assign",
      payload: { raceId },
      actorSubject: "a",
    });
    vi.setSystemTime(new Date(daysAgo(ACCEPTED_RETENTION_DAYS + 1)));
    await discard(raceId, "bob");
    vi.setSystemTime(NOW);

    const withdrawn = await db.outbox.get(placement.id);
    expect(withdrawn?.status).toBe("cancelled");
    expect(withdrawn?.resolvedAt).toBeDefined();
    expect(await pruneResolvedOutboxItems()).toBe(2);
  });

  it("is a safe no-op on an empty queue and records nothing", async () => {
    expect(await pruneResolvedOutboxItems()).toBe(0);
    expect(await db.syncMeta.get(LAST_PRUNE_KEY)).toBeUndefined();
  });

  it("uses the device clock by default, so a start-up sweep needs no arguments", async () => {
    const old = await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 30));
    // Same call the app makes at start-up — the fake system time is the cutoff.
    await pruneResolvedOutboxItems();
    expect(await db.outbox.get(old)).toBeUndefined();
  });
});

describe("prune notice", () => {
  it("surfaces the count once, then stays acknowledged", async () => {
    await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 2));
    await pruneResolvedOutboxItems();

    const notice = await unseenPrune();
    expect(notice?.prunedCount).toBe(2);

    await markPruneSeen();
    expect(await unseenPrune()).toBeNull();
    // The record itself survives — only its unread flag changes.
    expect(((await db.syncMeta.get(LAST_PRUNE_KEY))?.value as LastPrune).prunedCount).toBe(2);
  });

  it("does not raise a notice when nothing was pruned", async () => {
    await accepted(daysAgo(1));
    await pruneResolvedOutboxItems();
    expect(await unseenPrune()).toBeNull();
  });
});

describe("roster cache sweep (ADR-0026 §3)", () => {
  it(`drops a roster older than ${ROSTER_RETENTION_HOURS}h and keeps a fresher one`, async () => {
    await roster("ev-stale", ROSTER_RETENTION_HOURS + 1);
    await roster("ev-fresh", ROSTER_RETENTION_HOURS - 1);

    await pruneResolvedOutboxItems();

    expect(await db.eventRosters.get("ev-stale")).toBeUndefined();
    expect(await db.eventRosters.get("ev-fresh")).toBeDefined();
  });

  it("keeps a roster fetched exactly on the boundary", async () => {
    await roster("ev-edge", ROSTER_RETENTION_HOURS);

    await pruneResolvedOutboxItems();

    expect(await db.eventRosters.get("ev-edge")).toBeDefined();
  });

  it("does not count evicted rosters in the outbox figure the official is shown", async () => {
    await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    await roster("ev-stale", ROSTER_RETENTION_HOURS + 1);

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect((await unseenPrune())?.prunedCount).toBe(1);
  });

  it("finishes a sign-out whose clear failed, instead of ageing rosters normally", async () => {
    // The device was signed out but could not be cleared; the fresh-looking
    // roster below is the previous official's and must go anyway.
    localStorage.setItem("carnival.deviceClearPending", "1");
    await roster("ev-fresh", 1);

    await pruneResolvedOutboxItems();

    expect(await db.eventRosters.count()).toBe(0);
    expect(localStorage.getItem("carnival.deviceClearPending")).toBeNull();
  });

  it("still prunes the outbox when roster upkeep throws", async () => {
    const old = await accepted(daysAgo(ACCEPTED_RETENTION_DAYS + 1));
    vi.spyOn(db.eventRosters, "where").mockImplementation(() => {
      throw new Error("IDB unavailable");
    });

    expect(await pruneResolvedOutboxItems()).toBe(1);
    expect(await db.outbox.get(old)).toBeUndefined();
    vi.restoreAllMocks();
  });
});

/**
 * ADR-0031 (C6): an accepted row keeps `display` through the bridge window and
 * sheds it on the first sweep after the grace period. Conflict rows keep it —
 * a human still has to identify them.
 */
describe("accepted-row display strip (ADR-0031)", () => {
  async function acceptedAt(id: string, status: "accepted" | "conflict", syncedAt: string) {
    await db.outbox.put({
      id,
      kind: "result.submit",
      payload: { entryId: "e-1", value: "35.67", display: { entryDisplayName: "Nina Novak" } },
      actorSubject: "alice",
      capturedAt: daysAgo(1),
      status,
      attempts: 1,
      syncedAt,
    });
  }

  const displayOf = async (id: string) =>
    ((await db.outbox.get(id))?.payload as { display?: unknown }).display;

  it("strips rows accepted before the grace window (deployed devices converge too)", async () => {
    const now = Date.now();
    await acceptedAt("aged", "accepted", new Date(now - ACCEPTED_DISPLAY_GRACE_MS - 1).toISOString());

    await pruneResolvedOutboxItems(now);

    expect(await displayOf("aged")).toBeUndefined();
  });

  it("keeps a fresh acceptance — the optimistic bridge still renders from it", async () => {
    const now = Date.now();
    await acceptedAt("fresh", "accepted", new Date(now - 60_000).toISOString());

    await pruneResolvedOutboxItems(now);

    expect(await displayOf("fresh")).toEqual({ entryDisplayName: "Nina Novak" });
  });

  it("leaves parked rows alone — a human still has to identify them", async () => {
    const now = Date.now();
    await acceptedAt("legacy-conflict", "conflict", daysAgo(1));

    await pruneResolvedOutboxItems(now);

    expect(await displayOf("legacy-conflict")).toEqual({ entryDisplayName: "Nina Novak" });
  });
});
