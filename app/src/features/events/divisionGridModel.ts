/** The division tickbox matrix for one event: age rows × category/tier columns. */

import type {
  CarnivalEvent,
  CarnivalEventStatus,
  CompetitionCategory,
  DivisionProfile,
  EventTier,
  GenerateDivisionRequest,
} from "../../api/types";
import { ageScopeLabel, ageScopeOrder, categoryLabel } from "./eventLabels";

export interface DivisionGridColumn {
  key: string;
  category: CompetitionCategory | null;
  tier: EventTier;
  label: string;
  outsideProfile: boolean;
}

export interface DivisionGridCell {
  key: string;
  /** The division as staff read it — "13 · Boys · Comp" — and what a refusal names. */
  label: string;
  spec: GenerateDivisionRequest;
  /** Null when the carnival does not contest this division. */
  event: CarnivalEvent | null;
  entryCount: number;
  status: CarnivalEventStatus | null;
  outsideProfile: boolean;
}

export interface DivisionGridRow {
  /** The age code the division carries ("13", "SNR", "OPEN"). */
  key: string;
  label: string;
  outsideProfile: boolean;
  cells: DivisionGridCell[];
}

export interface DivisionGridModel {
  columns: DivisionGridColumn[];
  rows: DivisionGridRow[];
  /** False means "not configured" — an ordinary state, never an empty matrix. */
  configured: boolean;
  /** Divisions contested outside the profile. */
  outsideProfile: number;
}

function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

export function columnKey(category: string | null, tier: EventTier): string {
  return `${category ?? ""}|${tier}`;
}

export function cellKey(ageGroup: string, category: string | null, tier: EventTier): string {
  return `${ageGroup}|${columnKey(category, tier)}`;
}

interface Age {
  code: string;
  label: string;
  outsideProfile: boolean;
}

interface Column {
  category: CompetitionCategory | null;
  tier: EventTier;
  outsideProfile: boolean;
}

export function buildDivisionGrid(
  profile: DivisionProfile | null | undefined,
  divisions: readonly CarnivalEvent[],
): DivisionGridModel {
  const ages = buildAges(profile, divisions);
  const columns = buildColumns(profile, divisions);
  const byCell = new Map<string, CarnivalEvent>();
  for (const division of divisions)
    byCell.set(cellKey(division.ageGroup, division.category, division.tier), division);

  const tiers = new Set(columns.map((c) => c.tier));
  const gridColumns = columns.map((column) => ({
    key: columnKey(column.category, column.tier),
    category: column.category,
    tier: column.tier,
    label: columnLabel(column, tiers.size > 1),
    outsideProfile: column.outsideProfile,
  }));

  let outsideProfile = 0;
  const rows = ages.map((age) => ({
    key: age.code,
    label: age.label,
    outsideProfile: age.outsideProfile,
    cells: gridColumns.map((column) => {
      const event = byCell.get(cellKey(age.code, column.category, column.tier)) ?? null;
      const outside = age.outsideProfile || column.outsideProfile;
      if (event && outside) outsideProfile++;
      return {
        key: cellKey(age.code, column.category, column.tier),
        label: `${age.label} · ${column.label}`,
        spec: {
          ageGroup: age.code === "OPEN" ? null : age.code,
          category: column.category,
          tier: column.tier,
        },
        event,
        entryCount: event?.entryCount ?? 0,
        status: event?.status ?? null,
        outsideProfile: outside,
      };
    }),
  }));

  return {
    columns: gridColumns,
    rows,
    configured:
      (profile?.ageGroups?.length ?? 0) > 0 &&
      (profile?.categories?.length ?? 0) > 0 &&
      (profile?.tiers?.length ?? 0) > 0,
    outsideProfile,
  };
}

/** Every cell of the matrix — what the add-an-event screen pre-ticks. */
export function allCellKeys(model: DivisionGridModel): Set<string> {
  return new Set(model.rows.flatMap((row) => row.cells.map((cell) => cell.key)));
}

/** The generate request for a set of ticked cells, in row-major order. */
export function tickedSpecs(
  model: DivisionGridModel,
  ticked: ReadonlySet<string>,
): GenerateDivisionRequest[] {
  return model.rows.flatMap((row) =>
    row.cells.filter((cell) => ticked.has(cell.key)).map((cell) => cell.spec),
  );
}

// Declared ages first, then undeclared contested ones: removing an age never removes divisions,
// so hiding those would hide events that exist.
function buildAges(
  profile: DivisionProfile | null | undefined,
  divisions: readonly CarnivalEvent[],
): Age[] {
  const ages: Age[] = [];
  const seen = new Set<string>();
  for (const code of profile?.ageGroups ?? []) {
    const trimmed = code.trim();
    if (trimmed === "" || seen.has(trimmed)) continue;
    seen.add(trimmed);
    ages.push({ code: trimmed, label: ageLabel(trimmed), outsideProfile: false });
  }

  const extra = new Set<string>();
  for (const division of divisions)
    if (!seen.has(division.ageGroup)) extra.add(division.ageGroup);
  for (const code of [...extra].sort(
    (a, b) => ageScopeOrder(a) - ageScopeOrder(b) || ordinal(a, b),
  ))
    ages.push({ code, label: ageLabel(code), outsideProfile: true });
  return ages;
}

function ageLabel(code: string): string {
  return ageScopeLabel({ ageGroup: code }) ?? "All ages";
}

function buildColumns(
  profile: DivisionProfile | null | undefined,
  divisions: readonly CarnivalEvent[],
): Column[] {
  const categories = profile?.categories ?? [];
  const tiers = profile?.tiers ?? [];
  const columns: Column[] = [];
  const seen = new Set<string>();
  for (const category of categories)
    for (const tier of tiers) {
      const key = columnKey(category, tier);
      if (seen.has(key)) continue;
      seen.add(key);
      columns.push({ category, tier, outsideProfile: false });
    }
  for (const division of divisions) {
    const key = columnKey(division.category, division.tier);
    if (seen.has(key)) continue;
    seen.add(key);
    columns.push({
      category: division.category,
      tier: division.tier,
      // A null category is a division state no profile can declare, so only a declarable axis can
      // put a column outside the profile.
      outsideProfile:
        (division.category !== null && !categories.includes(division.category)) ||
        !tiers.includes(division.tier),
    });
  }
  return columns;
}

function columnLabel(column: Column, showTier: boolean): string {
  const category = categoryLabel(column.category);
  return showTier ? `${category} · ${column.tier === "Social" ? "Social" : "Comp"}` : category;
}
