import {
  ENTRY_CREATE_KIND,
  RESULT_ATTEMPT_KIND,
  RESULT_JUMP_KIND,
  RESULT_SUBMIT_KIND,
  TEAM_CREATE_KIND,
  type EntryCreatePayload,
  type ResultAttemptPayload,
  type ResultCapturePayload,
  type ResultJumpPayload,
  type TeamCreatePayload,
} from "../../api/outboxTransport";
import type { CarnivalEvent } from "../../api/types";
import type { OutboxItem, OutboxStatus } from "../../db/db";

/**
 * Rejected and conflict items are answered by the server, not progress; accepted ones are already
 * in the server's counts.
 */
export const UNDELIVERED_STATUSES: readonly OutboxStatus[] = ["queued", "syncing"];

export interface QueuedProgress {
  /** Offline adds, relays included. */
  entries: number;
  /** First result captures. */
  results: number;
}

/**
 * Corrections are absent: the server already counted the result. Deduplicated per entry, so a
 * re-save, a field series or a jump card counts once.
 */
export function queuedProgressByEvent(
  items: OutboxItem[] | undefined,
): Map<string, QueuedProgress> {
  const results = new Map<string, Set<string>>();
  const entries = new Map<string, Set<string>>();
  for (const item of items ?? []) {
    if (!UNDELIVERED_STATUSES.includes(item.status)) continue;
    if (item.kind === RESULT_SUBMIT_KIND) {
      const payload = item.payload as ResultCapturePayload;
      addTo(results, payload.carnivalEventId, payload.entryId);
    } else if (item.kind === RESULT_ATTEMPT_KIND || item.kind === RESULT_JUMP_KIND) {
      const payload = item.payload as ResultAttemptPayload | ResultJumpPayload;
      addTo(results, payload.carnivalEventId, payload.entryId);
    } else if (item.kind === ENTRY_CREATE_KIND) {
      const payload = item.payload as EntryCreatePayload;
      addTo(entries, payload.carnivalEventId, payload.studentId);
    } else if (item.kind === TEAM_CREATE_KIND) {
      // TEAM_EXISTS: one team per house per event, so a queued relay dedupes on the house.
      const payload = item.payload as TeamCreatePayload;
      addTo(entries, payload.carnivalEventId, payload.houseCode);
    }
  }
  const byEvent = new Map<string, QueuedProgress>();
  for (const eventId of new Set([...results.keys(), ...entries.keys()])) {
    byEvent.set(eventId, {
      entries: entries.get(eventId)?.size ?? 0,
      results: results.get(eventId)?.size ?? 0,
    });
  }
  return byEvent;
}

function addTo(map: Map<string, Set<string>>, key: string, value: string): void {
  const set = map.get(key);
  if (set) set.add(value);
  else map.set(key, new Set([value]));
}

/** A Social event scores from entries alone, so no result is owed. */
export function expectsResults(event: Pick<CarnivalEvent, "effectiveScoringMode">): boolean {
  return event.effectiveScoringMode !== "Social";
}

export interface Progress {
  /** Every entry counted, Social instances included. */
  entryCount: number;
  /** Results against entries where a result is EXPECTED. */
  resultCount: number;
  /** Those entries — the denominator, and 0 when nothing here needs a time. */
  expectedCount: number;
}

/** Server aggregates plus undelivered captures; Social instances count as entered, not owed. */
export function sumProgress(
  events: readonly CarnivalEvent[],
  queued?: Map<string, QueuedProgress>,
): Progress {
  const progress: Progress = { entryCount: 0, resultCount: 0, expectedCount: 0 };
  for (const event of events) {
    const q = queued?.get(event.id);
    const entryCount = (event.entryCount ?? 0) + (q?.entries ?? 0);
    progress.entryCount += entryCount;
    if (!expectsResults(event)) continue;
    progress.expectedCount += entryCount;
    // A queued mark cannot be told from one the server already counted, so the
    // overlay's clamp applies here too, or a ladder tile reads 11/8.
    progress.resultCount += Math.min((event.resultCount ?? 0) + (q?.results ?? 0), entryCount);
  }
  return progress;
}

/** 0–100 for the progress bar; 0 where nothing is expected (nothing to fill). */
export function progressPercent(progress: Progress): number {
  return progress.expectedCount > 0
    ? Math.round((progress.resultCount / progress.expectedCount) * 100)
    : 0;
}

/** "8 entered" where no results are owed; "?/10" while the results read is down. */
export function progressText(progress: Progress, unavailable = false): string {
  return progress.expectedCount > 0
    ? `${unavailable ? "?" : progress.resultCount}/${progress.expectedCount}`
    : `${progress.entryCount} entered`;
}
