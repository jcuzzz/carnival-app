import { describe, expect, it } from "vitest";
import type { ScoringRules } from "../../api/types";
import { scoringLegend } from "./scoringLegend";

const RULES: ScoringRules = {
  version: "v1",
  placePoints: [10, 8, 6],
  participationFloor: 1,
  dnfPoints: 1,
  dnsPoints: 0,
  dqPoints: 0,
  socialPerEntryPoints: 1,
};

const PLAIN = { weightedEvents: [], hasSocialEvents: false };

describe("scoringLegend", () => {
  it("prices every place and every non-result status, NM among them", () => {
    expect(scoringLegend(RULES, PLAIN)).toBe(
      "1st 10 · 2nd 8 · 3rd 6 · 4th+ 1 · DNF 1 · DNS 0 · DQ 0 · NM = DNF",
    );
  });

  it("says NM costs what a DNF costs (ADR-0052 §4), so the standings state its price", () => {
    const legend = scoringLegend({ ...RULES, dnfPoints: 2 }, PLAIN);
    expect(legend).toContain("DNF 2");
    expect(legend).toContain("NM = DNF");
  });

  it("names each weighted definition with its own weight rather than assuming relays ×2", () => {
    const legend = scoringLegend(RULES, {
      weightedEvents: [
        { definitionName: "Medley Relay", weight: 3 },
        { definitionName: "Freestyle Relay", weight: 2 },
        { definitionName: "Backstroke Relay", weight: 2 },
      ],
      hasSocialEvents: false,
    });
    expect(legend).toContain(" · Medley Relay ×3 · Freestyle Relay, Backstroke Relay ×2");
    expect(legend).not.toContain("relays ×2");
  });

  it("states the Social per-entry points only when the carnival has Social events", () => {
    expect(scoringLegend({ ...RULES, socialPerEntryPoints: 2 }, { ...PLAIN, hasSocialEvents: true }))
      .toMatch(/ · Social 2 per entry$/);
    expect(scoringLegend(RULES, PLAIN)).not.toContain("Social");
  });
});
