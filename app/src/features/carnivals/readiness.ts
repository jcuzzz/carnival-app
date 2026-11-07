// Pure, and warnings only: a school may open on last year's roster or without a program on purpose.

import type { Carnival, CarnivalEvent, Schedule } from "../../api/types";
import { codesSummary, orderCodes } from "../events/ageCodes";
import { counted } from "../events/counted";

export interface ReadinessFacts {
  carnival: Pick<Carnival, "date">;
  /** Undefined while unread: an unread list claims nothing. */
  events: readonly CarnivalEvent[] | undefined;
  rosterAgeGroups: readonly string[] | undefined;
  /** Null = no roster, undefined = unread. */
  rosterYear: number | null | undefined;
  schedule: Schedule | null | undefined;
}

export interface Readiness {
  warnings: string[];
  /** "12 events · 96 divisions"; null while unread or empty. */
  summary: string | null;
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
  const warnings: string[] = [];
  const events = facts.events;

  const years = rosterYearMismatch(facts.rosterYear, facts.carnival.date);
  if (years) warnings.push(years.text);

  // With no roster at all, the Students step already says so.
  if (events !== undefined && facts.rosterAgeGroups !== undefined && facts.rosterYear !== null) {
    const roster = new Set(facts.rosterAgeGroups);
    const unmatched = events.filter((e) => e.ageGroup !== ALL_AGES && !roster.has(e.ageGroup));
    if (unmatched.length > 0) {
      const ages = orderCodes(unmatched.map((e) => e.ageGroup));
      warnings.push(
        `${counted(unmatched.length, "division")} ${unmatched.length === 1 ? "has" : "have"} ` +
          `an age no current student has (${ages.length === 1 ? "age" : "ages"} ` +
          `${codesSummary(ages)}).`,
      );
    }
  }

  const slots = (facts.schedule?.streams ?? []).reduce(
    (total, stream) => total + (stream.slots?.length ?? 0),
    0,
  );
  if (events !== undefined && slots > 0) {
    const outside = events.filter((e) => e.scheduleSlotId == null).length;
    if (outside > 0) {
      warnings.push(
        `${counted(outside, "division")} ${outside === 1 ? "is" : "are"} not in the program.`,
      );
    }
  }

  if (events !== undefined && events.length === 0) warnings.push("No events yet.");

  const definitions = new Set((events ?? []).map((e) => e.eventDefinitionId)).size;
  return {
    warnings,
    summary:
      events === undefined || events.length === 0
        ? null
        : `${counted(definitions, "event")} · ${counted(events.length, "division")}`,
  };
}
