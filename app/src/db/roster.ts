import { db } from "./db";
import type { EligibleStudent } from "../api/types";

export interface CachedRoster {
  students: EligibleStudent[];
  /** The server had more eligible students than one page returns. */
  truncated: boolean;
  limit: number | null;
  fetchedAt: string;
}

/** A capped snapshot is not the whole division, and the panel must be able to say so. */
export async function loadCachedRosterPage(eventId: string): Promise<CachedRoster | null> {
  const row = await db.eventRosters.get(eventId);
  if (!row) return null;
  return {
    students: row.students as EligibleStudent[],
    // Missing metadata means the row predates it, not that it was complete: treated as capped,
    // since the wrong guess would claim everyone is entered off a partial list.
    truncated: row.truncated ?? true,
    limit: row.limit ?? null,
    fetchedAt: row.fetchedAt,
  };
}

export async function loadCachedRoster(eventId: string): Promise<EligibleStudent[] | null> {
  const row = await loadCachedRosterPage(eventId);
  return row ? row.students : null;
}

export async function saveCachedRoster(
  eventId: string,
  students: EligibleStudent[],
  page?: { truncated: boolean; limit: number },
): Promise<void> {
  // A write with no page metadata says nothing about completeness, so it carries the stored
  // values forward rather than dropping them.
  const existing = page ? undefined : await db.eventRosters.get(eventId);
  await db.eventRosters.put({
    carnivalEventId: eventId,
    students,
    fetchedAt: new Date().toISOString(),
    ...(page
      ? { truncated: page.truncated, limit: page.limit }
      : {
          ...(existing?.truncated !== undefined ? { truncated: existing.truncated } : {}),
          ...(existing?.limit !== undefined ? { limit: existing.limit } : {}),
        }),
  });
}

/** A cache of a server read, not captured work; covers a device prepared Friday for Monday. */
export const ROSTER_RETENTION_HOURS = 72;

const ROSTER_RETENTION_MS = ROSTER_RETENTION_HOURS * 60 * 60 * 1000;

export async function pruneStaleRosters(now: number = Date.now()): Promise<number> {
  const cutoff = new Date(now - ROSTER_RETENTION_MS).toISOString();
  // Upper bound exclusive: a roster fetched exactly on the boundary stays.
  return db.eventRosters.where("fetchedAt").below(cutoff).delete();
}

export async function clearAllRosters(): Promise<void> {
  await db.eventRosters.clear();
}

export function filterRoster(students: EligibleStudent[], term: string): EligibleStudent[] {
  const q = term.trim().toLowerCase();
  if (q === "") return students;
  return students.filter(
    (s) =>
      s.displayName.toLowerCase().includes(q) ||
      s.houseCode.toLowerCase().includes(q) ||
      `yr ${s.yearLevel}`.includes(q) ||
      String(s.yearLevel) === q,
  );
}
