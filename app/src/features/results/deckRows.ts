import type { Entry } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import type { EntryCreatePayload, TeamCreatePayload } from "../../api/outboxTransport";
import { optimisticEntry } from "../../entries/submitEntry";
import { optimisticTeamEntry } from "../../entries/submitTeam";
import { isAbandoned } from "../../sync/outbox";

export interface DeckRow {
  entry: Entry;
  /** The queued `entry.create` this row is, for the optimistic add. */
  createItem: OutboxItem | undefined;
}

/** Deck order — heat, lane, name — because results arrive lane by lane. */
export function deckRows(
  entries: readonly Entry[] | undefined,
  queuedEntryItems: readonly OutboxItem[],
  queuedTeamItems: readonly OutboxItem[] = [],
): DeckRow[] {
  const key = (n: number | null) => (n == null ? Number.MAX_SAFE_INTEGER : n);
  const serverStudentIds = new Set((entries ?? []).map((e) => e.studentId));
  const serverHouses = new Set(
    (entries ?? []).flatMap((e) => (e.isTeam && e.teamHouseCode ? [e.teamHouseCode] : [])),
  );
  const server = (entries ?? []).map((entry) => ({
    entry,
    createItem: undefined as OutboxItem | undefined,
  }));
  // One row per student, the newest capture speaking for them, so a refused add and its
  // replacement are not two rows. UUIDv7 ids compare in capture order.
  const newest = new Map<string, OutboxItem>();
  for (const i of queuedEntryItems) {
    // An add that was let go never became an entry: no row, and the student
    // goes back on the eligible list rather than into an unfillable row.
    if (isAbandoned(i.status)) continue;
    const studentId = (i.payload as EntryCreatePayload).studentId;
    if (serverStudentIds.has(studentId)) continue;
    const prev = newest.get(studentId);
    if (!prev || i.id > prev.id) newest.set(studentId, i);
  }
  const optimistic = [...newest.values()].flatMap((item) => {
    const entry = optimisticEntry(item);
    return entry ? [{ entry, createItem: item }] : [];
  });
  // TEAM_EXISTS: one team per house per event, so a queued relay dedupes on the house.
  const newestTeam = new Map<string, OutboxItem>();
  for (const i of queuedTeamItems) {
    if (isAbandoned(i.status)) continue;
    const house = (i.payload as TeamCreatePayload).houseCode;
    if (serverHouses.has(house)) continue;
    const prev = newestTeam.get(house);
    if (!prev || i.id > prev.id) newestTeam.set(house, i);
  }
  const optimisticTeams = [...newestTeam.values()].flatMap((item) => {
    const entry = optimisticTeamEntry(item);
    return entry ? [{ entry, createItem: item }] : [];
  });
  return [...server, ...optimistic, ...optimisticTeams].sort(
    (a, b) =>
      key(a.entry.heat) - key(b.entry.heat) ||
      key(a.entry.lane) - key(b.entry.lane) ||
      a.entry.displayName.localeCompare(b.entry.displayName),
  );
}
