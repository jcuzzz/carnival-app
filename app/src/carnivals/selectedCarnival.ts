import { useSyncExternalStore } from "react";
import type { Carnival, CarnivalStatus } from "../api/types";

/**
 * Device-local, not per-account: a deck tablet is shared and the venue may have no uplink.
 * `name`/`date`/`status` are a display cache, null until an adopted deep link's carnival loads.
 */
export interface SelectedCarnival {
  id: string;
  name: string | null;
  date: string | null;
  status: CarnivalStatus | null;
}

const STORAGE_KEY = "sports.carnival";

function load(): SelectedCarnival | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as SelectedCarnival;
    if (typeof parsed.id !== "string" || parsed.id === "") return null;
    return {
      id: parsed.id,
      name: typeof parsed.name === "string" ? parsed.name : null,
      date: typeof parsed.date === "string" ? parsed.date : null,
      status: typeof parsed.status === "string" ? parsed.status : null,
    };
  } catch {
    return null;
  }
}

let current: SelectedCarnival | null = typeof localStorage === "undefined" ? null : load();
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

function persist(next: SelectedCarnival | null): void {
  current = next;
  try {
    if (next) localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Private mode or quota: the selection still holds for this tab rather
    // than throwing out of a render.
  }
  notify();
}

export function getSelectedCarnival(): SelectedCarnival | null {
  return current;
}

export function selectCarnival(carnival: Carnival): void {
  persist({
    id: carnival.id,
    name: carnival.name,
    date: carnival.date,
    status: carnival.status,
  });
}

/** A no-op when already selected, or every render of its screens would null the cached name. */
export function adoptCarnival(carnivalId: string): void {
  if (current?.id === carnivalId) return;
  persist({ id: carnivalId, name: null, date: null, status: null });
}

export function syncSelectedCarnival(carnival: Carnival): void {
  if (current?.id !== carnival.id) return;
  if (
    current.name === carnival.name &&
    current.date === carnival.date &&
    current.status === carnival.status
  ) {
    return;
  }
  selectCarnival(carnival);
}

export function clearSelectedCarnival(): void {
  persist(null);
}

// The app is often open twice on one device (PWA plus a tab); without this the first copy
// to write silently overwrites the carnival the other is showing.
if (typeof addEventListener !== "undefined") {
  addEventListener("storage", (event) => {
    if (event.key !== null && event.key !== STORAGE_KEY) return;
    current = load();
    notify();
  });
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useSelectedCarnival(): SelectedCarnival | null {
  return useSyncExternalStore(subscribe, getSelectedCarnival, () => null);
}

export function _resetSelectedCarnivalForTests(): void {
  persist(null);
}
