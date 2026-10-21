import { describe, expect, it } from "vitest";
import type { CarnivalEvent, DivisionProfile, EventDefinition } from "../../api/types";
import { buildProfileReview, outstandingSummary } from "./profileReview";
import { buildEventSetupRows } from "./setupRows";

/**
 * The review prompt (ADR-0022 §8): a profile edit creates nothing, so the events
 * screen has to name what a new age left outstanding — and say nothing at all
 * when nothing is, or when no division of the event could ever satisfy it.
 */

const profile: DivisionProfile = {
  ageGroups: ["12", "13", "16", "17"],
  categories: ["M", "F"],
  tiers: ["Competitive"],
};

const definition = {
  id: "def-free",
  code: "SW-50-FREE",
  name: "Freestyle 50m",
  isActive: true,
  usage: { carnivals: 1, divisions: 2, schoolRecords: 0, results: 0 },
} as EventDefinition;

function division(ageGroup: string): CarnivalEvent {
  return {
    id: `ev-${ageGroup}`,
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    ageGroup,
    category: "M",
    tier: "Competitive",
    status: "Scheduled",
  } as CarnivalEvent;
}

function review(definitions: EventDefinition[], events: CarnivalEvent[]) {
  return buildProfileReview(profile, buildEventSetupRows(definitions, events));
}

describe("buildProfileReview", () => {
  it("names the ages an event contests nothing of", () => {
    const items = review([definition], [division("12")]);

    expect(items).toHaveLength(1);
    expect(items[0]!.missingAges).toEqual(["13", "16", "17"]);
    expect(outstandingSummary(items[0]!)).toBe("no divisions for 13, 16 or 17");
  });

  it("says nothing once every age the profile names is contested", () => {
    const items = review(
      [definition],
      [division("12"), division("13"), division("16"), division("17")],
    );

    expect(items).toEqual([]);
  });

  it("says nothing about an event contested on the open age axis", () => {
    // An open division runs every age there is, so none is outstanding and
    // generating one per age would be wrong.
    expect(review([definition], [division("OPEN")])).toEqual([]);
    expect(review([definition], [division("12"), division("OPEN")])).toEqual([]);
  });

  it("names a non-numeric age exactly as the roster derives it", () => {
    const seniors: DivisionProfile = { ...profile, ageGroups: ["12", "SNR"] };
    const items = buildProfileReview(
      seniors,
      buildEventSetupRows([definition], [division("12")]),
    );

    expect(items[0]!.missingAges).toEqual(["SNR"]);
  });

  it("says nothing about an event this carnival contests none of", () => {
    expect(review([definition], [])).toEqual([]);
  });

  it("says nothing about a retired event, which can gain no divisions", () => {
    const retired = { ...definition, isActive: false };

    expect(review([retired], [division("12")])).toEqual([]);
  });

  it("says nothing at all when the carnival has no profile", () => {
    const rows = buildEventSetupRows([definition], [division("12")]);

    expect(buildProfileReview(null, rows)).toEqual([]);
    expect(buildProfileReview({ ageGroups: [], categories: [], tiers: [] }, rows)).toEqual([]);
  });
});

describe("outstandingSummary", () => {
  it("lists one, two and many ages as a sentence fragment", () => {
    const of = (missingAges: string[]) => outstandingSummary({ definition, missingAges });

    expect(of(["17"])).toBe("no divisions for 17");
    expect(of(["17", "18"])).toBe("no divisions for 17 or 18");
    expect(of(["16", "17", "18"])).toBe("no divisions for 16, 17 or 18");
  });
});
