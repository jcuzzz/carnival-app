// Pure: each done state is derived from a read that may still be missing.

import type {
  CarnivalEvent,
  CarnivalStatus,
  DivisionProfile,
  EventDefinition,
  HouseParticipation,
  Schedule,
} from "../../api/types";
import { counted } from "../events/counted";
import { buildEventSetupRows, splitSetupRows } from "../events/setupRows";
import { rosterYearMismatch } from "./readiness";

export type SetupStepId = "roster" | "events" | "program" | "open";

export interface SetupStep {
  id: string;
  title: string;
  /** The meta line: what the step is for, or what it now holds. */
  detail: string;
  /** What the link promises. */
  action: string;
  /** Null where the read that decides it is not this actor's to make. */
  done: boolean | null;
  optional: boolean;
  /** Null where this actor has no screen for it — the roster, for most. */
  path: string | null;
  /** Said beside the step without undoing it. */
  warning?: string;
}

export interface SetupFacts {
  carnivalId: string;
  status: CarnivalStatus | undefined;
  /** Null when the read failed, not zero. */
  participation: readonly HouseParticipation[] | null | undefined;
  /** SystemAdministrator: the Students step may then link at the import screen. */
  canImport: boolean;
  /** The year the promoted roster's ages are for; null = no roster, undefined = unread. */
  rosterYear?: number | null;
  /** ISO date; its year is what the roster's should match. */
  carnivalDate?: string;
  profile: DivisionProfile | null | undefined;
  /** The school's ages, used only where nothing is saved. */
  profileDefault?: DivisionProfile | null | undefined;
  events: readonly CarnivalEvent[] | undefined;
  /** The catalogue, for what is left to add; undefined = unread. */
  definitions?: readonly EventDefinition[] | undefined;
  /** The carnival's sport, which decides what "available" counts. */
  sport?: string;
  schedule: Schedule | null | undefined;
}

export function setupSteps(facts: SetupFacts): SetupStep[] {
  const base = `/carnivals/${facts.carnivalId}`;
  return [
    studentsStep(facts),
    eventsStep(facts, `${base}/setup/events`),
    programStep(facts, `${base}/schedule`),
    openStep(facts, `${base}/manage`),
  ];
}

/** What the organiser is pointed at: the first required step not yet done. */
export function firstUndoneStep(steps: readonly SetupStep[]): SetupStep | undefined {
  return steps.find((step) => step.done === false && !step.optional);
}

/** Active students on the current list: the Students step's count, and what opening needs. */
export function studentCount(participation: readonly HouseParticipation[]): number {
  return participation.reduce((total, house) => total + house.competitors, 0);
}

function studentsStep(facts: SetupFacts): SetupStep {
  const rows = facts.participation;
  const students = studentCount(rows ?? []);
  return {
    id: "roster",
    title: "Students",
    action: "Import the students",
    done: rows === undefined || rows === null ? null : students > 0,
    optional: false,
    // Imports are SystemAdministrator-only both ways: no link that only refuses.
    path: facts.canImport ? `/import?carnival=${facts.carnivalId}` : null,
    detail:
      rows === null
        ? "The student count couldn’t be read here"
        : rows === undefined
          ? "The student list every entry is drawn from"
          : students > 0
            ? facts.rosterYear != null
              ? `${counted(students, "student")} · ages for ${facts.rosterYear}`
              : counted(students, "student")
            : facts.canImport
              ? "No students yet, so nobody can be entered"
              : "No students yet: a system administrator imports the student list",
    warning: rosterYearWarning(facts),
  };
}

// A school may run on last year's roster on purpose, so this warns and never blocks.
function rosterYearWarning(facts: SetupFacts): string | undefined {
  const years = rosterYearMismatch(facts.rosterYear, facts.carnivalDate);
  if (!years) return undefined;
  return (
    `${years.text} ` +
    (facts.canImport
      ? `Import the students again for ${years.carnivalYear} from this step.`
      : `A system administrator can import them again for ${years.carnivalYear}.`)
  );
}

function eventsStep(facts: SetupFacts, path: string): SetupStep {
  const events = facts.events;
  const definitions = new Set((events ?? []).map((event) => event.eventDefinitionId)).size;
  const lists =
    facts.definitions === undefined || events === undefined
      ? null
      : splitSetupRows(buildEventSetupRows(facts.definitions, events), facts.sport ?? "");
  // No ages saved and none from the school, so nothing can be added yet.
  const noAges = facts.profile === null && facts.profileDefault === null && events?.length === 0;
  return {
    id: "events",
    title: "Events",
    action: "Add events",
    done: events === undefined ? null : events.length > 0,
    optional: false,
    path,
    detail: noAges
      ? "Needs the school's ages from School settings"
      : lists !== null
        ? `${lists.inCarnival.length} in this carnival · ` +
          `${lists.available.length} available to add`
        : events !== undefined && events.length > 0
          ? `${counted(events.length, "division")} across ${counted(definitions, "event")}`
          : "The event catalogue and which divisions of it run here",
  };
}

function programStep(facts: SetupFacts, path: string): SetupStep {
  const streams = facts.schedule?.streams ?? [];
  const slots = streams.reduce((total, stream) => total + (stream.slots?.length ?? 0), 0);
  return {
    id: "program",
    title: "Program",
    action: "Build the program",
    done: facts.schedule === undefined ? null : slots > 0,
    optional: true,
    path,
    detail:
      slots > 0
        ? `${counted(slots, "slot")} across ${counted(streams.length, "stream")}`
        : "Optional — the time-slotted plan staff print. A carnival runs without one",
  };
}

function openStep(facts: SetupFacts, path: string): SetupStep {
  const status = facts.status;
  // Undefined, not zero, where the event list has not landed: an unread list
  // cannot say "every division is open".
  const scheduled = facts.events?.filter((event) => event.status === "Scheduled").length;
  return {
    id: "open",
    title: "Open",
    action: "Open the carnival",
    done: status === undefined ? null : status !== "Draft",
    optional: false,
    path,
    detail:
      status === undefined || status === "Draft"
        ? "Results are only accepted while the carnival and the division are Open"
        : scheduled === undefined
          ? status
          : scheduled > 0
            ? `${status} · ${counted(scheduled, "division")} still Scheduled`
            : `${status} · every division is open`,
  };
}
