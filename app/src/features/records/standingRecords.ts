import type { SchoolRecord } from "../../api/types";

/**
 * The direction comes off the record (`higherBetter`), never re-derived. Two indexes because a
 * results-book row carries the printed division name and no category.
 */
export interface StandingRecords {
  /** `${eventDefinitionId}|${ageGroup}|${category}` — the deck's key. */
  byDivision: Map<string, SchoolRecord>;
  /** `${eventCode}|${division}` — the results book's key. */
  byBookRow: Map<string, SchoolRecord>;
}

export function divisionKey(
  eventDefinitionId: string,
  ageGroup: string,
  category: string | null,
): string {
  return `${eventDefinitionId}|${ageGroup}|${category ?? ""}`;
}

export function bookRowKey(eventCode: string, division: string): string {
  return `${eventCode}|${division}`;
}

/** An absent or empty book indexes to empty maps, so every lookup misses. */
export function indexRecords(records: readonly SchoolRecord[] | undefined): StandingRecords {
  const byDivision = new Map<string, SchoolRecord>();
  const byBookRow = new Map<string, SchoolRecord>();
  for (const record of records ?? []) {
    byDivision.set(
      divisionKey(record.eventDefinitionId, record.ageGroup, record.category),
      record,
    );
    const key = bookRowKey(record.eventCode, record.division);
    const held = byBookRow.get(key);
    // Divisions printing the same name keep the harder record, so a label collision can only
    // under-badge, never badge a swim against the wrong record.
    if (held === undefined || beatsRecord(held, record.value)) byBookRow.set(key, record);
  }
  return { byDivision, byBookRow };
}

/** Strictly better in the measure's own direction: equalling is not beating. */
export function beatsRecord(
  record: SchoolRecord | undefined,
  value: number | null | undefined,
): boolean {
  if (record === undefined || value == null) return false;
  return record.higherBetter ? value > record.value : value < record.value;
}
