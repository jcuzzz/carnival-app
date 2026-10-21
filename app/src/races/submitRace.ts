import { enqueue } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { RACE_MATERIALISE_KIND, type RaceMaterialisePayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";

export async function submitMaterialiseViaOutbox(input: {
  carnivalId: string;
  eventId: string;
  scheduleSlotId?: string | null;
  label?: string | null;
  title: string;
  actorSubject: string;
  actorDisplayName?: string;
}): Promise<OutboxItem> {
  const payload: RaceMaterialisePayload = {
    carnivalId: input.carnivalId,
    eventId: input.eventId,
    scheduleSlotId: input.scheduleSlotId ?? null,
    label: input.label != null && input.label.trim() !== "" ? input.label.trim() : null,
    display: { title: input.title },
  };
  const item = await enqueue({
    kind: RACE_MATERIALISE_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

export interface MaterialiseIntent {
  id: string;
  eventId: string;
  status: OutboxItem["status"];
  item: OutboxItem;
}

export function materialiseIntents(
  items: OutboxItem[] | undefined,
  carnivalId: string,
): MaterialiseIntent[] {
  const intents: MaterialiseIntent[] = [];
  for (const item of items ?? []) {
    if (item.kind !== RACE_MATERIALISE_KIND) continue;
    const payload = item.payload as RaceMaterialisePayload;
    if (payload.carnivalId !== carnivalId) continue;
    intents.push({ id: item.id, eventId: payload.eventId, status: item.status, item });
  }
  return intents;
}
