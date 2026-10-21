import type { Race, RaceEntry } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import type { RaceMaterialisePayload } from "../../api/outboxTransport";

/** Races minted for the division plus races holding its entries: one rule for the whole deck. */
export function racesOfEvent(races: readonly Race[] | undefined, eventId: string): Race[] {
  return (races ?? []).filter(
    (race) =>
      race.eventId === eventId || race.entries.some((entry) => entry.carnivalEventId === eventId),
  );
}

/** The item id is the race id, so entries and lanes captured against it line up on drain. */
export function optimisticMaterialisedRace(item: OutboxItem): Race {
  const payload = item.payload as RaceMaterialisePayload;
  return {
    id: item.id,
    // The server stamps the mint's division, so the optimistic shape carries the same stamp.
    eventId: payload.eventId ?? null,
    scheduleSlotId: payload.scheduleSlotId ?? null,
    // NOT 0: the server decides a race's position, and borrowing one here would
    // number every queued heat 1 — including two of them at once.
    sortOrder: -1,
    label: payload.label ?? null,
    entries: [] as RaceEntry[],
  };
}

/** With the races read down, an entry's own raceId is the only evidence its heat exists. */
function entryStampedRace(id: string, eventId: string): Race {
  return {
    id,
    eventId,
    scheduleSlotId: null,
    // Unknown, not 0: the server owns a race's position, so these sort last and
    // fall back to id order (UUIDv7 — creation order) against each other.
    sortOrder: Number.MAX_SAFE_INTEGER,
    label: null,
    entries: [] as RaceEntry[],
  };
}

/** A race the read carries beats either optimistic shape, so it gets its real number. */
export function deckRaces(
  eventId: string,
  races: readonly Race[] | undefined,
  standingMaterialisations: readonly OutboxItem[],
  entryRaceIds: readonly string[] = [],
): Race[] {
  const found = racesOfEvent(races, eventId);
  const known = new Set(found.map((r) => r.id));
  for (const item of standingMaterialisations) {
    if (known.has(item.id)) continue;
    if ((item.payload as RaceMaterialisePayload).eventId !== eventId) continue;
    known.add(item.id);
    found.push((races ?? []).find((r) => r.id === item.id) ?? optimisticMaterialisedRace(item));
  }
  for (const raceId of entryRaceIds) {
    if (known.has(raceId)) continue;
    known.add(raceId);
    found.push(entryStampedRace(raceId, eventId));
  }
  return found;
}
