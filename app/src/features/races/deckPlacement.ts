import type { Race } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { RACE_MATERIALISE_KIND, RACE_CREATE_KIND } from "../../api/outboxTransport";
import type { RaceMaterialisePayload, RaceCreatePayload } from "../../api/outboxTransport";

/**
 * A race keyed to a vanished slot lands unscheduled; the minting outbox item, whose id is the
 * race id, is the only record of the slot it asked for.
 */
export function requestedSlotId(
  items: OutboxItem[] | undefined,
  race: Pick<Race, "id" | "scheduleSlotId">,
): string | null {
  if (race.scheduleSlotId) return null;
  const item = (items ?? []).find((i) => i.id === race.id);
  if (!item) return null;
  if (item.kind === RACE_MATERIALISE_KIND)
    return (item.payload as RaceMaterialisePayload).scheduleSlotId ?? null;
  if (item.kind === RACE_CREATE_KIND) return (item.payload as RaceCreatePayload).scheduleSlotId ?? null;
  return null;
}

/**
 * An undelivered placement overrules the server. Carnival-wide, because a row's picker reads the
 * race its entry sits in.
 */
export function placementOf(
  races: readonly Race[] | undefined,
  placements: ReadonlyMap<string, OutboxItem>,
  entryId: string,
  acceptedFresh: (item: OutboxItem) => boolean = () => false,
): { raceId: string | null; lane: number | null } {
  const item = placements.get(entryId);
  if (item && (item.status === "queued" || item.status === "syncing" || acceptedFresh(item))) {
    const payload = item.payload as { raceId: string | null; lane: number | null };
    return { raceId: payload.raceId, lane: payload.lane };
  }
  for (const race of races ?? []) {
    const found = race.entries.find((e) => e.entryId === entryId);
    if (found) return { raceId: race.id, lane: found.lane };
  }
  return { raceId: null, lane: null };
}

/** Unoccupied lanes plus its own: a collision from this control would be a mis-tap, not a swap. */
export function vacantLanes(
  options: readonly number[],
  taken: ReadonlySet<number>,
  lane: number | null,
): number[] {
  return options.filter((n) => n === lane || !taken.has(n));
}
