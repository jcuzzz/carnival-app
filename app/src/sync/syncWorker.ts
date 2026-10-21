import type { OutboxItem } from "../db/db";
import type { OutboxAck } from "./ackShape";
import {
  blockedByDependency,
  cardTargetOf,
  isCardMark,
  isHeldBehindParkedMark,
  parkedCardKeys,
  pauseQueue,
  pendingInOrder,
  queuePauseState,
  reclaimStuckSyncing,
  transition,
} from "./outbox";

/**
 * Foreground sync is the only mechanism: iOS Background Sync is unreliable, so none is
 * registered. Drains `queued` items in capture order, so a withdrawn capture is never sent.
 */

export type SyncOutcome = "accepted" | "retry" | "conflict" | "rejected" | "pause";

export interface SyncResponse {
  status: number;
  body?: OutboxAck;
  /** Omitted when there is no machine code to read; `status` alone then decides. */
  outcome?: SyncOutcome;
  /** From a 429's `Retry-After`. */
  retryAfterMs?: number;
}

/** The fallback for a response with no machine code (an older build, or a proxy in the way). */
export function outcomeFromStatus(status: number): SyncOutcome {
  if (status >= 200 && status < 300) return "accepted";
  if (status === 401) return "pause";
  if (status === 429) return "retry";
  if (status === 409 || status === 422) return "conflict";
  if (status >= 400 && status < 500) return "rejected";
  return "retry";
}

/** Throws on network failure. */
export type Transport = (item: OutboxItem) => Promise<SyncResponse>;

export type DrainStopReason =
  | "drained"
  | "paused"
  | "offline"
  | "network-error"
  /** The uplink is fine but the server's own state refuses for now; not `network-error`. */
  | "retry-later"
  /** Rate-limited: the server answered, and the worker backs off before asking again. */
  | "throttled";

export interface DrainResult {
  stopReason: DrainStopReason;
  accepted: number;
  rejected: number;
  conflicts: number;
  retryAfterMs?: number;
}

export interface DrainOptions {
  transport: Transport;
  isOnline?: () => boolean;
  now?: () => string;
}

/**
 * Stops at the first retryable failure or auth pause to keep capture order, but skips an item
 * waiting on a local mint or held behind a parked card mark: unrelated work still drains.
 */
export async function drainOnce(opts: DrainOptions): Promise<DrainResult> {
  if (typeof opts.transport !== "function") {
    throw new TypeError(
      "drainOnce requires an injected transport (see src/api/outboxTransport.ts)",
    );
  }
  const isOnline = opts.isOnline ?? (() => navigator.onLine);
  const now = opts.now ?? (() => new Date().toISOString());
  const result: DrainResult = { stopReason: "drained", accepted: 0, rejected: 0, conflicts: 0 };

  if (await queuePauseState()) {
    result.stopReason = "paused";
    return result;
  }
  if (!isOnline()) {
    result.stopReason = "offline";
    return result;
  }

  await reclaimStuckSyncing(Date.parse(now()));

  const pending = await pendingInOrder();
  // Read off the queue, not this pass's refusals: a parked row is gone from `pending`, so the
  // next pass would otherwise send the marks this one shielded.
  const held = await parkedCardKeys();
  for (const item of pending) {
    if (await blockedByDependency(item)) continue;
    if (isHeldBehindParkedMark(item, held)) continue;
    const card = cardTargetOf(item);
    let syncing: OutboxItem;
    try {
      syncing = await transition(item.id, "syncing", {
        attempts: item.attempts + 1,
        syncingAt: now(),
      });
    } catch {
      // An Undo or another tab's drain moved it out of `queued` after the snapshot.
      continue;
    }

    let response: SyncResponse;
    try {
      response = await opts.transport(syncing);
    } catch (err) {
      await transition(item.id, "queued", { lastError: String(err) });
      result.stopReason = "network-error";
      return result;
    }

    const outcome = response.outcome ?? outcomeFromStatus(response.status);
    if (outcome === "accepted") {
      await transition(item.id, "accepted", {
        serverResponse: response.body,
        syncedAt: now(),
      });
      result.accepted += 1;
    } else if (outcome === "pause") {
      // A 401 pauses the queue and never drops the item.
      await transition(item.id, "queued", { lastError: "auth: 401 during sync" });
      await pauseQueue("Authentication expired — sign in again to resume syncing");
      result.stopReason = "paused";
      return result;
    } else if (outcome === "conflict") {
      await transition(item.id, "conflict", {
        lastError: `conflict: HTTP ${response.status}`,
        serverResponse: response.body,
      });
      if (card !== null && isCardMark(item)) held.add(card);
      result.conflicts += 1;
    } else if (outcome === "rejected") {
      await transition(item.id, "rejected", {
        lastError: `rejected: HTTP ${response.status}`,
        serverResponse: response.body,
      });
      if (card !== null && isCardMark(item)) held.add(card);
      result.rejected += 1;
    } else {
      // A later item must never overtake this one.
      const serverFault = response.status >= 500;
      const throttled = response.status === 429;
      const label = serverFault ? "server error" : throttled ? "throttled" : "not ready";
      await transition(item.id, "queued", { lastError: `${label}: HTTP ${response.status}` });
      result.stopReason = serverFault ? "network-error" : throttled ? "throttled" : "retry-later";
      if (throttled && response.retryAfterMs !== undefined) {
        result.retryAfterMs = response.retryAfterMs;
      }
      return result;
    }
  }
  return result;
}

export interface SyncWorkerOptions extends DrainOptions {
  /** A throw here is swallowed: housekeeping must never fail a drain. */
  afterDrain?: (result: DrainResult) => void | Promise<void>;
  baseDelayMs?: number;
  maxDelayMs?: number;
  pollIntervalMs?: number;
  sleep?: (ms: number) => Promise<void>;
}

const defaultSleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Never sooner than an ordinary poll, nor sooner than the server asked. */
export function throttleDelayMs(
  consecutiveThrottles: number,
  pollIntervalMs: number,
  maxDelayMs: number,
  retryAfterMs: number = 0,
  random: () => number = Math.random,
): number {
  const backoff = backoffDelayMs(consecutiveThrottles, pollIntervalMs, maxDelayMs, random);
  return Math.max(pollIntervalMs, backoff, retryAfterMs);
}

/** Full jitter, capped. */
export function backoffDelayMs(
  consecutiveFailures: number,
  baseDelayMs: number,
  maxDelayMs: number,
  random: () => number = Math.random,
): number {
  const cap = Math.min(maxDelayMs, baseDelayMs * 2 ** Math.max(0, consecutiveFailures - 1));
  return Math.floor(random() * cap) + 1;
}

export interface SyncWorker {
  start(): void;
  stop(): void;
  /**
   * Coalesces onto a drain in flight. A wake covers one chained capture (the follow-up pass in
   * `runDrain`); anything chained deeper waits for the poll.
   */
  syncNow(): Promise<DrainResult>;
}

export function createSyncWorker(opts: SyncWorkerOptions): SyncWorker {
  const baseDelayMs = opts.baseDelayMs ?? 1_000;
  const maxDelayMs = opts.maxDelayMs ?? 60_000;
  const pollIntervalMs = opts.pollIntervalMs ?? 15_000;
  const sleep = opts.sleep ?? defaultSleep;

  let running = false;
  let consecutiveFailures = 0;
  let consecutiveThrottles = 0;
  let draining: Promise<DrainResult> | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const runPass = async (): Promise<DrainResult> => {
    const result = await drainOnce(opts);
    if (opts.afterDrain) {
      try {
        await opts.afterDrain(result);
      } catch {
        // Housekeeping only — never let it turn a successful drain into a failure.
      }
    }
    return result;
  };

  // One follow-up pass, never more: catches a capture chained mid-pass without holding the loop.
  const runDrain = async (): Promise<DrainResult> => {
    const first = await runPass();
    if (first.stopReason !== "drained") return first;
    if ((await pendingInOrder()).length === 0) return first;
    const second = await runPass();
    return {
      stopReason: second.stopReason,
      accepted: first.accepted + second.accepted,
      rejected: first.rejected + second.rejected,
      conflicts: first.conflicts + second.conflicts,
      ...(second.retryAfterMs !== undefined ? { retryAfterMs: second.retryAfterMs } : {}),
    };
  };

  const syncNow = async (): Promise<DrainResult> => {
    if (draining) return draining;
    draining = runDrain().finally(() => {
      draining = null;
    });
    return draining;
  };

  // One live timer: an `online` event must move the next tick, never fork a second poll chain.
  const schedule = (delayMs: number) => {
    if (timer !== undefined) clearTimeout(timer);
    timer = undefined;
    if (!running) return;
    timer = setTimeout(() => void tick(), delayMs);
  };

  const tick = async (): Promise<void> => {
    if (!running) return;
    if (timer !== undefined) {
      clearTimeout(timer);
      timer = undefined;
    }
    try {
      const result = await syncNow();
      if (!running) return;
      if (result.stopReason === "network-error") {
        consecutiveFailures += 1;
        await sleep(backoffDelayMs(consecutiveFailures, baseDelayMs, maxDelayMs));
        schedule(0);
        return;
      }
      consecutiveFailures = 0;
      if (result.stopReason === "throttled") {
        consecutiveThrottles += 1;
        schedule(
          throttleDelayMs(consecutiveThrottles, pollIntervalMs, maxDelayMs, result.retryAfterMs),
        );
        return;
      }
      consecutiveThrottles = 0;
    } catch {
      // A throw is a failed pass, not the end of the poll, or the device goes quietly offline.
    }
    schedule(pollIntervalMs);
  };

  const onOnline = () => void tick();

  return {
    start() {
      if (running) return;
      running = true;
      if (typeof addEventListener === "function") addEventListener("online", onOnline);
      void tick();
    },
    stop() {
      running = false;
      if (typeof removeEventListener === "function") removeEventListener("online", onOnline);
      if (timer !== undefined) clearTimeout(timer);
      timer = undefined;
    },
    syncNow,
  };
}
