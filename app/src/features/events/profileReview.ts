/** What a division-profile edit left outstanding on the events screen. */

import type { DivisionProfile, EventDefinition } from "../../api/types";
import type { EventSetupRow } from "./setupRows";

export interface ProfileReviewItem {
  definition: EventDefinition;
  /** Ages the profile declares that this event contests no division of. */
  missingAges: string[];
}

/** Not contested, retired, or on the open age axis: none of the three is behind. */
export function buildProfileReview(
  profile: DivisionProfile | null | undefined,
  rows: readonly EventSetupRow[],
): ProfileReviewItem[] {
  const ages: string[] = [];
  const seen = new Set<string>();
  for (const code of profile?.ageGroups ?? []) {
    const trimmed = code.trim();
    if (trimmed === "" || seen.has(trimmed)) continue;
    seen.add(trimmed);
    ages.push(trimmed);
  }
  if (ages.length === 0) return [];

  const items: ProfileReviewItem[] = [];
  for (const row of rows) {
    if (row.divisions.length === 0 || !row.definition.isActive) continue;
    const contested = new Set(row.divisions.map((d) => d.ageGroup));
    if (contested.has("OPEN")) continue;
    const missing = ages.filter((age) => !contested.has(age));
    if (missing.length > 0) items.push({ definition: row.definition, missingAges: missing });
  }
  return items;
}

/** "no divisions for 17 or 18" — the outstanding half of one prompt line. */
export function outstandingSummary(item: ProfileReviewItem): string {
  const ages = item.missingAges;
  const list =
    ages.length < 2 ? (ages[0] ?? "") : `${ages.slice(0, -1).join(", ")} or ${ages.at(-1)}`;
  return `no divisions for ${list}`;
}
