import Dexie from "dexie";
import { clearDeviceCaches, deviceClearPending } from "../auth/session";
import { db, type OutboxItem } from "../db/db";
import { pruneStaleRosters } from "../db/roster";
import { withoutDisplay } from "./outbox";

/**
 * The server's audit trail is the durable record, so `accepted`, `discarded` and `cancelled` rows
 * age out; a `conflict` is unfinished work and is kept forever.
 */

export const ACCEPTED_RETENTION_DAYS = 7;

const RETENTION_MS = ACCEPTED_RETENTION_DAYS * 24 * 60 * 60 * 1000;

export const LAST_PRUNE_KEY = "outbox.lastPrune";

/** An hour: far past the bridge, and short because the student name must not live on the device. */
export const ACCEPTED_DISPLAY_GRACE_MS = 60 * 60 * 1000;

export interface LastPrune {
  prunedCount: number;
  at: string;
  /** The count is shown once only. */
  seen: boolean;
}

const RESOLVED_RANGES = [
  ["[status+syncedAt]", "accepted"],
  ["[status+resolvedAt]", "discarded"],
  ["[status+resolvedAt]", "cancelled"],
] as const;

/**
 * Returns how many outbox items went; roster evictions are not reported, since an evicted cache of
 * a server read is not the official's business.
 */
export async function pruneResolvedOutboxItems(now: number = Date.now()): Promise<number> {
  const cutoff = new Date(now - RETENTION_MS).toISOString();
  let pruned = 0;
  for (const [index, status] of RESOLVED_RANGES) {
    pruned += await db.outbox
      .where(index)
      // Upper bound exclusive: an item resolved exactly on the boundary stays.
      .between([status, Dexie.minKey], [status, cutoff], true, false)
      .delete();
  }
  pruned += await pruneUnstampedCancelled(cutoff);
  await sweepAcceptedDisplay(now);
  await sweepRosterCache(now);
  if (pruned > 0) {
    await db.syncMeta.put({
      key: LAST_PRUNE_KEY,
      value: { prunedCount: pruned, at: new Date(now).toISOString(), seen: false } as LastPrune,
    });
  }
  return pruned;
}

/** Older builds cancelled without `resolvedAt`; the undo window makes capture time a close proxy. */
async function pruneUnstampedCancelled(cutoff: string): Promise<number> {
  return db.outbox
    .where("status")
    .equals("cancelled")
    .filter((item) => item.resolvedAt === undefined && item.capturedAt < cutoff)
    .delete();
}

export async function unseenPrune(): Promise<LastPrune | null> {
  const row = await db.syncMeta.get(LAST_PRUNE_KEY);
  const value = row?.value as LastPrune | undefined;
  return value && !value.seen ? value : null;
}

export async function markPruneSeen(): Promise<void> {
  const row = await db.syncMeta.get(LAST_PRUNE_KEY);
  const value = row?.value as LastPrune | undefined;
  if (!value || value.seen) return;
  await db.syncMeta.put({ key: LAST_PRUNE_KEY, value: { ...value, seen: true } });
}

/**
 * Not at the accepted transition: the optimistic overlay renders a fresh acceptance from
 * `display` until the refetch lands.
 */
async function sweepAcceptedDisplay(now: number): Promise<void> {
  try {
    const cutoff = new Date(now - ACCEPTED_DISPLAY_GRACE_MS).toISOString();
    await db.outbox
      .where("[status+syncedAt]")
      .between(["accepted", Dexie.minKey], ["accepted", cutoff], true, false)
      .modify((item: OutboxItem) => {
        const stripped = withoutDisplay(item.payload);
        if (stripped !== item.payload) item.payload = stripped;
      });
  } catch {
    // Convergence is upkeep: it must never fail the sweep it rides on.
  }
}

/**
 * Finishes a sign-out that could not clear the device: the sweep runs at app start, before anyone
 * signs in, so a failed clear costs at most one reload.
 */
async function sweepRosterCache(now: number): Promise<void> {
  try {
    if (deviceClearPending()) await clearDeviceCaches();
    else await pruneStaleRosters(now);
  } catch {
    // Cache upkeep must never fail the outbox sweep it rides on.
  }
}
