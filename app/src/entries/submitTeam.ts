import { enqueue } from "../sync/outbox";
import type { OutboxItem } from "../db/db";
import { TEAM_CREATE_KIND, type TeamCreatePayload } from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";
import type { EligibleStudent, Entry } from "../api/types";

/**
 * Queued even online: a direct POST can overtake this device's own queued heat mint and come
 * back RACE_NOT_IN_EVENT, while the outbox holds it behind the mint by capture order.
 */
export async function submitTeamViaOutbox(input: {
  carnivalId: string;
  carnivalEventId: string;
  houseCode: string;
  /** In leg order. */
  swimmers: readonly EligibleStudent[];
  /** Prescribed stroke per leg, where the definition names them (medley). */
  strokes?: readonly (string | null)[] | null;
  ageGroup: string;
  actorSubject: string;
  actorDisplayName?: string;
  /** The heat the deck is showing, queued or landed. */
  raceId?: string | null;
}): Promise<OutboxItem> {
  const payload: TeamCreatePayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    houseCode: input.houseCode,
    studentIds: input.swimmers.map((s) => s.studentId),
    ...(input.raceId ? { raceId: input.raceId } : {}),
    display: {
      ageGroup: input.ageGroup,
      legs: input.swimmers.map((s, i) => ({
        displayName: s.displayName,
        yearLevel: s.yearLevel,
        stroke: input.strokes?.[i] ?? null,
      })),
    },
  };
  const item = await enqueue({
    kind: TEAM_CREATE_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
  });
  void syncWorker.syncNow();
  return item;
}

export function teamItemsForEvent(
  items: OutboxItem[] | undefined,
  carnivalEventId: string,
): OutboxItem[] {
  if (!items) return [];
  return items.filter(
    (i) =>
      i.kind === TEAM_CREATE_KIND &&
      (i.payload as TeamCreatePayload).carnivalEventId === carnivalEventId,
  );
}

/** Synthesised the way the server builds its team row, so the row keeps its shape on landing. */
export function optimisticTeamEntry(item: OutboxItem): Entry | null {
  const p = item.payload as TeamCreatePayload;
  // No display: the retention sweep stripped an aged acceptance the server's row long replaced.
  if (!p.display) return null;
  return {
    id: item.id,
    carnivalEventId: p.carnivalEventId,
    studentId: null,
    raceId: p.raceId ?? null,
    heat: null,
    lane: null,
    displayName: `${p.houseCode} team`,
    yearLevel: 0,
    houseCode: p.houseCode,
    ageGroup: p.display.ageGroup,
    overrideApplied: false,
    isTeam: true,
    teamHouseCode: p.houseCode,
    legs: p.display.legs.map((leg, i) => ({
      legOrder: i + 1,
      displayName: leg.displayName,
      yearLevel: leg.yearLevel,
      stroke: leg.stroke,
      studentId: p.studentIds[i]!,
    })),
  };
}
