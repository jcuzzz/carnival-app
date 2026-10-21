import { enqueue } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { ENTRY_ASSIGN_KIND, type EntryAssignPayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";

/**
 * Heat is derived server-side, keeping the field that decides house points out of the marshal's
 * hands; `heat` is only for an explicit override of a genuinely odd case.
 */
export async function submitAssignmentViaOutbox(input: AssignmentInput): Promise<OutboxItem> {
  const item = await enqueueAssignment(input);
  void syncWorker.syncNow();
  return item;
}

interface AssignmentInput {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  /** Null (with lane null) clears the placement: the PUT replaces. */
  raceId: string | null;
  /** Null for a mass start, which has no lanes. */
  lane: number | null;
  heat?: number | null;
  entryDisplayName: string;
  raceTitle?: string;
  actorSubject: string;
  actorDisplayName?: string;
}

async function enqueueAssignment(input: AssignmentInput): Promise<OutboxItem> {
  const payload: EntryAssignPayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    entryId: input.entryId,
    raceId: input.raceId,
    lane: input.lane,
    heat: input.heat ?? null,
    display: {
      entryDisplayName: input.entryDisplayName,
      ...(input.raceTitle !== undefined ? { raceTitle: input.raceTitle } : {}),
    },
  };
  return enqueue({
    kind: ENTRY_ASSIGN_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
}

export interface SwapSide {
  entryId: string;
  carnivalEventId: string;
  entryDisplayName: string;
  lane: number;
}

/**
 * `lane: null` first is what makes the pair satisfiable. One nudge after the third enqueue: a
 * per-item nudge lets a drain snapshot the queue mid-sequence. Not a transaction.
 */
export async function submitSwapViaOutbox(input: {
  carnivalId: string;
  raceId: string;
  raceTitle?: string;
  moving: SwapSide;
  occupant: SwapSide;
  actorSubject: string;
  actorDisplayName?: string;
}): Promise<OutboxItem[]> {
  const common = {
    carnivalId: input.carnivalId,
    ...(input.raceTitle !== undefined ? { raceTitle: input.raceTitle } : {}),
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  };
  const side = (who: SwapSide, lane: number | null) => ({
    ...common,
    carnivalEventId: who.carnivalEventId,
    entryId: who.entryId,
    entryDisplayName: who.entryDisplayName,
    raceId: input.raceId,
    lane,
  });
  const items = [
    await enqueueAssignment(side(input.moving, null)),
    await enqueueAssignment(side(input.occupant, input.moving.lane)),
    await enqueueAssignment(side(input.moving, input.occupant.lane)),
  ];
  void syncWorker.syncNow();
  return items;
}
