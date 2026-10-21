import { describe, expect, it } from "vitest";
import type { Race } from "../../api/types";
import { PROSPECTIVE_HEAT, heatsOfDivision, numberHeats, selectedHeat } from "./heats";

/** The heat axis (ADR-0039 §1): the division's races, numbered within it. */

const race = (id: string, sortOrder: number): Race => ({
  id,
  eventId: "ev-1",
  scheduleSlotId: null,
  sortOrder,
  label: null,
  entries: [],
});

describe("heatsOfDivision", () => {
  it("numbers the races within the division, in the carnival's order", () => {
    const heats = heatsOfDivision([race("r-b", 4), race("r-a", 1)]);
    expect(heats.map((h) => [h.label, h.raceId])).toEqual([
      ["Heat 1", "r-a"],
      ["Heat 2", "r-b"],
    ]);
  });

  it("puts a queued mint last — it has no server position yet", () => {
    const heats = heatsOfDivision([race("r-queued", -1), race("r-1", 7)]);
    expect(heats.map((h) => h.raceId)).toEqual(["r-1", "r-queued"]);
    expect(heats[1]!.queued).toBe(true);
    expect(heats[0]!.queued).toBe(false);
  });

  it("draws NO chip for a division whose races nothing can see (review B2)", () => {
    // The prospective chip is the caller's to offer, and only off resolved
    // reads: one "Heat 1" over a three-heat division is a claim, not a default.
    expect(heatsOfDivision([])).toEqual([]);
  });
});

describe("numberHeats", () => {
  const heats = heatsOfDivision([race("r-1", 0), race("r-2", 1)]);

  it("labels and orders by the ordinal — adding to a heat never moves a chip", () => {
    // The server derives `entry.heat` from the same ordinal (ADR-0039
    // amendment), so filling the SECOND heat first leaves both chips put.
    const numbered = numberHeats(heats, (raceId) => (raceId === "r-2" ? 2 : null));
    expect(numbered.map((h) => [h.raceId, h.label])).toEqual([
      ["r-1", "Heat 1"],
      ["r-2", "Heat 2"],
    ]);
  });

  it("shows a legacy mismatch as the members' own heat, in place", () => {
    // Pre-amendment data: r-1 holds heat 2 and r-2 holds heat 1. The label is
    // the truth about the placing pool; the ORDER is the division's, unmoved.
    const numbered = numberHeats(heats, (raceId) => (raceId === "r-1" ? 2 : 1));
    expect(numbered.map((h) => [h.raceId, h.label])).toEqual([
      ["r-1", "Heat 2"],
      ["r-2", "Heat 1"],
    ]);
  });

  it("keeps a queued mint last, and lets it stay there once it lands", () => {
    const queued = numberHeats(heatsOfDivision([race("r-1", 0), race("r-q", -1)]), () => null);
    expect(queued.map((h) => [h.raceId, h.label])).toEqual([
      ["r-1", "Heat 1"],
      ["r-q", "Heat 2"],
    ]);
    // Landed: the server gives it the division's highest sortOrder, and the
    // heat its first swimmer takes is the ordinal it already showed.
    const landed = numberHeats(heatsOfDivision([race("r-1", 0), race("r-q", 1)]), (id) =>
      id === "r-q" ? 2 : 1,
    );
    expect(landed.map((h) => [h.raceId, h.label])).toEqual([
      ["r-1", "Heat 1"],
      ["r-q", "Heat 2"],
    ]);
  });

  it("keeps position order while no member carries a heat", () => {
    const numbered = numberHeats(heats, () => null);
    expect(numbered.map((h) => h.label)).toEqual(["Heat 1", "Heat 2"]);
  });
});

describe("selectedHeat", () => {
  const heats = heatsOfDivision([race("r-1", 0), race("r-2", 1)]);

  it("defaults to the LAST heat — the one being filled", () => {
    expect(selectedHeat(heats, null)!.raceId).toBe("r-2");
  });

  it("takes the one the URL names", () => {
    expect(selectedHeat(heats, "r-1")!.raceId).toBe("r-1");
  });

  it("falls back to the last when the URL names no heat of this division", () => {
    expect(selectedHeat(heats, "r-deleted")!.raceId).toBe("r-2");
  });

  it("lands on the prospective heat when that is all there is", () => {
    expect(selectedHeat([PROSPECTIVE_HEAT], "r-anything")!.raceId).toBeNull();
  });

  it("has nothing to select with no chips at all", () => {
    expect(selectedHeat([], "r-1")).toBeNull();
  });
});
