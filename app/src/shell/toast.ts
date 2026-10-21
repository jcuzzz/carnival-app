import { useSyncExternalStore } from "react";

// Confirmations only: errors stay inline next to the control that failed. A plain module, so any
// module, not just a component under a provider, can raise one.

export type ToastTone = "success" | "info";

export interface Toast {
  id: number;
  message: string;
  tone: ToastTone;
}

export const TOAST_DISMISS_MS = 4_000;

const EMPTY: Toast[] = [];
let toasts: Toast[] = EMPTY;
let nextId = 1;
const timers = new Map<number, ReturnType<typeof setTimeout>>();
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

export function showToast(message: string, tone: ToastTone = "success"): number {
  const id = nextId++;
  toasts = [...toasts, { id, message, tone }];
  timers.set(
    id,
    setTimeout(() => dismissToast(id), TOAST_DISMISS_MS),
  );
  notify();
  return id;
}

export function dismissToast(id: number): void {
  const timer = timers.get(id);
  if (timer !== undefined) {
    clearTimeout(timer);
    timers.delete(id);
  }
  const remaining = toasts.filter((t) => t.id !== id);
  if (remaining.length === toasts.length) return;
  toasts = remaining.length === 0 ? EMPTY : remaining;
  notify();
}

export function getToasts(): Toast[] {
  return toasts;
}

export function subscribeToasts(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useToasts(): Toast[] {
  return useSyncExternalStore(subscribeToasts, getToasts, () => EMPTY);
}

// Module state and pending timers outlive a render.
export function _resetToastsForTests(): void {
  for (const timer of timers.values()) clearTimeout(timer);
  timers.clear();
  toasts = EMPTY;
  nextId = 1;
  listeners.clear();
}
