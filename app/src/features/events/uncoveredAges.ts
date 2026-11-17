import type { DivisionProfile, UncoveredAge } from "../../api/types";
import { ageNumber, codesSummary, isOpenEnded, orderCodes } from "./ageCodes";
import { counted } from "./counted";

/** "11", "10 or 11", "9, 10 or 11". */
function agesList(uncovered: readonly UncoveredAge[]): string {
  const ages = orderCodes(uncovered.map((u) => u.ageGroup));
  return ages.length < 2 ? (ages[0] ?? "") : `${ages.slice(0, -1).join(", ")} or ${ages.at(-1)}`;
}

/** "14 students are aged 11" / "1 student is aged 10 or 11". */
export function uncoveredStudents(uncovered: readonly UncoveredAge[]): string {
  const students = uncovered.reduce((total, u) => total + u.students, 0);
  const verb = students === 1 ? "is" : "are";
  return `${counted(students, "student")} ${verb} aged ${agesList(uncovered)}`;
}

function theseAges(uncovered: readonly UncoveredAge[]): string {
  return uncovered.length === 1 ? `age ${uncovered[0]!.ageGroup}` : "those ages";
}

/** Event setup's sentence. */
export function uncoveredText(uncovered: readonly UncoveredAge[]): string {
  const ages = theseAges(uncovered);
  return `${uncoveredStudents(uncovered)}, and this carnival has no events for ${ages}.`;
}

/** Ready to open's shorter sentence. */
export function uncoveredWarning(uncovered: readonly UncoveredAge[]): string {
  return `${uncoveredStudents(uncovered)}, and no event has ${theseAges(uncovered)}.`;
}

/** The youngest age a carnival's ages start at; null with no numeric age. */
export function youngestAge(profile: DivisionProfile | null | undefined): number | null {
  const ages = (profile?.ageGroups ?? [])
    .map(ageNumber)
    .filter((age): age is number => age !== null);
  return ages.length === 0 ? null : Math.min(...ages);
}

export interface Extension {
  /** The new youngest age, as the extend-ages request takes it. */
  youngest: number;
  /** "Add age 11" / "Add ages 10 to 11". */
  label: string;
}

/**
 * Only an age below the carnival's youngest can be added: a gap or one past a plain oldest age
 * is the Change screen's job.
 */
export function extension(
  uncovered: readonly UncoveredAge[],
  profile: DivisionProfile | null | undefined,
): Extension | null {
  const current = youngestAge(profile);
  if (current === null) return null;
  const below = uncovered
    .filter((u) => !isOpenEnded(u.ageGroup))
    .map((u) => ageNumber(u.ageGroup))
    .filter((age): age is number => age !== null && age < current);
  if (below.length === 0) return null;
  const youngest = Math.min(...below);
  const top = current - 1;
  return {
    youngest,
    label: youngest === top ? `Add age ${top}` : `Add ages ${youngest} to ${top}`,
  };
}

/** "Age 11 added: 52 divisions." */
export function extendedText(addedAges: readonly string[], divisionsAdded: number): string {
  const ages =
    addedAges.length === 1 ? `Age ${addedAges[0]}` : `Ages ${codesSummary(addedAges, " to ")}`;
  return divisionsAdded === 0
    ? `${ages} added. No events needed new divisions.`
    : `${ages} added: ${counted(divisionsAdded, "division")}.`;
}
