import { useSyncExternalStore } from "react";
import { clearAllRosters } from "../db/roster";
import type { PlatformRole } from "../api/types";

export interface Session {
  subject: string;
  displayName: string;
  roles: PlatformRole[];
}

const STORAGE_KEY = "carnival.session";
const CLEAR_PENDING_KEY = "carnival.deviceClearPending";
/** Workbox runtime cache of raw GET /api/v1/* bodies. */
const API_READ_CACHE = "api-reads";

function load(): Session | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Session;
    if (typeof parsed.subject !== "string" || !Array.isArray(parsed.roles)) return null;
    return parsed;
  } catch {
    return null;
  }
}

let current: Session | null = typeof localStorage === "undefined" ? null : load();
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

export function getSession(): Session | null {
  return current;
}

export function signIn(session: Session): void {
  // Real sign-out is a full-page navigation, so a different official arriving is the second place
  // the previous one's roster goes. Same subject must not clear: every cold start re-signs-in.
  const handover = current !== null && current.subject !== session.subject;
  current = session;
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  } catch {
    // Private browsing or quota: the session still lives in memory.
  }
  if (handover) void clearDeviceCaches();
  notify();
}

export function signOut(): void {
  current = null;
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // ignore
  }
  // Not awaited and never allowed to fail the sign-out; a failure raises CLEAR_PENDING_KEY and
  // the retention sweep retries at the next app start, before anyone can sign in.
  void clearDeviceCaches();
  notify();
}

function setClearPending(pending: boolean): void {
  try {
    if (pending) localStorage.setItem(CLEAR_PENDING_KEY, "1");
    else localStorage.removeItem(CLEAR_PENDING_KEY);
  } catch {
    // ignore
  }
}

export function deviceClearPending(): boolean {
  try {
    return localStorage.getItem(CLEAR_PENDING_KEY) === "1";
  } catch {
    return false;
  }
}

/** The outbox is deliberately untouched: queued work belongs to the device, not the session. */
export async function clearDeviceCaches(): Promise<boolean> {
  let cleared = true;
  try {
    await clearAllRosters();
  } catch {
    cleared = false;
  }
  try {
    if (typeof caches !== "undefined") {
      const names = await caches.keys();
      await Promise.all(
        // Matched rather than named: Workbox may prefix a runtime cache.
        names.filter((n) => n.includes(API_READ_CACHE)).map((n) => caches.delete(n)),
      );
    }
  } catch {
    cleared = false;
  }
  setClearPending(!cleared);
  return cleared;
}

export function subscribeSession(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function hasRole(session: Session | null, ...roles: PlatformRole[]): boolean {
  return session !== null && roles.some((r) => session.roles.includes(r));
}

export function useSession(): Session | null {
  return useSyncExternalStore(subscribeSession, getSession, () => null);
}
