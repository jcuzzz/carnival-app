import type { Carnival, RoleAssignment, SchoolSettings } from "../../api/types";
import type { SetupStep } from "../carnivals/setupSteps";
import { counted } from "../events/counted";

/** Seeded when the deployment names no school (SchoolSettingsBootstrap): set, never chosen. */
export const UNCONFIGURED_SCHOOL_NAME = "(unconfigured)";

/** Each read is undefined while in flight and null when it failed. */
export interface SchoolFacts {
  settings: SchoolSettings | null | undefined;
  assignments: readonly RoleAssignment[] | null | undefined;
  /** The signed-in administrator's subject, as a grant they make records it. */
  subject: string;
  carnivals: readonly Carnival[] | null | undefined;
  /** People exists on this build, so staff are added there rather than on Roles. */
  localAccounts: boolean;
}

export function schoolSteps(facts: SchoolFacts): SetupStep[] {
  return [settingsStep(facts.settings), staffStep(facts), carnivalStep(facts.carnivals)];
}

/** Done once an administrator has saved School settings; a seeded name does not count. */
export function schoolSetUp(settings: SchoolSettings | null | undefined): boolean | null {
  return settings == null ? null : settings.setupConfirmed;
}

function settingsStep(settings: SchoolSettings | null | undefined): SetupStep {
  const done = schoolSetUp(settings);
  return {
    id: "settings",
    title: "School name, colours and houses",
    action: "Open School settings",
    done,
    optional: false,
    path: "/school/settings",
    detail:
      done && settings
        ? `${settings.name.trim()} · ${counted(settings.houses.length, "house")}`
        : "The name and colour every screen shows, and the houses students belong to",
  };
}

function staffStep(facts: SchoolFacts): SetupStep {
  const others = peopleGranted(facts.assignments, facts.subject);
  return {
    id: "staff",
    title: "Staff accounts",
    action: "Add staff",
    done: others === null ? null : others > 0,
    optional: true,
    path: facts.localAccounts ? "/school/people" : "/school/roles",
    detail: others
      ? `${counted(others, "person", "people")} given a role`
      : "The people who record results and run carnivals, each with a role",
  };
}

// By creator, not by upn: an Entra subject is an object id that no upn matches, and the first
// administrator's own row was granted by the host.
function peopleGranted(
  assignments: readonly RoleAssignment[] | null | undefined,
  subject: string,
): number | null {
  if (assignments == null) return null;
  const people = assignments
    .filter((row) => row.revokedAtUtc === null && row.assignedBySubject === subject)
    .map((row) => row.upn.toLowerCase());
  return new Set(people).size;
}

function carnivalStep(carnivals: readonly Carnival[] | null | undefined): SetupStep {
  const count = carnivals?.length;
  return {
    id: "carnival",
    title: "Your first carnival",
    action: "New carnival",
    done: count === undefined ? null : count > 0,
    optional: false,
    path: "/carnivals",
    detail: count
      ? counted(count, "carnival")
      : "Name it, pick its sport and date; Manage then walks through its setup",
  };
}
