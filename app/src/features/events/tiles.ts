import type { CarnivalEvent } from "../../api/types";
import { disciplineOf, eventName } from "./eventLabels";
import { sumProgress, type Progress, type QueuedProgress } from "./progress";

export type TileClassification = Pick<
  CarnivalEvent,
  "tier" | "scoresPoints" | "scoringMode" | "effectiveScoringMode"
>;

export interface EventTile {
  key: string;
  /** What staff call it: the definition name, its code, or its discipline. */
  name: string;
  discipline: string | null;
  /** Every instance behind the tile, in the order the server listed them. */
  events: CarnivalEvent[];
  /** The instance a tap opens — the division chips move from there. */
  target: CarnivalEvent;
  /** Only badges true of every instance. */
  classification: TileClassification;
  progress: Progress;
  openCount: number;
  /** Reversible (a late entry puts an instance back below the line): a readout, never a state. */
  completeCount: number;
}

/** A badge is raised only where it holds of every instance: one Social twin cannot badge a tile. */
function tileClassification(events: readonly CarnivalEvent[]): TileClassification {
  const tier = events.every((e) => e.tier === "Social") ? "Social" : "Competitive";
  // A scoring-mode badge means "this scores differently from its tier", which is
  // only honest for the tile when every instance overrides, and to the same mode.
  const modes = new Set(events.map((e) => e.effectiveScoringMode));
  const overridden = modes.size === 1 && events.every((e) => e.scoringMode !== null);
  const mode = overridden ? [...modes][0]! : tier === "Social" ? "Social" : "Places";
  return {
    tier,
    scoresPoints: events.some((e) => e.scoresPoints),
    scoringMode: overridden ? mode : null,
    effectiveScoringMode: mode,
  };
}

/** Tiles in first-appearance (program) order; `queued` overlays captures still on this device. */
export function buildEventTiles(
  events: CarnivalEvent[],
  queued?: Map<string, QueuedProgress>,
): EventTile[] {
  const grouped = new Map<string, CarnivalEvent[]>();
  for (const event of events) {
    const siblings = grouped.get(event.eventDefinitionId);
    if (siblings) siblings.push(event);
    else grouped.set(event.eventDefinitionId, [event]);
  }
  return [...grouped].map(([key, instances]) => {
    const target = instances[0]!;
    return {
      key,
      name: eventName(target),
      discipline: disciplineOf(target),
      events: instances,
      target,
      classification: tileClassification(instances),
      progress: sumProgress(instances, queued),
      openCount: instances.filter((e) => e.status === "Open").length,
      // Completion is the server's `complete` flag; nothing enters event status Finalised any more.
      completeCount: instances.filter((e) => e.complete).length,
    };
  });
}

/** Reads the event list's `officials`, so it still answers with no uplink. */
export function isAssignedTo(event: CarnivalEvent, subject: string | null): boolean {
  if (!subject) return false;
  return (event.officials ?? []).some((o) => o.subjectId === subject);
}

/** Ordering only, never access control: the server alone decides who may submit what. */
export function assignedEvents(events: CarnivalEvent[], subject: string | null): CarnivalEvent[] {
  return events.filter((e) => isAssignedTo(e, subject));
}
