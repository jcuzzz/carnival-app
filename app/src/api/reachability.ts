import { useSyncExternalStore } from "react";

/**
 * Not `navigator.onLine`, which stays true with the pool AP up and the uplink dead: a fetch that
 * threw is unreachable, and any HTTP response at all (500 included) is reachable.
 */
let reachable = true;
const listeners = new Set<() => void>();

function set(next: boolean): void {
  if (reachable === next) return;
  reachable = next;
  for (const listener of listeners) listener();
}

export function reportReachable(): void {
  set(true);
}

export function reportUnreachable(): void {
  set(false);
}

export function isReachable(): boolean {
  return reachable;
}

export function subscribeReachability(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useReachable(): boolean {
  return useSyncExternalStore(subscribeReachability, isReachable, () => true);
}

/** Module state outlives a render, so suites must reset it. */
export function _resetReachabilityForTests(): void {
  reachable = true;
  listeners.clear();
}
