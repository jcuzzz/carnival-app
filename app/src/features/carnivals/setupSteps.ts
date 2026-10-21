// Pure, so the deck builds the same list to point its empty state at the first step to do.

import type {
  CarnivalEvent,
  CarnivalStatus,
  DivisionProfile,
  EventDefinition,
  HouseParticipation,
  Schedule,
} from "../../api/types";
import { codesSummary } from "../events/ageCodes";
import { counted } from "../events/counted";
import { categoryLabel } from "../events/eventLabels";
import { buildEventSetupRows, splitSetupRows } from "../events/setupRows";

export type SetupStepId = "roster" | "divisions" | "events" | "program" | "open";

export interface SetupStep {
  id: SetupStepId;
  title: string;
  /** The meta line: what the step is for, or what it now holds. */
  detail: string;
  /** What the link promises — also the deck's empty-state button. */
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
  /** SystemAdministrator: the roster step may then link at the import screen. */
  canImport: boolean;
  /** The year the promoted roster's ages are for; null = no roster, undefined = unread. */
  rosterYear?: number | null;
  /** ISO date; its year is what the roster's should match. */
  carnivalDate?: string;
  profile: DivisionProfile | null | undefined;
  /** The roster default, used only where nothing is saved. */
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
    rosterStep(facts),
    divisionsStep(facts, `${base}/setup/divisions`),
    eventsStep(facts, `${base}/setup/events`),
    programStep(facts, `${base}/schedule`),
    openStep(facts, `${base}/manage`),
  ];
}

/** What the organiser is pointed at: the first required step not yet done. */
export function firstUndoneStep(steps: readonly SetupStep[]): SetupStep | undefined {
  return steps.find((step) => step.done === false && !step.optional);
}

function rosterStep(facts: SetupFacts): SetupStep {
  const rows = facts.participation;
  const students = (rows ?? []).reduce((total, house) => total + house.competitors, 0);
  return {
    id: "roster",
    title: "Roster",
    action: "Import the roster",
    done: rows === undefined || rows === null ? null : students > 0,
    optional: false,
    // Imports are SystemAdministrator-only both ways: no link that only refuses.
    path: facts.canImport ? "/import" : null,
    detail:
      rows === null
        ? "Check School → Roster import — the roster count couldn’t be read here"
        : rows === undefined
          ? "The promoted snapshot every entry is drawn from"
          : students > 0
            ? `${counted(students, "student")} in the promoted roster`
            : "No roster has been promoted, so no student is eligible for an entry",
    warning: rosterYearWarning(facts),
  };
}

// A school may run on last year's roster on purpose, so this warns and never blocks.
function rosterYearWarning(facts: SetupFacts): string | undefined {
  const carnivalYear = Number(facts.carnivalDate?.slice(0, 4));
  if (facts.rosterYear == null || !carnivalYear || facts.rosterYear === carnivalYear) {
    return undefined;
  }
  return (
    `The roster’s age groups are for ${facts.rosterYear}, but this carnival is in ` +
    `${carnivalYear}. Import the roster again for ${carnivalYear} under School → Roster import.`
  );
}

function divisionsStep(facts: SetupFacts, path: string): SetupStep {
  const fromRoster = facts.profile == null && facts.profileDefault != null;
  const profile = facts.profile ?? facts.profileDefault;
  const ages = profile?.ageGroups ?? [];
  const categories = profile?.categories ?? [];
  const tiers = profile?.tiers ?? [];
  const done = ages.length > 0 && categories.length > 0 && tiers.length > 0;
  const unread = facts.profile === undefined && facts.profileDefault === undefined;
  return {
    id: "divisions",
    title: "Divisions",
    action: fromRoster ? "Change" : "Add divisions",
    done: unread ? null : done,
    optional: false,
    path,
    detail: fromRoster
      ? `Ages from the roster: ${codesSummary(ages)} · ` +
        `${categories.map(categoryLabel).join(", ")} · ${tiers.join(", ")}`
      : done
        ? `${counted(ages.length, "age")} · ` +
          `${counted(categories.length, "category", "categories")} · ` +
          `${counted(tiers.length, "tier")}`
        : "The ages, categories and tiers this carnival contests",
  };
}

function eventsStep(facts: SetupFacts, path: string): SetupStep {
  const events = facts.events;
  const definitions = new Set((events ?? []).map((event) => event.eventDefinitionId)).size;
  const lists =
    facts.definitions === undefined || events === undefined
      ? null
      : splitSetupRows(buildEventSetupRows(facts.definitions, events), facts.sport ?? "");
  return {
    id: "events",
    title: "Events",
    action: "Add events",
    done: events === undefined ? null : events.length > 0,
    optional: false,
    path,
    detail:
      lists !== null
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
