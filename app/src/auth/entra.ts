import { useSyncExternalStore } from "react";
import { api, ApiError, apiBase, isDemoBuild } from "../api/client";
import type { PlatformRole } from "../api/types";
import { getSession, signIn, signOut, type Session } from "./session";

/** Never the Vite dev server nor a demo build: those have no OIDC sign-in. */
export function entraAuthEnabled(): boolean {
  return !import.meta.env.DEV && !isDemoBuild();
}

/** The server's cookie is the session — OIDC sign-in, or the public demo's (ADR-0061 §2). */
export function serverSessionEnabled(): boolean {
  return !import.meta.env.DEV;
}

interface MeResponse {
  subject: string;
  displayName: string;
  roles: PlatformRole[];
}

export function loginHref(returnTo = "/"): string {
  return `${apiBase()}/api/v1/auth/login?returnUrl=${encodeURIComponent(returnTo)}`;
}

export function logoutHref(): string {
  return `${apiBase()}/api/v1/auth/logout`;
}

/** A 401 is null, the expected answer before login; any other failure propagates. */
export async function fetchMe(): Promise<Session | null> {
  try {
    const me = await api<MeResponse>("/me");
    return { subject: me.subject, displayName: me.displayName, roles: me.roles };
  } catch (err) {
    if (err instanceof ApiError && err.status === 401) return null;
    throw err;
  }
}

/**
 * Anything that branches on a role at first paint must wait for this, or it decides against
 * `roles: []` and never re-decides; `bootstrapEntraSession` is deliberately not awaited.
 */
let sessionSettled = !serverSessionEnabled();
let previousSessionEnded = false;
const settledListeners = new Set<() => void>();

function subscribeSettled(listener: () => void): () => void {
  settledListeners.add(listener);
  return () => settledListeners.delete(listener);
}

export function useEntraSessionSettled(): boolean {
  return useSyncExternalStore(
    subscribeSettled,
    () => sessionSettled,
    () => true,
  );
}

/** True once a session this device held was refused at start-up — the demo's "expired" note. */
export function previousSessionHasEnded(): boolean {
  return previousSessionEnded;
}

export async function bootstrapEntraSession(onSessionAdopted: () => void): Promise<void> {
  if (!serverSessionEnabled()) return;
  try {
    const held = getSession() !== null;
    const session = await fetchMe();
    // Adopting the cookie is this build's sign-in, so it owes the queue the same resume.
    if (session) {
      previousSessionEnded = false;
      signIn(session);
      onSessionAdopted();
    } else {
      previousSessionEnded = held;
      signOut();
    }
  } catch {
    // Offline must not sign the official out.
  } finally {
    sessionSettled = true;
    for (const listener of settledListeners) listener();
  }
}

/** The clear races a timeout: a wedged IndexedDB must delay leaving, never prevent it. */
export async function signOutAndClear(
  clear: () => Promise<boolean>,
  go: (href: string) => void,
  timeoutMs = 1500,
): Promise<void> {
  const timeout = new Promise<void>((resolve) => setTimeout(resolve, timeoutMs));
  try {
    await Promise.race([clear(), timeout]);
  } catch {
    // Clearing reports its own failure; leaving must still happen.
  }
  go(logoutHref());
}
