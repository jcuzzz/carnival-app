// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { filterRoster, loadCachedRoster, saveCachedRoster } from "../db/roster";
import {
  clearDeviceCaches,
  deviceClearPending,
  getSession,
  signIn,
  signOut,
  type Session,
} from "./session";
import type { EligibleStudent } from "../api/types";

/**
 * ADR-0026 §3: a shared iPad passed between volunteers must not still hold the
 * previous session's roster — and must still be searchable, offline, for the
 * volunteer who IS signed in.
 */

const alice: Session = { subject: "alice", displayName: "Alice", roles: ["Recorder"] };
const bob: Session = { subject: "bob", displayName: "Bob", roles: ["Recorder"] };

const students: EligibleStudent[] = [
  { studentId: "st-a", displayName: "Ana Aardvark", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
];

/** A CacheStorage stand-in: jsdom has none, and the real one is the SW's. */
function stubCaches(names: string[]): { deleted: string[] } {
  const deleted: string[] = [];
  vi.stubGlobal("caches", {
    keys: () => Promise.resolve(names),
    delete: (name: string) => {
      deleted.push(name);
      return Promise.resolve(true);
    },
  });
  return { deleted };
}

beforeEach(async () => {
  await db.eventRosters.clear();
  localStorage.clear();
  signOut();
  await vi.waitFor(async () => expect(await db.eventRosters.count()).toBe(0));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("sign-out clears the device", () => {
  it("leaves no cached roster behind", async () => {
    signIn(alice);
    await saveCachedRoster("ev-1", students, { truncated: false, limit: 200 });

    signOut();

    expect(getSession()).toBeNull();
    await vi.waitFor(async () => expect(await loadCachedRoster("ev-1")).toBeNull());
  });

  it("deletes the service worker's api-reads cache and nothing else", async () => {
    const { deleted } = stubCaches(["api-reads", "workbox-precache-v2-http://localhost/"]);

    expect(await clearDeviceCaches()).toBe(true);

    expect(deleted).toEqual(["api-reads"]);
  });

  it("signs the official out even when the caches cannot be cleared, and says so", async () => {
    vi.spyOn(db.eventRosters, "clear").mockRejectedValue(new Error("QuotaExceededError"));
    signIn(alice);

    signOut();

    // Never stranded: the session ends regardless of the storage failure.
    expect(getSession()).toBeNull();
    // Never silent: the sweep retries this at the next app start.
    await vi.waitFor(() => expect(deviceClearPending()).toBe(true));
  });

  it("lowers the pending flag once a clear succeeds", async () => {
    localStorage.setItem("carnival.deviceClearPending", "1");

    expect(await clearDeviceCaches()).toBe(true);

    expect(deviceClearPending()).toBe(false);
  });
});

describe("handover through sign-in", () => {
  it("clears the roster when a DIFFERENT official signs in", async () => {
    signIn(alice);
    await saveCachedRoster("ev-1", students, { truncated: false, limit: 200 });

    signIn(bob);

    await vi.waitFor(async () => expect(await loadCachedRoster("ev-1")).toBeNull());
  });

  it("keeps the roster when the SAME official is re-signed-in", async () => {
    // Every cold start does this: bootstrapEntraSession adopts the cookie
    // session and calls signIn again. Clearing here would wipe the cache on
    // each reload — the offline promise dies quietly.
    signIn(alice);
    await saveCachedRoster("ev-1", students, { truncated: false, limit: 200 });

    signIn({ ...alice, displayName: "Alice Renamed" });

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(await loadCachedRoster("ev-1")).toHaveLength(1);
  });
});

describe("offline search while signed in", () => {
  it("still finds a cached student with no network at all", async () => {
    vi.stubGlobal("fetch", () => Promise.reject(new Error("offline")));
    signIn(alice);
    await saveCachedRoster("ev-1", students, { truncated: false, limit: 200 });

    const cached = await loadCachedRoster("ev-1");

    expect(filterRoster(cached ?? [], "aardvark")).toHaveLength(1);
  });
});
