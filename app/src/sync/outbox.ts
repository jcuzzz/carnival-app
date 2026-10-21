import Dexie from "dexie";
import { ErrorCodes, errorCodeOf } from "../api/errorCodes";
import {
  ENTRY_CREATE_KIND,
  RESULT_ATTEMPT_KIND,
  RESULT_CORRECT_KIND,
  RESULT_JUMP_KIND,
  RESULT_SUBMIT_KIND,
  TEAM_CREATE_KIND,
} from "../api/outboxTransport";
import { db, type OutboxItem, type OutboxStatus } from "../db/db";
import { uuidv7 } from "./uuidv7";

/**
 * Sync logic never deletes an item: undo is the `cancelled` status, aging out is `retention.ts`.
 * An auth failure pauses the queue and drops nothing.
 */

export const QUEUE_PAUSED_KEY = "outbox.paused";

export interface EnqueueInput {
  kind: string;
  payload: unknown;
  /** From the current session, captured now: a later handover never re-attributes the item. */
  actorSubject: string;
  actorDisplayName?: string;
  /** Corrections only. */
  baseVersion?: number;
}

export async function enqueue(input: EnqueueInput): Promise<OutboxItem> {
  const item: OutboxItem = {
    id: uuidv7(),
    kind: input.kind,
    payload: input.payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
    capturedAt: new Date().toISOString(),
    status: "queued",
    attempts: 0,
    ...(input.baseVersion !== undefined ? { baseVersion: input.baseVersion } : {}),
  };
  await db.outbox.add(item);
  return item;
}

const TRANSITIONS: Record<OutboxStatus, readonly OutboxStatus[]> = {
  // cancelled only via cancelIfPending; conflict only via `discard` of the create this item names.
  queued: ["syncing", "cancelled", "conflict"],
  syncing: ["accepted", "rejected", "conflict", "queued"],
  accepted: [],
  rejected: ["discarded"], // a definitive refusal is never retried
  conflict: ["queued", "discarded"],
  cancelled: [],
  discarded: [],
};

/** Every "does this item still speak for its row?" check asks this, so they cannot drift apart. */
export function isAbandoned(status: OutboxStatus): boolean {
  return status === "cancelled" || status === "discarded";
}

/** Re-sending the same `baseVersion` parks again; it can only requeue re-based on the server's. */
export function needsRebase(item: OutboxItem): boolean {
  if (item.baseVersion === undefined) return false;
  return errorCodeOf(item.serverResponse) === ErrorCodes.ConcurrencyConflict;
}

export const STALE_CORRECTION_MESSAGE =
  "This correction needs the result list to know which revision it amends — reconnect, or let it go from the outbox.";

export class StaleCorrectionError extends Error {
  constructor() {
    super(STALE_CORRECTION_MESSAGE);
    this.name = "StaleCorrectionError";
  }
}

export class InvalidTransitionError extends Error {
  constructor(id: string, from: OutboxStatus, to: OutboxStatus) {
    super(`Outbox item ${id}: illegal status transition ${from} -> ${to}`);
    this.name = "InvalidTransitionError";
  }
}

/** Terminal states are never left, so items are never silently dropped or resurrected. */
export async function transition(
  id: string,
  to: OutboxStatus,
  patch?: Partial<
    Pick<
      OutboxItem,
      | "attempts"
      | "lastError"
      | "serverResponse"
      | "syncedAt"
      | "payload"
      | "baseVersion"
      | "resolvedBy"
      | "resolvedAt"
      | "editedBySubject"
      | "editedByDisplayName"
      | "syncingAt"
    >
  >,
): Promise<OutboxItem> {
  return db.transaction("rw", db.outbox, async () => {
    const item = await db.outbox.get(id);
    if (!item) throw new Error(`Outbox item ${id} not found`);
    if (!TRANSITIONS[item.status].includes(to)) {
      throw new InvalidTransitionError(id, item.status, to);
    }
    // Dexie omits a row whose compound-index component is undefined, so an unstamped discard is
    // invisible to the retention sweep's [status+resolvedAt] read.
    const stamp =
      to === "discarded" && patch?.resolvedAt === undefined
        ? { resolvedAt: new Date().toISOString() }
        : {};
    const updated: OutboxItem = { ...item, ...patch, ...stamp, status: to };
    await db.outbox.put(updated);
    return updated;
  });
}

export function withoutDisplay(payload: unknown): unknown {
  if (payload === null || typeof payload !== "object") return payload;
  if (!("display" in payload)) return payload;
  const copy = { ...(payload as Record<string, unknown>) };
  delete copy.display;
  return copy;
}

/**
 * Not `transition(id, "cancelled")`: read and write share one transaction, so an item the drain
 * grabbed in between stays `syncing` and the caller gets null.
 */
export async function cancelIfPending(id: string): Promise<OutboxItem | null> {
  return db.transaction("rw", db.outbox, async () => {
    const item = await db.outbox.get(id);
    if (!item || item.status !== "queued") return null;
    const updated: OutboxItem = {
      ...item,
      status: "cancelled",
      resolvedAt: new Date().toISOString(),
    };
    await db.outbox.put(updated);
    return updated;
  });
}

/** Its id is untouched, so it re-enters the queue at its original position. */
export async function requeue(id: string): Promise<OutboxItem> {
  // `conflictDetail` reads the stored refusal, which would describe a refusal no longer carried.
  return transition(id, "queued", { lastError: undefined, serverResponse: undefined });
}

export interface EditContext {
  /** Top-level, not part of the payload. */
  baseVersion?: number;
  /** Recorded only if it is not the capturer. */
  editedBy?: { subject: string; displayName?: string };
}

/**
 * The item keeps its own id as the idempotency key: a fresh key would park again as a 409 if the
 * original POST had in fact landed.
 */
export async function editAndRequeue(
  id: string,
  payload: unknown,
  edit?: EditContext,
): Promise<OutboxItem> {
  return db.transaction("rw", db.outbox, async () => {
    const item = await db.outbox.get(id);
    if (item && edit?.baseVersion === undefined && needsRebase(item)) {
      throw new StaleCorrectionError();
    }
    // Stamped beside `actorSubject`, never over it.
    const editor =
      edit?.editedBy && item && edit.editedBy.subject !== item.actorSubject
        ? {
            editedBySubject: edit.editedBy.subject,
            ...(edit.editedBy.displayName !== undefined
              ? { editedByDisplayName: edit.editedBy.displayName }
              : {}),
          }
        : {};
    return transition(id, "queued", {
      payload,
      lastError: undefined,
      serverResponse: undefined,
      ...(edit?.baseVersion !== undefined ? { baseVersion: edit.baseVersion } : {}),
      ...editor,
    });
  });
}

/** An item's id is the id it mints, so a write may name a race or entry still queued here. */
export function dependencyIdsOf(item: OutboxItem): string[] {
  const payload = item.payload as { raceId?: unknown; entryId?: unknown } | null | undefined;
  return [payload?.raceId, payload?.entryId].filter(
    (id): id is string => typeof id === "string" && id !== item.id,
  );
}

/**
 * The server has never seen a race or entry this device only minted, so a write naming one would
 * park with no way home (requeue re-sends the same dead id); it waits queued instead.
 */
export async function blockedByDependency(item: OutboxItem): Promise<boolean> {
  for (const id of dependencyIdsOf(item)) {
    const dependency = await db.outbox.get(id);
    if (dependency !== undefined && dependency.status !== "accepted") return true;
  }
  return false;
}

/**
 * The mark PUT carries only the height and the server appends, so order within a card is the
 * wire contract.
 */
export function cardTargetOf(item: OutboxItem): string | null {
  return isCardMark(item) || isCardCorrection(item) ? cardKeyOf(item) : null;
}

export function isCardMark(item: OutboxItem): boolean {
  return item.kind === RESULT_JUMP_KIND && cardKeyOf(item) !== null;
}

export function isCardCorrection(item: OutboxItem): boolean {
  return (
    item.kind === RESULT_CORRECT_KIND &&
    (item.payload as { jumps?: unknown } | null | undefined)?.jumps != null &&
    cardKeyOf(item) !== null
  );
}

/** Sending past a parked mark stores the bar's marks out of order and the requeue is refused. */
export function heldCardKeys(items: readonly OutboxItem[]): Set<string> {
  const keys = new Set<string>();
  for (const item of items) {
    if (!isCardMark(item)) continue;
    if (item.status !== "conflict" && item.status !== "rejected") continue;
    const key = cardKeyOf(item);
    if (key !== null) keys.add(key);
  }
  return keys;
}

export async function parkedCardKeys(): Promise<Set<string>> {
  const parked = await db.outbox
    .where("status")
    .anyOf(PARKED_STATUSES as unknown as OutboxStatus[])
    .toArray();
  return heldCardKeys(parked);
}

export function isHeldBehindParkedMark(item: OutboxItem, held: ReadonlySet<string>): boolean {
  const key = cardTargetOf(item);
  return key !== null && held.has(key);
}

function cardKeyOf(item: OutboxItem): string | null {
  const payload = item.payload as
    | { carnivalEventId?: unknown; entryId?: unknown }
    | null
    | undefined;
  if (payload === null || typeof payload !== "object") return null;
  const { carnivalEventId, entryId } = payload;
  if (typeof carnivalEventId !== "string" || typeof entryId !== "string") return null;
  return `${carnivalEventId}/${entryId}`;
}

export async function dependentsOf(id: string): Promise<OutboxItem[]> {
  const queued = await db.outbox.where("status").equals("queued").toArray();
  return queued.filter((i) => i.id !== id && dependencyIdsOf(i).includes(id));
}

/** Compared at call time: this module and `outboxTransport` import each other. */
export function isResultCapture(item: OutboxItem): boolean {
  return (
    item.kind === RESULT_SUBMIT_KIND ||
    item.kind === RESULT_CORRECT_KIND ||
    item.kind === RESULT_ATTEMPT_KIND ||
    item.kind === RESULT_JUMP_KIND
  );
}

export const ORPHANED_RESULT_MESSAGE =
  "The entry this was captured against was let go — retype it from the event's deck against a new entry, or let it go.";

/**
 * A let-go mint re-points its queued adds at no heat (real swimmers); a let-go entry parks its
 * results, since a swum time is never dropped on somebody else's action.
 */
export async function discard(id: string, resolvedBy: string): Promise<OutboxItem> {
  return db.transaction("rw", db.outbox, async () => {
    for (const dependent of await dependentsOf(id)) {
      if (dependent.kind === ENTRY_CREATE_KIND || dependent.kind === TEAM_CREATE_KIND) {
        const payload = { ...(dependent.payload as Record<string, unknown>), raceId: null };
        await db.outbox.put({ ...dependent, payload });
      } else if (isResultCapture(dependent)) {
        await transition(dependent.id, "conflict", { lastError: ORPHANED_RESULT_MESSAGE });
      } else {
        // A placement into a race that will never exist is withdrawn with it.
        await db.outbox.put({
          ...dependent,
          status: "cancelled" as OutboxStatus,
          resolvedAt: new Date().toISOString(),
        });
      }
    }
    return transition(id, "discarded", { resolvedBy });
  });
}

/**
 * Longer than any request the transport waits on, so it never races one in flight; short enough
 * that a relaunch after an iOS reap recovers in one poll.
 */
export const SYNCING_RECLAIM_MS = 30_000;

export const RECLAIMED_MESSAGE = "Interrupted mid-send — put back in the queue.";

/**
 * Nothing else moves a row killed mid-request out of `syncing`. Every kind but `result.correct`
 * is idempotency-keyed; a correction that did land answers CONCURRENCY_CONFLICT and parks.
 */
export async function reclaimStuckSyncing(
  nowMs: number,
  olderThanMs: number = SYNCING_RECLAIM_MS,
): Promise<number> {
  const rows = await db.outbox.where("status").equals("syncing").toArray();
  let reclaimed = 0;
  for (const item of rows) {
    // No stamp: an older build wrote the row, so it cannot be in flight.
    const since = Date.parse(item.syncingAt ?? item.capturedAt);
    if (nowMs - since < olderThanMs) continue;
    try {
      await transition(item.id, "queued", { lastError: RECLAIMED_MESSAGE });
      reclaimed += 1;
    } catch {
      // Another tab finished the item between the read and the write.
    }
  }
  return reclaimed;
}

/** Capture order: UUIDv7 ids sort chronologically. */
export async function pendingInOrder(): Promise<OutboxItem[]> {
  const items = await db.outbox.where("status").equals("queued").toArray();
  return items.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
}

export async function pauseQueue(reason: string): Promise<void> {
  await db.syncMeta.put({ key: QUEUE_PAUSED_KEY, value: { reason, at: new Date().toISOString() } });
}

export async function resumeQueue(): Promise<void> {
  await db.syncMeta.delete(QUEUE_PAUSED_KEY);
}

export async function queuePauseState(): Promise<{ reason: string; at: string } | null> {
  const row = await db.syncMeta.get(QUEUE_PAUSED_KEY);
  return row ? (row.value as { reason: string; at: string }) : null;
}

export const OUTBOX_STATUSES: readonly OutboxStatus[] = [
  "queued",
  "syncing",
  "accepted",
  "rejected",
  "conflict",
  "cancelled",
  "discarded",
];

export interface QueueStats {
  total: number;
  byStatus: Record<OutboxStatus, number>;
  paused: boolean;
  pausedReason?: string;
}

/** One indexed `count()` per status: runs on every Dexie change, so it never materialises a row. */
export async function queueStats(): Promise<QueueStats> {
  const [counts, pause] = await Promise.all([
    Promise.all(OUTBOX_STATUSES.map((s) => db.outbox.where("status").equals(s).count())),
    queuePauseState(),
  ]);
  const byStatus = Object.fromEntries(
    OUTBOX_STATUSES.map((status, i) => [status, counts[i]!]),
  ) as Record<OutboxStatus, number>;
  return {
    total: counts.reduce((sum, n) => sum + n, 0),
    byStatus,
    paused: pause !== null,
    ...(pause ? { pausedReason: pause.reason } : {}),
  };
}

export async function itemsByStatus(
  statuses: readonly OutboxStatus[],
): Promise<OutboxItem[]> {
  const items = await db.outbox
    .where("status")
    .anyOf(statuses as OutboxStatus[])
    .toArray();
  // anyOf yields status order.
  return items.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
}

export const UNDELIVERED_STATUSES = ["queued", "syncing"] as const;

export const PARKED_STATUSES = ["conflict", "rejected"] as const;

/** `.first()` off the `status` index is the head: Dexie orders equal index keys by primary key. */
export async function oldestCapturedAt(
  statuses: readonly OutboxStatus[],
): Promise<string | null> {
  const heads = await Promise.all(
    statuses.map((status) => db.outbox.where("status").equals(status).first()),
  );
  let oldest: string | null = null;
  for (const item of heads) {
    if (item && (oldest === null || item.capturedAt < oldest)) oldest = item.capturedAt;
  }
  return oldest;
}

/** Windowed by syncedAt, not id: an item captured early and accepted late belongs in the window. */
export async function recentlyAccepted(limit: number): Promise<OutboxItem[]> {
  return db.outbox
    .where("[status+syncedAt]")
    .between(["accepted", Dexie.minKey], ["accepted", Dexie.maxKey])
    .reverse()
    .limit(limit)
    .toArray();
}
