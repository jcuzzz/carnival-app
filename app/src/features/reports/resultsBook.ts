import type { MeasureType, ResultRow } from "../../api/types";
import { heightKey } from "../../results/jumpCard";

// Order is preserved, never recomputed: the server placed these rows with the scoring ladder.

export interface HeatBlock {
  /** Stable across renders — a heat number is only unique within its event. */
  key: string;
  heat: number | null;
  rows: ResultRow[];
}

export interface EventBlock {
  carnivalEventId: string;
  eventCode: string;
  eventName: string;
  division: string;
  tier: ResultRow["tier"];
  heats: HeatBlock[];
}

export function groupResults(
  rows: readonly ResultRow[],
  /**
   * A non-Time division is one field however many flights it ran, so its rows stay one block.
   * Absent reads as Time.
   */
  measureByEvent?: ReadonlyMap<string, MeasureType>,
): EventBlock[] {
  const blocks: EventBlock[] = [];
  for (const row of rows) {
    const timed = (measureByEvent?.get(row.carnivalEventId) ?? "Time") === "Time";
    let block = blocks.at(-1);
    if (block?.carnivalEventId !== row.carnivalEventId) {
      block = {
        carnivalEventId: row.carnivalEventId,
        eventCode: row.eventCode,
        eventName: row.eventName,
        division: row.division,
        tier: row.tier,
        heats: [],
      };
      blocks.push(block);
    }
    let heat = block.heats.at(-1);
    if (!heat || (timed && heat.heat !== row.heat)) {
      const label = timed ? (row.heat ?? "unheated") : "field";
      heat = {
        key: `${row.carnivalEventId}:${label}`,
        heat: timed ? row.heat : null,
        rows: [],
      };
      block.heats.push(heat);
    }
    heat.rows.push(row);
  }
  return blocks;
}

/** An unmarshalled entry is its own field, so it is named as such; null on a non-Time event. */
export function heatLabel(heat: number | null, measureType: MeasureType = "Time"): string | null {
  if (measureType !== "Time") return null;
  return heat === null ? "No heat" : `Heat ${heat}`;
}

/** Empty wherever the block is not a high-jump card, which is how the layout is chosen. */
export function blockHeights(rows: readonly ResultRow[]): number[] {
  const heights = new Map<string, number>();
  for (const row of rows)
    for (const jump of row.jumps ?? []) heights.set(heightKey(jump.height), jump.height);
  return [...heights.values()].sort((a, b) => a - b);
}
