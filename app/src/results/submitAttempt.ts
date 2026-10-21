import { enqueue, isAbandoned } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { RESULT_ATTEMPT_KIND, type ResultAttemptPayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";
import type { MeasureType } from "../api/types";

export interface SubmitAttemptInput {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  entryDisplayName: string;
  ordinal: number;
  value: string | null;
  foul: boolean;
  measureType: MeasureType;
  attemptCount: number;
  actorSubject: string;
  actorDisplayName?: string;
}

export async function submitAttemptViaOutbox(input: SubmitAttemptInput): Promise<OutboxItem> {
  const payload: ResultAttemptPayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    entryId: input.entryId,
    ordinal: input.ordinal,
    value: input.value,
    foul: input.foul,
    display: {
      entryDisplayName: input.entryDisplayName,
      measureType: input.measureType,
      attemptCount: input.attemptCount,
    },
  };
  const item = await enqueue({
    kind: RESULT_ATTEMPT_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

/** Kept apart from `resultItemsForEvent` so a queued attempt never takes the row's display slot. */
export function attemptItemsForEvent(
  items: OutboxItem[] | undefined,
  carnivalEventId: string,
): Map<string, Map<number, OutboxItem>> {
  const byEntry = new Map<string, Map<number, OutboxItem>>();
  if (!items) return byEntry;
  for (const item of items) {
    if (item.kind !== RESULT_ATTEMPT_KIND) continue;
    if (isAbandoned(item.status)) continue;
    const payload = item.payload as ResultAttemptPayload;
    if (payload.carnivalEventId !== carnivalEventId) continue;
    let cells = byEntry.get(payload.entryId);
    if (!cells) byEntry.set(payload.entryId, (cells = new Map()));
    // Items arrive in capture order; the latest capture wins the cell.
    cells.set(payload.ordinal, item);
  }
  return byEntry;
}
