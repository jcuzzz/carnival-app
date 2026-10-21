import type { QueueStats } from "./outbox";

export type StuckCause =
  /** Stuck immediately on a 401, no grace. */
  | "paused"
  | "stalled"
  | "parked";

/** Five drain attempts at the 60s backoff cap. */
export const STUCK_AFTER_MS = 5 * 60 * 1000;

export interface OutboxHealthInput {
  stats: QueueStats | undefined;
  oldestPendingCapturedAt: string | null;
  oldestParkedCapturedAt: string | null;
  /** Platform online and the server answering: an offline queue is normal. */
  connected: boolean;
  now: number;
}

export interface OutboxHealth {
  pending: number;
  parked: number;
  waitingMs: number | null;
  /** The one cause that escalates, worst first. */
  cause: StuckCause | null;
}

const HEALTHY: OutboxHealth = { pending: 0, parked: 0, waitingMs: null, cause: null };

export function deriveOutboxHealth(input: OutboxHealthInput): OutboxHealth {
  const { stats } = input;
  if (!stats) return HEALTHY;

  const pending = stats.byStatus.queued + stats.byStatus.syncing;
  const parked = stats.byStatus.conflict + stats.byStatus.rejected;
  const waitingMs = ageMs(input.oldestPendingCapturedAt, input.now);
  const parkedMs = ageMs(input.oldestParkedCapturedAt, input.now);
  const health = { pending, parked, waitingMs };

  if (stats.paused) return { ...health, cause: "paused" };
  // Offline alone never escalates: the connectivity strip already names it, and a banner up all
  // day takes the true ones down with it.
  if (input.connected && pending > 0 && stuck(waitingMs)) return { ...health, cause: "stalled" };
  if (parked > 0 && stuck(parkedMs)) return { ...health, cause: "parked" };
  return { ...health, cause: null };
}

function ageMs(capturedAt: string | null, now: number): number | null {
  if (capturedAt === null) return null;
  const at = Date.parse(capturedAt);
  return Number.isNaN(at) ? null : now - at;
}

function stuck(ageMs: number | null): boolean {
  return ageMs !== null && ageMs >= STUCK_AFTER_MS;
}

export type StuckAction = "sign-in" | "outbox";

export interface StuckBanner {
  text: string;
  actionLabel: string;
  action: StuckAction;
}

const SIGNED_OUT = "Sync is paused — this device is signed out.";

export function stuckBanner(health: OutboxHealth): StuckBanner | null {
  const { pending, parked } = health;
  switch (health.cause) {
    case "paused":
      return {
        text:
          pending > 0
            ? `${SIGNED_OUT} ${waiting(pending)}, and nothing more will sync until you sign in.`
            : `${SIGNED_OUT} Nothing will sync until you sign in.`,
        actionLabel: "Sign in to resume",
        action: "sign-in",
      };
    case "stalled":
      return {
        text:
          `Nothing has synced for ${minutes(health.waitingMs)} —` +
          ` ${waiting(pending)} on this device.`,
        actionLabel: "Open the Outbox",
        action: "outbox",
      };
    case "parked":
      return {
        text:
          `${count(parked)} ${plural(parked, "was", "were")} refused and` +
          ` ${plural(parked, "needs", "need")} a decision —` +
          ` ${plural(parked, "it", "they")} will not sync until someone deals with` +
          ` ${plural(parked, "it", "them")}.`,
        actionLabel: "Open the Outbox",
        action: "outbox",
      };
    case null:
      return null;
  }
}

function count(n: number): string {
  return `${n} ${plural(n, "capture")}`;
}

function waiting(n: number): string {
  return `${count(n)} ${plural(n, "is", "are")} waiting`;
}

function plural(n: number, one: string, many = `${one}s`): string {
  return n === 1 ? one : many;
}

function minutes(ms: number | null): string {
  const n = Math.max(1, Math.floor((ms ?? STUCK_AFTER_MS) / 60_000));
  return `${n} ${plural(n, "minute")}`;
}
