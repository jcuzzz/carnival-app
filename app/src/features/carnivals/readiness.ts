// Pure. Only a missing student list or no events blocks the open (the server refuses it too,
// CARNIVAL_NOT_READY); the rest warns: a school may open on last year's roster on purpose.

import type { Carnival, CarnivalEvent, Schedule, UncoveredAge } from "../../api/types";
import { orderCodes } from "../events/ageCodes";
import { counted } from "../events/counted";
import { uncoveredWarning } from "../events/uncoveredAges";

export interface ReadinessFacts {
  carnival: Pick<Carnival, "date">;
  /** Active students on the current list; null = unreadable, undefined = unread. */
  students: number | null | undefined;
  /** Undefined while unread: an unread list claims nothing. */
  events: readonly CarnivalEvent[] | undefined;
  /** Students whose age no carnival age covers; undefined = unread. */
  uncoveredAges: readonly UncoveredAge[] | undefined;
  /** The carnival's ages no current student has; undefined = unread. */
  agesWithoutStudents: readonly string[] | undefined;
  /** Null = no roster, undefined = unread. */
  rosterYear: number | null | undefined;
  schedule: Schedule | null | undefined;
}

export type ReadinessWarningId = "rosterYear" | "uncoveredAges" | "emptyAges" | "outsideProgram";

export interface ReadinessWarning {
  id: ReadinessWarningId;
  text: string;
  /** The label of a link to where it is fixed; the screen supplies the path. */
  action?: string;
}

export interface Readiness {
  warnings: ReadinessWarning[];
  /** "12 events · 96 divisions"; null while unread or empty. */
  summary: string | null;
  /**
   * What must come before opening, as one line; null when nothing known is missing. An unread or
   * unreadable fact claims nothing, and the server still refuses.
   */
  missing: string | null;
}

export function rosterYearMismatch(
  rosterYear: number | null | undefined,
  carnivalDate: string | undefined,
): { rosterYear: number; carnivalYear: number; text: string } | null {
  const carnivalYear = Number(carnivalDate?.slice(0, 4));
  if (rosterYear == null || !carnivalYear || rosterYear === carnivalYear) return null;
  return {
    rosterYear,
    carnivalYear,
    text: `The student list’s ages are for ${rosterYear}, but this carnival is in ${carnivalYear}.`,
  };
}

export function readiness(facts: ReadinessFacts): Readiness {
  const warnings: ReadinessWarning[] = [];
  const events = facts.events;

  const years = rosterYearMismatch(facts.rosterYear, facts.carnival.date);
  if (years) warnings.push({ id: "rosterYear", text: years.text });

  // With no roster at all, the Students step already says so.
  if (facts.rosterYear != null) {
    if (facts.uncoveredAges && facts.uncoveredAges.length > 0)
      warnings.push({
        id: "uncoveredAges",
        text: uncoveredWarning(facts.uncoveredAges),
        action: "Event setup",
      });
    if (facts.agesWithoutStudents && facts.agesWithoutStudents.length > 0)
      warnings.push({ id: "emptyAges", text: emptyAgesText(facts.agesWithoutStudents, events) });
  }

  const slots = (facts.schedule?.streams ?? []).reduce(
    (total, stream) => total + (stream.slots?.length ?? 0),
    0,
  );
  if (events !== undefined && slots > 0) {
    const outside = events.filter((e) => e.scheduleSlotId == null).length;
    if (outside > 0) {
      const one = outside === 1;
      warnings.push({
        id: "outsideProgram",
        text:
          `${counted(outside, "division")} ${one ? "isn’t" : "aren’t"} in the program, so ` +
          `${one ? "it won’t" : "they won’t"} appear on the printed running order. ` +
          `Leave ${one ? "it if it runs" : "them if they run"} separately.`,
        action: one ? "Add it to the program" : "Add them to the program",
      });
    }
  }

  const noStudents = facts.students === 0;
  const noEvents = events !== undefined && events.length === 0;
  const definitions = new Set((events ?? []).map((e) => e.eventDefinitionId)).size;
  return {
    warnings,
    summary:
      events === undefined || events.length === 0
        ? null
        : `${counted(definitions, "event")} · ${counted(events.length, "division")}`,
    missing:
      noStudents && noEvents
        ? "Import the students and add events first."
        : noStudents
          ? "Import the students first."
          : noEvents
            ? "Add events first."
            : null,
  };
}

/** "Age 18+ has no students: 26 divisions will be empty." */
function emptyAgesText(
  ages: readonly string[],
  events: readonly CarnivalEvent[] | undefined,
): string {
  const ordered = orderCodes(ages);
  const named =
    ordered.length === 1
      ? `Age ${ordered[0]} has`
      : `Ages ${ordered.slice(0, -1).join(", ")} and ${ordered.at(-1)} have`;
  const empty = (events ?? []).filter((e) => ordered.includes(e.ageGroup)).length;
  const divisions =
    empty === 0 ? "" : `: ${counted(empty, "division")} will be empty`;
  return `${named} no students${divisions}.`;
}
