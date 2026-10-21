import type { Race } from "../../api/types";

export interface Heat {
  /** Null only for the prospective first heat: no race exists yet. */
  raceId: string | null;
  /** 1-based, within the division. */
  ordinal: number;
  label: string;
  /** Minted on this device and not yet carried by the races read. */
  queued: boolean;
}

export function heatLabel(ordinal: number): string {
  return `Heat ${ordinal}`;
}

/** Offered only off resolved reads: with the races read down, "no race" is a guess. */
export const PROSPECTIVE_HEAT: Heat = {
  raceId: null,
  ordinal: 1,
  label: heatLabel(1),
  queued: false,
};

/** A queued race has no server position: it sorts after every race that has one. */
function position(race: Race): number {
  return race.sortOrder < 0 ? Number.MAX_SAFE_INTEGER : race.sortOrder;
}

/** Empty is a real answer: a division whose races this device cannot see has no chip. */
export function heatsOfDivision(races: readonly Race[]): Heat[] {
  const ordered = [...races].sort(
    (a, b) => position(a) - position(b) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0),
  );
  return ordered.map((race, i) => ({
    raceId: race.id,
    ordinal: i + 1,
    label: heatLabel(i + 1),
    queued: race.sortOrder < 0,
  }));
}

/**
 * The ordinal is both label and order, as the server derives entry.heat; where legacy data
 * disagrees, the chip shows its members' heat but stays where it is.
 */
export function numberHeats(
  heats: readonly Heat[],
  scoringHeatOf: (raceId: string | null) => number | null,
): Heat[] {
  return heats.map((heat) => {
    const scoring = scoringHeatOf(heat.raceId);
    return scoring == null || scoring === heat.ordinal
      ? heat
      : { ...heat, label: heatLabel(scoring) };
  });
}

/** `?heat=` else the last heat; a `?heat` naming no race of the division also lands on the last. */
export function selectedHeat(heats: readonly Heat[], requested: string | null): Heat | null {
  return (
    heats.find((h) => h.raceId !== null && h.raceId === requested) ??
    heats[heats.length - 1] ??
    null
  );
}
