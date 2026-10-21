import { describe, expect, it } from "vitest";
import { demoNoindex, isCacheableApiRead } from "./vite.config";

/**
 * The `api-reads` allowlist, pinned on the very function Workbox serialises
 * into sw.js — there is no other test between this rule and a venue's device.
 */
const read = (path: string, sameOrigin = true) =>
  isCacheableApiRead({ url: new URL(`https://school.example${path}`), sameOrigin });

const carnival = "/api/v1/carnivals/00000000-0000-7000-8000-000000000001";

describe("isCacheableApiRead", () => {
  it("caches the carnival deck's own reads", () => {
    expect(read("/api/v1/carnivals")).toBe(true);
    expect(read("/api/v1/carnivals/")).toBe(true);
    expect(read(carnival)).toBe(true);
    expect(read(`${carnival}/events`)).toBe(true);
    expect(read(`${carnival}/events/ev-1`)).toBe(true);
    expect(read(`${carnival}/events/ev-1/entries`)).toBe(true);
    expect(read(`${carnival}/events/ev-1/results`)).toBe(true);
    expect(read(`${carnival}/races`)).toBe(true);
    expect(read(`${carnival}/schedule/`)).toBe(true);
    expect(read(`${carnival}/settings`)).toBe(true);
    expect(read(`${carnival}/standings`)).toBe(true);
    expect(read(`${carnival}/progress`)).toBe(true);
    expect(read(`${carnival}/adjustments`)).toBe(true);
    expect(read("/api/v1/event-definitions/?includeInactive=false")).toBe(true);
    expect(read("/api/v1/school/houses")).toBe(true);
  });

  it("never caches a cross-origin read or a non-API path", () => {
    expect(read(`${carnival}/events`, false)).toBe(false);
    expect(read("/health")).toBe(false);
    expect(read("/api/v1")).toBe(false);
  });

  it("never caches a roster import — the whole staged school roster (ADR-0026 §4)", () => {
    // The finding this allowlist exists for: one import wrote every staged
    // student, STKEY and category to disk, past the administrator-only gate.
    expect(read("/api/v1/imports")).toBe(false);
    expect(read("/api/v1/imports/b-1/validation")).toBe(false);
    expect(read("/api/v1/imports/b-1/reconciliation")).toBe(false);
  });

  it("excludes the roster search Dexie already mirrors", () => {
    expect(read(`${carnival}/events/e-1/eligible-students`)).toBe(false);
  });

  it("excludes every report: an export is audited server-side, never served stale", () => {
    expect(read(`${carnival}/reports/results`)).toBe(false);
    expect(read(`${carnival}/reports/standings.csv`)).toBe(false);
    expect(read(`${carnival}/reports/results.csv`)).toBe(false);
    // ADR-0028 §4: the district list is one of those files.
    expect(read(`${carnival}/reports/district.csv`)).toBe(false);
  });

  it("never caches the device fleet — a stale one says a quiet device was fine (ADR-0034)", () => {
    // The allowlist makes a new endpoint uncached by default, so this pins a
    // property rather than fixing a bug: the whole point of the panel is
    // noticing that a device stopped reporting, and a cached list answers with
    // the moment before it did. Organiser-only data on a shared deck device is
    // the second reason.
    expect(read(`${carnival}/devices`)).toBe(false);
    expect(read(`${carnival}/devices/d-1/sync-status`)).toBe(false);
  });

  it("excludes identity and build state, which are wrong the moment they are stale", () => {
    expect(read("/api/v1/me")).toBe(false);
    expect(read("/api/v1/auth/me")).toBe(false);
    expect(read("/api/v1/version")).toBe(false);
  });

  it("caches the record book, which is the point of it living outside /reports/", () => {
    // ADR-0028 §5: the deck badge has to work on a venue uplink and at a cold
    // start, and sign-out already deletes this cache (ADR-0026 §3).
    expect(read("/api/v1/records")).toBe(true);
    expect(read("/api/v1/records?sport=Swimming")).toBe(true);
  });

  it("still works re-evaluated from its own source, which is what sw.js runs", () => {
    // Workbox inlines it with toString(): anything captured from module scope
    // lands in sw.js as an undefined identifier and breaks the route outright.
    const inlined = new Function(`return (${isCacheableApiRead.toString()})`)() as
      typeof isCacheableApiRead;
    const at = (path: string) =>
      inlined({ url: new URL(`https://s.example${path}`), sameOrigin: true });
    expect(at(`${carnival}/reports/results.csv`)).toBe(false);
    expect(at(`${carnival}/reports/district.csv`)).toBe(false);
    expect(at("/api/v1/imports/b-1/reconciliation")).toBe(false);
    expect(at("/api/v1/imports/b-1/validation")).toBe(false);
    expect(at(`${carnival}/events`)).toBe(true);
    expect(at(`${carnival}/schedule/`)).toBe(true);
    expect(at("/api/v1/records")).toBe(true);
  });
});

describe("demoNoindex", () => {
  type Hooks = {
    configResolved: (config: { env: Record<string, string> }) => void;
    transformIndexHtml: () => unknown[];
  };
  const tagsFor = (env: Record<string, string>) => {
    const plugin = demoNoindex() as unknown as Hooks;
    plugin.configResolved({ env });
    return plugin.transformIndexHtml();
  };

  it("marks a demo build's HTML noindex (ADR-0061 §4)", () => {
    expect(tagsFor({ VITE_DEMO_MODE: "true" })).toEqual([
      { tag: "meta", attrs: { name: "robots", content: "noindex, nofollow" }, injectTo: "head" },
    ]);
  });

  it("leaves a school build indexable as before", () => {
    expect(tagsFor({})).toEqual([]);
  });
});
