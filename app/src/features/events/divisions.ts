import type { CarnivalEvent } from "../../api/types";
import { ageScopeOrder, divisionLabel } from "./eventLabels";

/**
 * Only divisions that exist, in deck rhythm (age, category, Competitive before Social), not
 * program order.
 */
export function siblingDivisions(
  events: readonly CarnivalEvent[],
  event: Pick<CarnivalEvent, "eventDefinitionId">,
): CarnivalEvent[] {
  return events
    .filter((e) => e.eventDefinitionId === event.eventDefinitionId)
    .sort(
      (a, b) =>
        ageScopeOrder(a.ageGroup) - ageScopeOrder(b.ageGroup) ||
        a.ageGroup.localeCompare(b.ageGroup) ||
        (a.category ?? "").localeCompare(b.category ?? "") ||
        a.tier.localeCompare(b.tier),
    );
}

/** Two divisions differing only by tier must never read alike: a wrong-tier time is accepted. */
export function divisionChipLabel(
  event: Pick<CarnivalEvent, "ageGroup" | "category" | "tier">,
): string {
  const division = divisionLabel(event);
  return event.tier === "Social" ? `${division} · Social` : division;
}
