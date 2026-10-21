import type { CarnivalEvent, CarnivalType, MeasureType } from "../../api/types";

export function carnivalSport(type: CarnivalType | undefined): string {
  if (type === undefined || type === "Other") return "";
  return type === "CrossCountry" ? "Cross country" : type;
}

/** "facet:value"; a tag with no colon falls in the generic "tag" facet. */
export function parseTag(t: string): { facet: string; value: string } {
  const i = t.indexOf(":");
  return i === -1 ? { facet: "tag", value: t } : { facet: t.slice(0, i), value: t.slice(i + 1) };
}

export function facetLabel(facet: string): string {
  return facet === "tag" ? "Tag" : facet.charAt(0).toUpperCase() + facet.slice(1);
}

export function disciplineOf(event: Pick<CarnivalEvent, "tags">): string | null {
  for (const tag of event.tags ?? []) {
    const { facet, value } = parseTag(tag);
    // A bare "discipline:" has no value, so it falls through to the next candidate.
    if (facet === "discipline" && value.trim() !== "") return value;
  }
  return null;
}

/** The first non-blank candidate wins: with `??` an empty definition name would title the row. */
export function eventName(
  event: Pick<CarnivalEvent, "definitionName" | "definitionCode" | "tags">,
): string {
  const candidates = [event.definitionName, event.definitionCode, disciplineOf(event)];
  return candidates.find((c) => c != null && c.trim() !== "")?.trim() ?? "Event";
}

export type DivisionScope = Pick<CarnivalEvent, "ageGroup" | "category">;

export function ageScopeLabel(event: Pick<CarnivalEvent, "ageGroup">): string | null {
  return event.ageGroup === "OPEN" ? null : event.ageGroup;
}

/** Always "age · category", in that order; tier and points ride as badges. */
export function divisionLabel(event: DivisionScope): string {
  return [ageScopeLabel(event) ?? "All ages", categoryLabel(event.category)].join(" · ");
}

const CATEGORY_SPELLINGS: Record<string, string> = { M: "Boys", F: "Girls", S: "Open" };

/** M/F/S only, never a gender or sex register; it names a division, never a student. */
export function categoryLabel(category: string | null): string {
  if (category === null) return "All categories";
  return CATEGORY_SPELLINGS[category] ?? category;
}

/** A category as a filter option or chip, where "All categories" would read as all of them. */
export function categoryValueLabel(category: string | null): string {
  return category === null ? "No category" : categoryLabel(category);
}

/** Sort key for an age: open first, then by value, then any other code last. */
export function ageScopeOrder(ageGroup: string): number {
  if (ageGroup === "OPEN") return -1;
  const age = Number.parseInt(ageGroup, 10);
  return Number.isNaN(age) ? Number.MAX_SAFE_INTEGER : age;
}

/** `Count` is legacy and never offered. */
export const EVENT_TYPES: readonly { measure: MeasureType; label: string }[] = [
  { measure: "Time", label: "Race (timed)" },
  { measure: "Distance", label: "Field — best of attempts (distance)" },
  { measure: "Height", label: "Field — bar ladder (vertical jump)" },
];

/** The type as a row spells it: "Timed", "Best of 3 attempts", "Bar ladder". */
export function eventTypeSummary(
  measureType: MeasureType,
  attemptCount: number | null | undefined,
): string {
  if (measureType === "Height") return "Bar ladder";
  if (measureType === "Time") return "Timed";
  if (measureType === "Count") return "Count";
  if (attemptCount == null) return "Best mark";
  return `Best of ${attemptCount} ${attemptCount === 1 ? "attempt" : "attempts"}`;
}
