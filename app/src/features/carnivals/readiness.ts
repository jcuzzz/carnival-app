// Pure. Only a missing student list or no events blocks the open (the server refuses it too,
// CARNIVAL_NOT_READY); the rest warns: a school may open on last year's roster on purpose.

import type { Carnival, CarnivalEvent, Schedule } from "../../api/types";
import { codesSummary, orderCodes } from "../events/ageCodes";
import { counted } from "../events/counted";

export interface ReadinessFacts {
  carnival: Pick<Carnival, "date">;
  /** Active students on the current list; null = unreadable, undefined = unread. */
  students: number | null | undefined;
  /** Undefined while unread: an unread list claims nothing. */
  events: readonly CarnivalEvent[] | undefined;
  rosterAgeGroups: readonly string[] | undefined;
  /** Null = no roster, undefined = unread. */
  rosterYear: number | null | undefined;
  schedule: Schedule | null | undefined;
}

export type ReadinessWarningId = "rosterYear" | "unmatchedAges" | "outsideProgram";

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

/** The all-ages division, which no roster age has to match. */
const ALL_AGES = "OPEN";

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
  if (events !== undefined && facts.rosterAgeGroups !== undefined && facts.rosterYear !== null) {
    const roster = new Set(facts.rosterAgeGroups);
    const unmatched = events.filter((e) => e.ageGroup !== ALL_AGES && !roster.has(e.ageGroup));
    if (unmatched.length > 0) {
      const ages = orderCodes(unmatched.map((e) => e.ageGroup));
      warnings.push({
        id: "unmatchedAges",
        text:
          `${counted(unmatched.length, "division")} ${unmatched.length === 1 ? "has" : "have"} ` +
          `an age no current student has (${ages.length === 1 ? "age" : "ages"} ` +
          `${codesSummary(ages)}).`,
      });
    }
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
