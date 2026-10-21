import type { ScoringRules, Standings } from "../../api/types";

type LegendFacts = Pick<Standings, "weightedEvents" | "hasSocialEvents">;

/** One-line summary of the carnival's scoring rules under the table. */
export function scoringLegend(rules: ScoringRules, facts: LegendFacts): string {
  const ordinals = ["1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th"];
  const places = rules.placePoints.map((p, i) => `${ordinals[i] ?? `${i + 1}th`} ${p}`);
  const byWeight = new Map<number, string[]>();
  for (const { definitionName, weight } of facts.weightedEvents) {
    byWeight.set(weight, [...(byWeight.get(weight) ?? []), definitionName]);
  }
  const weighted = [...byWeight].map(([weight, names]) => ` · ${names.join(", ")} ×${weight}`);
  return (
    `${places.join(" · ")} · ${rules.placePoints.length + 1}th+ ${rules.participationFloor}` +
    ` · DNF ${rules.dnfPoints} · DNS ${rules.dnsPoints} · DQ ${rules.dqPoints}` +
    ` · NM = DNF${weighted.join("")}` +
    (facts.hasSocialEvents ? ` · Social ${rules.socialPerEntryPoints} per entry` : "")
  );
}
