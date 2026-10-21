import { useEffect, useState } from "react";
import { entraAuthEnabled, loginHref } from "../auth/entra";
import { href, useUrl } from "../router";
import { PARKED_STATUSES, UNDELIVERED_STATUSES } from "../sync/outbox";
import { deriveOutboxHealth, stuckBanner, type OutboxHealth } from "../sync/stuckOutbox";
import { useOldestCapturedAt, useQueueStats } from "../sync/useOutboxStatus";
import { useConnectivity } from "./useOnline";

// A quarter of the shortest thing measured, and the worker's own idle poll.
const TICK_MS = 15_000;

// One subscription, so the banner and the connectivity strip never disagree about what is waiting.
export function useOutboxHealth(): OutboxHealth {
  const stats = useQueueStats();
  const oldestPending = useOldestCapturedAt(UNDELIVERED_STATUSES);
  const oldestParked = useOldestCapturedAt(PARKED_STATUSES);
  const connectivity = useConnectivity();
  // A queue that stops moving stops producing Dexie changes, which is the state being detected.
  const now = useNow(TICK_MS);
  return deriveOutboxHealth({
    stats,
    oldestPendingCapturedAt: oldestPending ?? null,
    oldestParkedCapturedAt: oldestParked ?? null,
    connected: connectivity === "connected",
    now,
  });
}

function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs]);
  return now;
}

// Keyed on the cause by the caller: a new cause replaces the live region rather than mutating it.
export default function StuckOutboxBanner({ health }: { health: OutboxHealth }) {
  // Signing in is a full-page trip to the IdP: without a returnTo the marshal
  // comes back to the carnival list, not the deck row they were capturing.
  const returnTo = useUrl();
  const banner = stuckBanner(health);
  if (!banner) return null;
  const target =
    banner.action === "sign-in"
      ? entraAuthEnabled()
        ? loginHref(returnTo)
        : href("/login")
      : href("/outbox");
  // A lapsed session is seconds old and stops everything behind it; the other
  // two are true by age, so interrupting a capture buys nothing.
  const assertive = health.cause === "paused";
  return (
    <div
      className={`stuck-banner ${health.cause}`}
      role={assertive ? "alert" : "status"}
      aria-live={assertive ? "assertive" : "polite"}
    >
      <span aria-hidden="true" className="stuck-glyph">
        !
      </span>
      <span className="stuck-text">{banner.text}</span>
      <a className="stuck-action" href={target}>
        {banner.actionLabel}
      </a>
    </div>
  );
}
