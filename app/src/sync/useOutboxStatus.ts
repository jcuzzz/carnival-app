import { useLiveQuery } from "dexie-react-hooks";
import type { OutboxItem, OutboxStatus } from "../db/db";
import { db } from "../db/db";
import {
  itemsByStatus,
  oldestCapturedAt,
  queueStats,
  recentlyAccepted,
  type QueueStats,
} from "./outbox";

/** These re-run on every Dexie change during a carnival, so each is index-scoped to its rows. */
export function useQueueStats(): QueueStats | undefined {
  return useLiveQuery(queueStats, []);
}

/**
 * No argument reads every status: an `accepted` capture keeps owning its row's badge until the
 * result list catches up. Callers that only want undelivered work should pass their statuses.
 */
export function useOutboxItems(statuses?: readonly OutboxStatus[]): OutboxItem[] | undefined {
  // Dexie returns a table in primary-key (capture) order, so the unscoped path needs no sort.
  const key = statuses?.join(",") ?? "*";
  return useLiveQuery(
    () => (statuses ? itemsByStatus(statuses) : db.outbox.toArray()),
    [key],
  );
}

/** `undefined` while the first read is in flight, `null` once it lands with nothing. */
export function useOldestCapturedAt(
  statuses: readonly OutboxStatus[],
): string | null | undefined {
  const key = statuses.join(",");
  return useLiveQuery(() => oldestCapturedAt(statuses), [key]);
}

export function useRecentlyAccepted(limit: number): OutboxItem[] | undefined {
  return useLiveQuery(() => recentlyAccepted(limit), [limit]);
}
