import { enqueue } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { ADJUSTMENT_CREATE_KIND, type AdjustmentCreatePayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";

/**
 * Queued before any network attempt: the item's UUIDv7 is the adjustment id and idempotency
 * key, so a retry after reconnect never double-awards.
 */
export async function submitAdjustmentViaOutbox(input: {
  carnivalId: string;
  houseCode: string;
  points: number;
  category: string;
  reason: string;
  actorSubject: string;
  actorDisplayName?: string;
}): Promise<OutboxItem> {
  const payload: AdjustmentCreatePayload = {
    carnivalId: input.carnivalId,
    houseCode: input.houseCode,
    points: input.points,
    category: input.category,
    reason: input.reason,
  };
  const item = await enqueue({
    kind: ADJUSTMENT_CREATE_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

export function adjustmentItemsForCarnival(
  items: OutboxItem[] | undefined,
  carnivalId: string,
): OutboxItem[] {
  if (!items) return [];
  return items.filter(
    (i) =>
      i.kind === ADJUSTMENT_CREATE_KIND &&
      (i.payload as AdjustmentCreatePayload).carnivalId === carnivalId,
  );
}
