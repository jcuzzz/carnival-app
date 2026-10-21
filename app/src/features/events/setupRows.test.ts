import { describe, expect, it } from "vitest";
import type { CarnivalEvent, EventDefinition } from "../../api/types";
import {
  buildEventSetupRows,
  definitionTags,
  setupRowSummary,
  splitSetupRows,
  usageSummary,
  type EventSetupRow,
} from "./setupRows";

/**
 * The setup list summarises a carnival's divisions of one event in a line an
 * administrator reads at a glance — so the line has to be true. The trap worth
 * pinning is the age span: a range over a gapped set claims age groups nobody
 * entered.
 */

function definition(id: string, code: string, name: string): EventDefinition {
  return {
    attemptCount: null,
    ladder: null,
    attemptsPerHeight: null,
    id,
    code,
    name,
    sport: "Swimming",
    measureType: "Time",
    units: "seconds",
    isActive: true,
    eligibilityJson: null,
    recordCategoriesJson: null,
    format: "Individual",
    teamSize: null,
    legsJson: null,
    tagsJson: null,
    scoringWeight: 1,
    usage: { carnivals: 2, divisions: 56, schoolRecords: 4, results: 0 },
  };
}

function division(
  definitionId: string,
  ageGroup: string,
  category: string | null,
  tier: CarnivalEvent["tier"] = "Competitive",
): CarnivalEvent {
  return {
    id: `${definitionId}-${ageGroup}-${category ?? "mixed"}-${tier}`,
    carnivalId: "c-1",
    eventDefinitionId: definitionId,
    ageGroup,
    category,
    tier,
  } as CarnivalEvent;
}

const free = definition("def-free", "SW-50-FREE", "Freestyle 50m");
const relay = definition("def-relay", "SW-RELAY", "Freestyle Relay 4x50m");

describe("buildEventSetupRows", () => {
  it("summarises a contiguous age span as a range", () => {
    const divisions = ["12", "13", "14"].flatMap((age) => [
      division(free.id, age, "M"),
      division(free.id, age, "F"),
    ]);
    const [row] = buildEventSetupRows([free], divisions);
    expect(row!.divisions).toHaveLength(6);
    expect(row!.ages).toBe("12–14");
    expect(row!.categories).toBe("Boys/Girls");
    expect(row!.tiers).toBe("Comp");
  });

  it("lists a gapped age span rather than claiming the ages in between", () => {
    const [row] = buildEventSetupRows(
      [free],
      [division(free.id, "12", "M"), division(free.id, "16", "M")],
    );
    expect(row!.ages).toBe("12, 16");
  });

  it("names a non-numeric age rather than folding it into the span", () => {
    const [row] = buildEventSetupRows(
      [free],
      [division(free.id, "15", "F"), division(free.id, "SNR", "F")],
    );
    expect(row!.ages).toBe("15, SNR");
  });

  it("names an open scope instead of inventing an age for it", () => {
    const [row] = buildEventSetupRows(
      [relay],
      [division(relay.id, "OPEN", "M"), division(relay.id, "13", "M")],
    );
    expect(row!.ages).toBe("All ages, 13");
  });

  it("reports both tiers and an uncategorised division", () => {
    const [row] = buildEventSetupRows(
      [free],
      [division(free.id, "13", "M"), division(free.id, "13", null, "Social")],
    );
    expect(row!.tiers).toBe("Comp+Social");
    expect(row!.categories).toBe("Boys/All categories");
  });

  it("keeps a definition the carnival contests nothing of, with no summary", () => {
    const rows = buildEventSetupRows([free, relay], [division(free.id, "13", "M")]);
    expect(rows.map((r) => r.definition.code)).toEqual(["SW-50-FREE", "SW-RELAY"]);
    const empty = rows[1]!;
    expect(empty.divisions).toEqual([]);
    expect(empty.ages).toBeNull();
    expect(empty.categories).toBeNull();
    expect(empty.tiers).toBeNull();
  });

  it("ignores divisions of a definition that is not in the catalogue list", () => {
    const rows = buildEventSetupRows([free], [division("def-gone", "13", "M")]);
    expect(rows).toHaveLength(1);
    expect(rows[0]!.divisions).toEqual([]);
  });
});

describe("setupRowSummary", () => {
  it("reads as one line, and drops to the bare code where the carnival runs none", () => {
    const [free_, relay_] = buildEventSetupRows(
      [free, relay],
      [division(free.id, "13", "M"), division(free.id, "13", "F", "Social")],
    );
    expect(setupRowSummary(free_!, true)).toBe(
      "SW-50-FREE · Timed · 2 divisions · 13 · Boys/Girls · Comp+Social",
    );
    expect(setupRowSummary(relay_!, true)).toBe("SW-RELAY · Timed");
  });

  it("counts one division in the singular", () => {
    const [row] = buildEventSetupRows([free], [division(free.id, "13", "M")]);
    expect(setupRowSummary(row!, true)).toBe(
      "SW-50-FREE · Timed · 1 division · 13 · Boys · Comp",
    );
  });

  // The TYPE is the definition's own and always known; only the divisions are
  // a claim about the carnival, and an unread list makes none.
  it("claims nothing about the carnival while its divisions are unread", () => {
    const [row] = buildEventSetupRows([free], []);
    expect(setupRowSummary(row!, false)).toBe("SW-50-FREE · Timed");
  });
});

describe("usageSummary", () => {
  it("counts everything a rename would reach", () => {
    expect(usageSummary({ carnivals: 3, divisions: 84, schoolRecords: 2, results: 0 })).toBe(
      "Used by 3 carnivals · 84 divisions · 2 school records",
    );
  });

  it("reads in the singular for one of each", () => {
    expect(usageSummary({ carnivals: 1, divisions: 1, schoolRecords: 1, results: 0 })).toBe(
      "Used by 1 carnival · 1 division · 1 school record",
    );
  });

  it("names an empty count rather than writing a zero", () => {
    expect(usageSummary({ carnivals: 2, divisions: 56, schoolRecords: 0, results: 0 })).toBe(
      "Used by 2 carnivals · 56 divisions · no school records",
    );
  });

  it("says plainly when nothing uses the event yet", () => {
    expect(usageSummary({ carnivals: 0, divisions: 0, schoolRecords: 0, results: 0 })).toBe(
      "Not used by any carnival yet",
    );
  });
});

describe("definitionTags", () => {
  it("reads the stored array", () => {
    expect(definitionTags('["discipline:Freestyle","distance:50m"]')).toEqual([
      "discipline:Freestyle",
      "distance:50m",
    ]);
  });

  it("treats a missing or malformed blob as no tags, never an exception", () => {
    expect(definitionTags(null)).toEqual([]);
    expect(definitionTags("")).toEqual([]);
    expect(definitionTags("{not json")).toEqual([]);
    expect(definitionTags('{"discipline":"Freestyle"}')).toEqual([]);
    expect(definitionTags('["ok",7,null]')).toEqual(["ok"]);
  });
});

describe("splitSetupRows (ADR-0052 §9, ADR-0059 §1)", () => {
  const shot = { ...definition("def-shot", "AT-SHOT", "Shot Put"), sport: "Athletics" };
  const novelty = { ...definition("def-nov", "NOV-SACK", "Sack race"), sport: "" };
  const codes = (rows: readonly EventSetupRow[]): string[] =>
    rows.map((row) => row.definition.code);

  it("offers this carnival's sport and a blank one, folding the rest away", () => {
    const rows = buildEventSetupRows([free, shot, novelty], []);
    const { inCarnival, available, otherSports } = splitSetupRows(rows, "Swimming");
    expect(inCarnival).toEqual([]);
    expect(codes(available)).toEqual(["SW-50-FREE", "NOV-SACK"]);
    expect(codes(otherSports)).toEqual(["AT-SHOT"]);
  });

  it("matches the sport case-insensitively", () => {
    const rows = buildEventSetupRows([{ ...shot, sport: "athletics" }], []);
    expect(splitSetupRows(rows, "Athletics").otherSports).toEqual([]);
  });

  it("never folds — or offers — a definition this carnival already contests", () => {
    const rows = buildEventSetupRows([shot], [division(shot.id, "13", "M")]);
    const { inCarnival, available, otherSports } = splitSetupRows(rows, "Swimming");
    expect(codes(inCarnival)).toEqual(["AT-SHOT"]);
    expect(available).toEqual([]);
    expect(otherSports).toEqual([]);
  });

  it("folds nothing when the carnival names no sport", () => {
    const rows = buildEventSetupRows([free, shot], []);
    expect(splitSetupRows(rows, "").otherSports).toEqual([]);
  });

  // Add generates divisions, and a retired definition takes none: the row is on
  // no add list. One this carnival already contests stays, badged.
  it("offers no retired definition, on either list", () => {
    const retired = { ...definition("def-old", "SW-OLD", "Old event"), isActive: false };
    const retiredOther = { ...shot, id: "def-old-at", code: "AT-OLD", isActive: false };
    const rows = buildEventSetupRows([retired, retiredOther], []);
    const { inCarnival, available, otherSports } = splitSetupRows(rows, "Swimming");
    expect(inCarnival).toEqual([]);
    expect(available).toEqual([]);
    expect(otherSports).toEqual([]);
  });

  it("keeps a retired definition this carnival contests in the carnival's list", () => {
    const retired = { ...free, isActive: false };
    const rows = buildEventSetupRows([retired], [division(retired.id, "13", "M")]);
    expect(codes(splitSetupRows(rows, "Swimming").inCarnival)).toEqual(["SW-50-FREE"]);
  });
});
