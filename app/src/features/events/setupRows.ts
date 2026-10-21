/** One row per event definition, with this carnival's divisions of it summarised. */

import type { CarnivalEvent, CompetitionCategory, EventDefinition } from "../../api/types";
import { ageScopeOrder, categoryLabel, eventTypeSummary } from "./eventLabels";

export interface EventSetupRow {
  definition: EventDefinition;
  /** This carnival's divisions of the event, in the order the server listed them. */
  divisions: CarnivalEvent[];
  /** "12–18", "13", "All ages, 15" — null when the carnival contests none. */
  ages: string | null;
  /** The categories contested, as `categoryLabel` writes them everywhere else. */
  categories: string | null;
  /** "Comp", "Social" or "Comp+Social". */
  tiers: string | null;
}

export function buildEventSetupRows(
  definitions: readonly EventDefinition[],
  events: readonly CarnivalEvent[],
): EventSetupRow[] {
  const byDefinition = new Map<string, CarnivalEvent[]>();
  for (const event of events) {
    const siblings = byDefinition.get(event.eventDefinitionId);
    if (siblings) siblings.push(event);
    else byDefinition.set(event.eventDefinitionId, [event]);
  }
  return definitions.map((definition) => {
    const divisions = byDefinition.get(definition.id) ?? [];
    return {
      definition,
      divisions,
      ages: ageSummary(divisions),
      categories: categorySummary(divisions),
      tiers: tierSummary(divisions),
    };
  });
}

/** The code alone where the carnival contests none of it; an unread event list claims nothing. */
export function setupRowSummary(row: EventSetupRow, divisionsKnown: boolean): string {
  const type = eventTypeSummary(row.definition.measureType, row.definition.attemptCount);
  if (!divisionsKnown || row.divisions.length === 0) return `${row.definition.code} · ${type}`;
  const n = row.divisions.length;
  const count = `${n} ${n === 1 ? "division" : "divisions"}`;
  const parts = [row.definition.code, type, count, row.ages, row.categories, row.tiers];
  return parts.filter((part): part is string => part !== null).join(" · ");
}

export interface EventSetupLists {
  /** At least one division here, whatever sport the definition claims. */
  inCarnival: EventSetupRow[];
  /** Active, this carnival's sport, nothing here yet — one Add press away. */
  available: EventSetupRow[];
  /** The same, for sports this carnival does not run. */
  otherSports: EventSetupRow[];
}

/**
 * A blank sport belongs to every carnival, and a contested definition is never folded away. A
 * retired definition takes no new divisions, so it is on neither add list.
 */
export function splitSetupRows(
  rows: readonly EventSetupRow[],
  sport: string,
): EventSetupLists {
  const inCarnival: EventSetupRow[] = [];
  const available: EventSetupRow[] = [];
  const otherSports: EventSetupRow[] = [];
  for (const row of rows) {
    if (row.divisions.length > 0) inCarnival.push(row);
    else if (!row.definition.isActive) continue;
    else if (matchesSport(row.definition.sport, sport)) available.push(row);
    else otherSports.push(row);
  }
  return { inCarnival, available, otherSports };
}

function matchesSport(definitionSport: string | null | undefined, sport: string): boolean {
  const own = definitionSport?.trim() ?? "";
  return own === "" || sport === "" || own.toLowerCase() === sport.toLowerCase();
}

/** A rename's blast radius: "Used by 3 carnivals · 84 divisions · 2 school records". */
export function usageSummary(usage: EventDefinition["usage"]): string {
  if (usage.carnivals === 0 && usage.divisions === 0 && usage.schoolRecords === 0)
    return "Not used by any carnival yet";
  const parts = [
    counted(usage.carnivals, "carnival", "carnivals"),
    counted(usage.divisions, "division", "divisions"),
    counted(usage.schoolRecords, "school record", "school records"),
  ];
  return `Used by ${parts.join(" · ")}`;
}

function counted(n: number, singular: string, plural: string): string {
  return n === 0 ? `no ${plural}` : `${n} ${n === 1 ? singular : plural}`;
}

const CATEGORY_ORDER: CompetitionCategory[] = ["M", "F", "S"];

// A range over a gapped set claims ages nobody entered.
function ageSummary(divisions: readonly CarnivalEvent[]): string | null {
  const ages = new Set<string>();
  let open = false;
  for (const division of divisions) {
    if (division.ageGroup === "OPEN") open = true;
    else ages.add(division.ageGroup);
  }
  const sorted = [...ages].sort(
    (a, b) => ageScopeOrder(a) - ageScopeOrder(b) || a.localeCompare(b),
  );
  const parts: string[] = [];
  if (open) parts.push("All ages");
  if (sorted.length === 1) parts.push(sorted[0]!);
  else if (sorted.length > 1)
    parts.push(
      contiguous(sorted) ? `${sorted[0]}–${sorted[sorted.length - 1]}` : sorted.join(", "),
    );
  return parts.length === 0 ? null : parts.join(", ");
}

function contiguous(ages: readonly string[]): boolean {
  let previous: number | null = null;
  for (const age of ages) {
    const n = Number.parseInt(age, 10);
    if (Number.isNaN(n) || String(n) !== age) return false;
    if (previous !== null && n !== previous + 1) return false;
    previous = n;
  }
  return true;
}

function categorySummary(divisions: readonly CarnivalEvent[]): string | null {
  const present = new Set(divisions.map((d) => d.category));
  const ordered = CATEGORY_ORDER.filter((c) => present.has(c)).map((c) => categoryLabel(c));
  if (present.has(null)) ordered.push(categoryLabel(null));
  return ordered.length === 0 ? null : ordered.join("/");
}

function tierSummary(divisions: readonly CarnivalEvent[]): string | null {
  const comp = divisions.some((d) => d.tier === "Competitive");
  const social = divisions.some((d) => d.tier === "Social");
  if (comp && social) return "Comp+Social";
  if (comp) return "Comp";
  return social ? "Social" : null;
}

/** Tolerant parse: a malformed blob must not take the screen down. */
export function definitionTags(tagsJson: string | null | undefined): string[] {
  if (!tagsJson) return [];
  try {
    const parsed: unknown = JSON.parse(tagsJson);
    return Array.isArray(parsed) ? parsed.filter((t): t is string => typeof t === "string") : [];
  } catch {
    return [];
  }
}
