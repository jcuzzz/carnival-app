import type { RowValidation } from "../../api/types";

/** Each refusal code as what is wrong with the row, singular and plural. */
const REASONS: Readonly<Record<string, readonly [one: string, many: string]>> = {
  MISSING_HOUSE: ["has no house", "have no house"],
  UNKNOWN_HOUSE: ["has a house the school doesn't have", "have a house the school doesn't have"],
  MISSING_BIRTHDATE: ["has no birth date", "have no birth date"],
  INVALID_BIRTHDATE: ["has a birth date that can't be read", "have a birth date that can't be read"],
  BIRTHDATE_AFTER_CUT_OFF: [
    "has a birth date too late for any age group",
    "have a birth date too late for any age group",
  ],
  DUPLICATE_STKEY: ["repeats a student ID", "repeat a student ID"],
  MISSING_STKEY: ["has no student ID", "have no student ID"],
  MISSING_SURNAME: ["has no surname", "have no surname"],
  MISSING_FIRST_NAME: ["has no first name", "have no first name"],
  MISSING_GENDER: ["has no gender", "have no gender"],
  MISSING_STATUS: ["has no enrolment status", "have no enrolment status"],
  UNKNOWN_STATUS: [
    "has an enrolment status the app doesn't know",
    "have an enrolment status the app doesn't know",
  ],
  INVALID_YEAR_LEVEL: ["has a year level outside 7 to 12", "have a year level outside 7 to 12"],
};

const OTHER: readonly [string, string] = ["has another problem", "have another problem"];

/** One refusal code as what is wrong with the row: "has no house". */
export function rowProblem(code: string): string {
  return (hasWordsFor(code) ? REASONS[code] : OTHER)[0];
}

/** False for a code the app has no words for yet. */
export function hasWordsFor(code: string): boolean {
  return Object.hasOwn(REASONS, code);
}

/** Every distinct problem on a row, joined: "has no house and has no birth date". */
export function rowProblems(issues: readonly { code: string }[]): string {
  return [...new Set(issues.map((issue) => rowProblem(issue.code)))].join(" and ");
}

/** A phrase as the start of a table cell. */
export function capitalised(phrase: string): string {
  return phrase.charAt(0).toUpperCase() + phrase.slice(1);
}

/**
 * "4 rows can't be imported: 2 have no house, 1 has no birth date, 1 repeats a student ID." A row
 * is refused by any Error or Exception; a Warning lets it through. Null when none is refused.
 */
export function rejectedRowsSummary(rows: readonly RowValidation[]): string | null {
  const counts = new Map<readonly [string, string], number>();
  let rejected = 0;
  for (const row of rows) {
    const reasons = new Set(
      row.issues
        .filter((issue) => issue.severity !== "Warning")
        .map((issue) => (hasWordsFor(issue.code) ? REASONS[issue.code] : OTHER)),
    );
    if (reasons.size === 0) continue;
    rejected++;
    for (const reason of reasons) counts.set(reason, (counts.get(reason) ?? 0) + 1);
  }
  if (rejected === 0) return null;

  const order = [...Object.values(REASONS), OTHER];
  const ranked = [...counts].sort(
    ([a, x], [b, y]) => y - x || order.indexOf(a) - order.indexOf(b),
  );
  if (rejected === 1)
    return `1 row can't be imported: it ${ranked.map(([[one]]) => one).join(" and ")}.`;
  const parts = ranked.map(([[one, many], n]) => `${n} ${n === 1 ? one : many}`);
  return `${rejected} rows can't be imported: ${parts.join(", ")}.`;
}
