import { enqueue, isAbandoned } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { RESULT_JUMP_KIND, type JumpMark, type ResultJumpPayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";
import type { MeasureType } from "../api/types";
import { heightKey } from "./jumpCard";

export interface SubmitJumpInput {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  entryDisplayName: string;
  height: number;
  mark: JumpMark;
  measureType: MeasureType;
  actorSubject: string;
  actorDisplayName?: string;
}

export async function submitJumpViaOutbox(input: SubmitJumpInput): Promise<OutboxItem> {
  const payload: ResultJumpPayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    entryId: input.entryId,
    height: input.height,
    mark: input.mark,
    display: { entryDisplayName: input.entryDisplayName, measureType: input.measureType },
  };
  const item = await enqueue({
    kind: RESULT_JUMP_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

/** Every item at a height is kept, in capture order: three taps at one bar are three marks. */
export function jumpItemsForEvent(
  items: OutboxItem[] | undefined,
  carnivalEventId: string,
): Map<string, Map<string, OutboxItem[]>> {
  const byEntry = new Map<string, Map<string, OutboxItem[]>>();
  if (!items) return byEntry;
  for (const item of items) {
    if (item.kind !== RESULT_JUMP_KIND) continue;
    if (isAbandoned(item.status)) continue;
    const payload = item.payload as ResultJumpPayload;
    if (payload.carnivalEventId !== carnivalEventId) continue;
    let heights = byEntry.get(payload.entryId);
    if (!heights) byEntry.set(payload.entryId, (heights = new Map()));
    const key = heightKey(payload.height);
    const marks = heights.get(key);
    if (marks) marks.push(item);
    else heights.set(key, [item]);
  }
  return byEntry;
}
