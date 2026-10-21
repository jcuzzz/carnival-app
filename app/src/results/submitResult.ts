import { enqueue, isAbandoned } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import {
  RESULT_CORRECT_KIND,
  RESULT_SUBMIT_KIND,
  type NonOkResultStatus,
  type ResultCapturePayload,
  type ResultSubmitPayload,
} from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";
import type { MeasureType } from "../api/types";

export interface SubmitResultInput {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  entryDisplayName: string;
  value: string | null;
  status: NonOkResultStatus | null;
  measureType: MeasureType;
  actorSubject: string;
  actorDisplayName?: string;
}

export async function submitResultViaOutbox(input: SubmitResultInput): Promise<OutboxItem> {
  const payload: ResultSubmitPayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    entryId: input.entryId,
    value: input.value,
    status: input.status,
    display: { entryDisplayName: input.entryDisplayName, measureType: input.measureType },
  };
  const item = await enqueue({
    kind: RESULT_SUBMIT_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

/**
 * First captures and queued corrections alike. Abandoned items are skipped so the slot falls back
 * to the empty form or the recorded result; otherwise the row keeps a time nobody can replace.
 */
export function resultItemsForEvent(
  items: OutboxItem[] | undefined,
  carnivalEventId: string,
): Map<string, OutboxItem> {
  const byEntry = new Map<string, OutboxItem>();
  if (!items) return byEntry;
  for (const item of items) {
    if (item.kind !== RESULT_SUBMIT_KIND && item.kind !== RESULT_CORRECT_KIND) continue;
    if (isAbandoned(item.status)) continue;
    const payload = item.payload as ResultCapturePayload;
    if (payload.carnivalEventId !== carnivalEventId) continue;
    // Items arrive in capture order; the latest capture wins the display.
    byEntry.set(payload.entryId, item);
  }
  return byEntry;
}
