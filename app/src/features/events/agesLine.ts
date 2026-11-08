import type { DivisionProfile } from "../../api/types";
import { codesSummary, orderCodes } from "./ageCodes";
import { categoryLabel } from "./eventLabels";

export interface AgesLine {
  text: string;
  /** Saved ages no current student has: divisions nobody can enter. */
  stale: string[];
  /** Student-list ages the saved profile leaves out: possibly a deliberate narrowing. */
  leftOut: string[];
}

/** Null where there is nothing to state: no saved profile and no roster to default from. */
export function agesLine(
  saved: DivisionProfile | null | undefined,
  rosterDefault: DivisionProfile | null | undefined,
  rosterAges: readonly string[] | undefined,
): AgesLine | null {
  const profile = saved ?? rosterDefault;
  if (profile == null) return null;
  const ages = orderCodes(profile.ageGroups ?? []);
  const summary = ages.length > 0 ? `Ages ${codesSummary(ages)}` : "No ages";
  const parts = [
    saved == null ? `${summary} from the student list` : summary,
    (profile.categories ?? []).map(categoryLabel).join(", "),
    (profile.tiers ?? []).join(", "),
  ];
  // Only saved ages can disagree with the roster, and only a roster can say which.
  const roster = saved != null ? orderCodes(rosterAges ?? []) : [];
  const compared = roster.length > 0;
  return {
    text: parts.filter((part) => part !== "").join(" · "),
    stale: compared ? ages.filter((code) => !roster.includes(code)) : [],
    leftOut: compared ? roster.filter((code) => !ages.includes(code)) : [],
  };
}

export function staleText(stale: readonly string[]): string {
  return (
    `This carnival has ages no student has: ${stale.join(", ")}. ` +
    "Their divisions can’t be entered."
  );
}

export function leftOutText(leftOut: readonly string[]): string {
  return (
    `Left out of this carnival: ${leftOut.join(", ")}. ` +
    "Students of those ages can’t be entered."
  );
}
