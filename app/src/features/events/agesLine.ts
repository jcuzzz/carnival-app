import type { DivisionProfile } from "../../api/types";
import { codesSummary } from "./ageCodes";
import { categoryLabel } from "./eventLabels";

/** "12 to 18+ · Boys, Girls · Competitive"; null where the carnival has no ages at all. */
export function agesLine(
  saved: DivisionProfile | null | undefined,
  schoolDefault: DivisionProfile | null | undefined,
): string | null {
  const profile = saved ?? schoolDefault;
  if (profile == null) return null;
  const ages = codesSummary(profile.ageGroups ?? [], " to ");
  return [
    ages === "" ? "No ages" : ages,
    (profile.categories ?? []).map(categoryLabel).join(", "),
    (profile.tiers ?? []).join(", "),
  ]
    .filter((part) => part !== "")
    .join(" · ");
}
