import { beforeEach, describe, expect, it } from "vitest";
import { db } from "./db";
import { loadCachedRosterPage, saveCachedRoster } from "./roster";
import type { EligibleStudent } from "../api/types";

/**
 * The offline roster snapshot and the page metadata that says whether it is the
 * whole division (K11). Both halves matter at the pool: browsing offline is
 * browsing this row, and a row that cannot say it was capped must not be read
 * as complete.
 */

const roster: EligibleStudent[] = [
  { studentId: "st-a", displayName: "Ana Aardvark", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
  { studentId: "st-b", displayName: "Bea Bandicoot", yearLevel: 7, houseCode: "BANK", ageGroup: "U13" },
];

beforeEach(async () => {
  await db.eventRosters.clear();
});

describe("loadCachedRosterPage", () => {
  it("reads a row written before truncation metadata existed as CAPPED, not complete", async () => {
    // Written by an older build: no `truncated`, no `limit`. Reading that as
    // "complete" is the reassuring answer and the wrong one — the panel would
    // drop the type-to-narrow hint and could claim everyone eligible is already
    // entered off a snapshot that only ever held the first page.
    await db.eventRosters.put({
      carnivalEventId: "ev-old",
      students: roster,
      fetchedAt: "2026-08-01T00:00:00.000Z",
    });

    const page = await loadCachedRosterPage("ev-old");
    expect(page?.truncated).toBe(true);
    expect(page?.limit).toBeNull();
    expect(page?.students).toHaveLength(2);
  });

  it("reports a snapshot that recorded its own completeness as complete", async () => {
    await saveCachedRoster("ev-1", roster, { truncated: false, limit: 50 });

    const page = await loadCachedRosterPage("ev-1");
    expect(page?.truncated).toBe(false);
    expect(page?.limit).toBe(50);
  });
});

describe("saveCachedRoster", () => {
  it("carries stored page metadata forward when the caller has none", async () => {
    // The team panel's leg search saves the roster without page metadata. It is
    // the same row Add-entry reads, so a full-row overwrite would turn a known
    // capped snapshot into an unknown one and lose the warning with it.
    await saveCachedRoster("ev-1", roster, { truncated: true, limit: 50 });
    await saveCachedRoster("ev-1", [roster[0]!]);

    const page = await loadCachedRosterPage("ev-1");
    expect(page?.truncated).toBe(true);
    expect(page?.limit).toBe(50);
    expect(page?.students).toHaveLength(1); // the students themselves are refreshed
  });

  it("lets a metadata-carrying save replace what an earlier one recorded", async () => {
    await saveCachedRoster("ev-1", roster, { truncated: true, limit: 50 });
    await saveCachedRoster("ev-1", roster, { truncated: false, limit: 200 });

    const page = await loadCachedRosterPage("ev-1");
    expect(page?.truncated).toBe(false);
    expect(page?.limit).toBe(200);
  });
});
