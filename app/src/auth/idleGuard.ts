import { useCallback, useEffect, useRef, useState } from "react";
import { apiBase } from "../api/client";
import { entraAuthEnabled, signOutAndClear } from "./entra";
import { clearDeviceCaches, signOut } from "./session";

/**
 * Unreachable, the idle timeout must not sign out: the roster cannot be re-fetched without an
 * uplink, so a privacy overlay goes up and the sign-out waits for the server. Outbox untouched.
 */

/** Long enough for a 40-minute event block; short enough that a device left out signs out. */
export const IDLE_TIMEOUT_MS = 60 * 60 * 1000;

const ACTIVITY_THROTTLE_MS = 30_000;

/** AP up with the uplink dead never fires an `online` event, so the lock re-probes on a cadence. */
const LOCKED_RETRY_MS = 5 * 60 * 1000;

const PROBE_TIMEOUT_MS = 3_000;

/** Pointer/key/touch only: scroll and mousemove fire without a human. */
const ACTIVITY_EVENTS = ["pointerdown", "keydown", "touchstart"] as const;

/**
 * Never `navigator.onLine`, which stays true with the pool AP up and the uplink dead. Any HTTP
 * status is a yes; /health is outside the service-worker cache, so disk cannot answer it.
 */
export async function probeServerReachable(): Promise<boolean> {
  if (typeof navigator !== "undefined" && !navigator.onLine) return false;
  try {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
    await fetch(`${apiBase()}/health`, { cache: "no-store", signal: controller.signal });
    clearTimeout(timer);
    return true;
  } catch {
    return false;
  }
}

/** Entra ends the cookie session too, or the next launch silently re-adopts it. */
function defaultSignOut(): void {
  if (entraAuthEnabled()) {
    void signOutAndClear(clearDeviceCaches, (href) => location.assign(href));
  } else {
    signOut();
  }
}

export interface IdleGuardOptions {
  /** Called only after a reachable probe. */
  onSignOut?: () => void;
  onLockedChange: (locked: boolean) => void;
  probe?: () => Promise<boolean>;
  timeoutMs?: number;
  retryMs?: number;
  target?: IdleEventTarget;
}

interface IdleEventTarget {
  addEventListener(type: string, listener: () => void, capture?: boolean): void;
  removeEventListener(type: string, listener: () => void, capture?: boolean): void;
}

export interface IdleGuard {
  start(): void;
  stop(): void;
  resume(): void;
}

export function createIdleGuard(opts: IdleGuardOptions): IdleGuard {
  const timeoutMs = opts.timeoutMs ?? IDLE_TIMEOUT_MS;
  const retryMs = opts.retryMs ?? LOCKED_RETRY_MS;
  const probe = opts.probe ?? probeServerReachable;
  const signOutNow = opts.onSignOut ?? defaultSignOut;
  const target = opts.target ?? (typeof window === "undefined" ? undefined : window);

  let timer: ReturnType<typeof setTimeout> | undefined;
  let lastReset = 0;
  let locked = false;
  let running = false;
  let probing = false;

  function setLocked(next: boolean): void {
    if (locked === next) return;
    locked = next;
    opts.onLockedChange(next);
  }

  function arm(): void {
    clearTimeout(timer);
    lastReset = Date.now();
    timer = setTimeout(() => void fire(), timeoutMs);
  }

  async function fire(): Promise<void> {
    if (!running || probing) return;
    probing = true;
    const reachable = await probe().catch(() => false);
    probing = false;
    // resume() or stop() may have run during the await; they win.
    if (!running || (locked === false && Date.now() - lastReset < timeoutMs)) return;
    clearTimeout(timer);
    if (reachable) {
      setLocked(false);
      signOutNow();
      return;
    }
    setLocked(true);
    timer = setTimeout(() => void fire(), retryMs);
  }

  function onActivity(): void {
    // The overlay swallows taps behind it; only `resume()` lowers it.
    if (!running || locked) return;
    if (Date.now() - lastReset < ACTIVITY_THROTTLE_MS) return;
    arm();
  }

  function onOnline(): void {
    if (!running || !locked) return;
    void fire();
  }

  return {
    start() {
      if (running) return;
      running = true;
      for (const event of ACTIVITY_EVENTS) target?.addEventListener(event, onActivity, true);
      target?.addEventListener("online", onOnline);
      arm();
    },
    stop() {
      running = false;
      clearTimeout(timer);
      for (const event of ACTIVITY_EVENTS) target?.removeEventListener(event, onActivity, true);
      target?.removeEventListener("online", onOnline);
      setLocked(false);
    },
    resume() {
      if (!running) return;
      setLocked(false);
      arm();
    },
  };
}

/** React view of the guard. `active` is "somebody is signed in on this device". */
export function useIdleGuard(active: boolean): { locked: boolean; resume: () => void } {
  const guard = useRef<IdleGuard | null>(null);
  const [locked, setLocked] = useState(false);

  useEffect(() => {
    if (!active) return;
    const created = createIdleGuard({ onLockedChange: setLocked });
    guard.current = created;
    created.start();
    return () => {
      created.stop();
      guard.current = null;
    };
  }, [active]);

  return { locked: active && locked, resume: useCallback(() => guard.current?.resume(), []) };
}
