import { useSyncExternalStore } from "react";
import { getBranding } from "../api/resources";
import { db } from "../db/db";
import type { Branding } from "../api/types";
import { color } from "../tokens.json";

// The last answer is kept in Dexie so a cold start with no uplink still shows the school's
// chrome rather than the neutral default.

const DARK_INK = color.ink.$value;
const LIGHT_INK = color.paper.$value;
const SETTING_KEY = "branding";

export const BRAND_INK_LIGHT = LIGHT_INK;

/** Stands in for the school's name until branding lands, and on a school that set none. */
export const PRODUCT_NAME = "Carnival App";
export const PRODUCT_SHORT_NAME = "Carnival App";

/** The server enforces this; the settings screen previews it. */
export const MIN_BRAND_CONTRAST = 4.5;

export interface BrandingState {
  schoolName: string | null;
  brandColour: string | null;
  monogram: string | null;
}

const NONE: BrandingState = { schoolName: null, brandColour: null, monogram: null };

let current: BrandingState = NONE;
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

/** Lower-case `#rrggbb`, mirroring the server's parse; null when unusable. */
export function normaliseBrandColour(value: string | null | undefined): string | null {
  const text = value?.trim().toLowerCase();
  if (!text || !/^#([0-9a-f]{3}|[0-9a-f]{6})$/.test(text)) return null;
  const digits = text.slice(1);
  return digits.length === 3 ? `#${[...digits].map((c) => `${c}${c}`).join("")}` : text;
}

function channel(value: number): number {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminance(colour: string): number {
  const rgb = Number.parseInt(colour.slice(1), 16);
  return (
    0.2126 * channel((rgb >> 16) & 0xff) +
    0.7152 * channel((rgb >> 8) & 0xff) +
    0.0722 * channel(rgb & 0xff)
  );
}

function contrast(a: number, b: number): number {
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** WCAG 2.x contrast — the same maths as the server. */
export function contrastRatio(a: string, b: string): number {
  return contrast(luminance(a), luminance(b));
}

/** The server has already refused a colour that cannot reach 4.5:1 against either ink. */
export function inkFor(brandColour: string): string {
  const fill = luminance(brandColour);
  return contrast(fill, luminance(LIGHT_INK)) >= contrast(fill, luminance(DARK_INK))
    ? LIGHT_INK
    : DARK_INK;
}

export function monogramOf(schoolName: string | null): string | null {
  const initials = (schoolName ?? "")
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => word[0]);
  return initials.length > 0 ? initials.join("").toUpperCase() : null;
}

export function applyBranding(branding: Branding): BrandingState {
  const schoolName = branding.schoolName.trim() || null;
  const brandColour = normaliseBrandColour(branding.brandColour);
  const next: BrandingState = { schoolName, brandColour, monogram: monogramOf(schoolName) };

  const root = document.documentElement;
  if (brandColour) {
    root.style.setProperty("--brand", brandColour);
    root.style.setProperty("--brand-ink", inkFor(brandColour));
  } else {
    root.style.removeProperty("--brand");
    root.style.removeProperty("--brand-ink");
  }
  applyThemeColour(brandColour);

  if (next.schoolName !== current.schoolName || next.brandColour !== current.brandColour) {
    current = next;
    notify();
  }
  return next;
}

function applyThemeColour(brandColour: string | null): void {
  if (!brandColour) return;
  try {
    document.querySelector('meta[name="theme-color"]')?.setAttribute("content", brandColour);
  } catch {
    // The address-bar tint is cosmetic and never worth failing a boot over.
  }
}

function asBranding(value: unknown): Branding | null {
  if (value === null || typeof value !== "object") return null;
  const row = value as Partial<Branding>;
  if (typeof row.schoolName !== "string") return null;
  return {
    schoolName: row.schoolName,
    brandColour: typeof row.brandColour === "string" ? row.brandColour : null,
  };
}

export async function loadPersistedBranding(): Promise<Branding | null> {
  try {
    return asBranding((await db.appSettings.get(SETTING_KEY))?.value);
  } catch {
    return null;
  }
}

async function persistBranding(branding: Branding): Promise<void> {
  try {
    await db.appSettings.put({ key: SETTING_KEY, value: branding });
  } catch {
    // Storage refused: the chrome is painted anyway, it just will not survive
    // an offline cold start.
  }
}

/**
 * The persisted pair goes first so a cold start does not flash the default accent. A failed
 * fetch is silent: offline is a supported state and the persisted branding still holds.
 */
export async function initBranding(): Promise<void> {
  const persisted = await loadPersistedBranding();
  if (persisted) applyBranding(persisted);

  let fresh: Branding;
  try {
    fresh = await getBranding();
  } catch {
    return;
  }
  applyBranding(fresh);

  const changed =
    persisted === null ||
    persisted.schoolName !== fresh.schoolName ||
    normaliseBrandColour(persisted.brandColour) !== normaliseBrandColour(fresh.brandColour);
  if (changed) await persistBranding(fresh);
}

/** Silent on failure: the settings save already landed and a failed repaint is cosmetic. */
export async function refreshBranding(): Promise<void> {
  let fresh: Branding;
  try {
    fresh = await getBranding();
  } catch {
    return;
  }
  applyBranding(fresh);
  await persistBranding(fresh);
}

export function getBrandingState(): BrandingState {
  return current;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useBranding(): BrandingState {
  return useSyncExternalStore(subscribe, getBrandingState, () => NONE);
}

export function _resetBrandingForTests(): void {
  applyBranding({ schoolName: "", brandColour: null });
}
