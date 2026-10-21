import { enqueue } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { ENTRY_CREATE_KIND, type EntryCreatePayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";
import type { EligibleStudent, Entry } from "../api/types";

/**
 * Queued before any network attempt: the item's UUIDv7 is the entry id and idempotency key,
 * so a result can be captured against it before the entry reaches the server.
 */
export async function submitEntryViaOutbox(input: {
  carnivalId: string;
  carnivalEventId: string;
  student: EligibleStudent;
  actorSubject: string;
  actorDisplayName?: string;
  /** The heat the deck is showing — the create places into it. */
  raceId?: string | null;
}): Promise<OutboxItem> {
  const payload: EntryCreatePayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    studentId: input.student.studentId,
    ...(input.raceId ? { raceId: input.raceId } : {}),
    display: {
      displayName: input.student.displayName,
      yearLevel: input.student.yearLevel,
      houseCode: input.student.houseCode,
      ageGroup: input.student.ageGroup,
    },
  };
  const item = await enqueue({
    kind: ENTRY_CREATE_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

export function entryItemsForEvent(
  items: OutboxItem[] | undefined,
  carnivalEventId: string,
): OutboxItem[] {
  if (!items) return [];
  return items.filter(
    (i) =>
      i.kind === ENTRY_CREATE_KIND &&
      (i.payload as EntryCreatePayload).carnivalEventId === carnivalEventId,
  );
}

/** An override is a separate audited admin action, so a queued add never carries one. */
export function optimisticEntry(item: OutboxItem): Entry | null {
  const p = item.payload as EntryCreatePayload;
  // No display: the retention sweep stripped an aged acceptance the server's row long replaced.
  if (!p.display) return null;
  return {
    id: item.id,
    carnivalEventId: p.carnivalEventId,
    studentId: p.studentId,
    // The lane stays null: the server auto-fills it at drain time, and a guess here is one two
    // devices could both make.
    raceId: p.raceId ?? null,
    heat: null,
    lane: null,
    displayName: p.display.displayName,
    yearLevel: p.display.yearLevel,
    houseCode: p.display.houseCode,
    ageGroup: p.display.ageGroup,
    overrideApplied: false,
    // This path is individual-only, so these are the server's own "not a team" values.
    isTeam: false,
    teamHouseCode: null,
    legs: [],
  };
}
