import { useSyncExternalStore } from "react";
import { getHouseMarks } from "../api/resources";
import { getSession, subscribeSession } from "../auth/session";
import { db } from "../db/db";
import { normaliseBrandColour } from "./branding";
import type { HouseMarkDto } from "../api/types";

// Colour is never the only signal: this only mints the value a swatch or bar fill may use
// beside the house's name.

const SETTING_KEY = "houseMarks";
const PREFIX = "--house-";

let current: HouseMarkDto[] = [];
let minted: string[] = [];
let fetched = false;
let unsubscribe: (() => void) | null = null;
let retryWhenOnline: (() => void) | null = null;
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

export function houseSlug(code: string): string {
  return code
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

/**
 * Two codes can slug the same (`MOUNTAIN ASH`, `Mountain-Ash`) and a punctuation-only code slugs to
 * nothing; either would otherwise hand one house another house's colour.
 */
function slugsOf(marks: HouseMarkDto[]): string[] {
  const taken = new Set<string>();
  return marks.map((mark, index) => {
    const base = houseSlug(mark.code) || `house-${index}`;
    let slug = base;
    for (let n = 2; taken.has(slug); n++) slug = `${base}-${n}`;
    taken.add(slug);
    return slug;
  });
}

/** By code first: two codes may share one slug. */
function indexOf(marks: HouseMarkDto[], code: string): number {
  const wanted = code.trim().toLowerCase();
  const byCode = marks.findIndex((mark) => mark.code.trim().toLowerCase() === wanted);
  if (byCode >= 0) return byCode;
  const slug = houseSlug(code);
  return slug ? marks.findIndex((mark) => houseSlug(mark.code) === slug) : -1;
}

function markFor(marks: HouseMarkDto[], code: string): HouseMarkDto | undefined {
  const index = indexOf(marks, code);
  return index < 0 ? undefined : marks[index];
}

export function houseNameOf(marks: HouseMarkDto[], code: string): string {
  return markFor(marks, code)?.name ?? code;
}

export function houseColourVarOf(marks: HouseMarkDto[], code: string): string | null {
  const index = indexOf(marks, code);
  if (index < 0 || !marks[index]!.colour) return null;
  return `var(${PREFIX}${slugsOf(marks)[index]})`;
}

export function houseColourVar(code: string): string | null {
  return houseColourVarOf(current, code);
}

export function getHouseMarksState(): HouseMarkDto[] {
  return current;
}

/**
 * A house with no usable colour mints nothing, so `var(--house-x)` never resolves to a stale
 * hue left behind by an earlier configuration.
 */
export function applyHouseMarks(marks: HouseMarkDto[]): void {
  const root = document.documentElement;
  for (const name of minted) root.style.removeProperty(name);
  minted = [];
  const slugs = slugsOf(marks);
  marks.forEach((mark, index) => {
    const colour = normaliseBrandColour(mark.colour);
    if (!colour) return;
    const name = `${PREFIX}${slugs[index]}`;
    root.style.setProperty(name, colour);
    minted.push(name);
  });
  current = marks;
  notify();
}

function asMarks(value: unknown): HouseMarkDto[] | null {
  if (!Array.isArray(value)) return null;
  const marks: HouseMarkDto[] = [];
  for (const row of value as Partial<HouseMarkDto>[]) {
    if (typeof row?.code !== "string") return null;
    marks.push({
      code: row.code,
      name: typeof row.name === "string" ? row.name : row.code,
      colour: typeof row.colour === "string" ? row.colour : null,
    });
  }
  return marks;
}

export async function loadPersistedHouseMarks(): Promise<HouseMarkDto[] | null> {
  try {
    return asMarks((await db.appSettings.get(SETTING_KEY))?.value);
  } catch {
    return null;
  }
}

function stopRetrying(): void {
  if (!retryWhenOnline) return;
  window.removeEventListener("online", retryWhenOnline);
  retryWhenOnline = null;
}

async function fetchAndApply(): Promise<boolean> {
  let fresh: HouseMarkDto[];
  try {
    fresh = await getHouseMarks();
  } catch {
    return false;
  }
  fetched = true;
  stopRetrying();
  applyHouseMarks(fresh);
  try {
    await db.appSettings.put({ key: SETTING_KEY, value: fresh });
  } catch {
    // Storage refused: painted anyway, it just will not survive a cold start.
  }
  return true;
}

async function refresh(): Promise<void> {
  if (fetched || !getSession()) return;
  if (await fetchAndApply()) return;
  // A device signed in at the gate has no colours until it reaches the venue's
  // network, and nothing else refetches them; one listener, added once.
  if (!retryWhenOnline) {
    retryWhenOnline = () => void refresh();
    window.addEventListener("online", retryWhenOnline);
  }
}

/** Skips `refresh`'s once-per-session guard: after a settings save the answer has moved. */
export async function refreshHouseMarks(): Promise<void> {
  await fetchAndApply();
}

/** Unlike `/branding` the houses read needs a session, so the first sign-in fetches them. */
export async function initHouseMarks(): Promise<void> {
  const persisted = await loadPersistedHouseMarks();
  if (persisted) applyHouseMarks(persisted);
  unsubscribe ??= subscribeSession(() => void refresh());
  await refresh();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useHouseMarks(): HouseMarkDto[] {
  return useSyncExternalStore(subscribe, getHouseMarksState, () => current);
}

export function _resetHouseMarksForTests(): void {
  applyHouseMarks([]);
  fetched = false;
  stopRetrying();
  unsubscribe?.();
  unsubscribe = null;
}
