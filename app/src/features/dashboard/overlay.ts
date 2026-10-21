import type { CarnivalProgress, EventProgress, ProgressTotals } from "../../api/types";
import type { QueuedProgress } from "../events/progress";

export const COMPLETE = "complete";
export const IN_PROGRESS = "inProgress";
export const NOT_STARTED = "notStarted";

export interface OverlaidProgress {
  events: EventProgress[];
  totals: ProgressTotals;
}

export function overlayQueued(
  progress: CarnivalProgress,
  queued: Map<string, QueuedProgress>,
): OverlaidProgress {
  const events = progress.events.map((event) => {
    const held = queued.get(event.carnivalEventId);
    if (!held || (held.entries === 0 && held.results === 0)) return event;
    // Complete on fewer results than entries is the server saying Social, which a queued capture
    // must not reopen.
    const owesNoResults = event.status === COMPLETE && event.recorded < event.expected;
    // Offline adds enlarge the denominator exactly as they do in sumProgress;
    // without that, an add on a finished event vanishes instead of reopening it.
    const expected = event.expected + held.entries;
    const recorded = Math.min(event.recorded + held.results, expected);
    return {
      ...event,
      expected,
      recorded,
      status: owesNoResults ? COMPLETE : statusOf(expected, recorded),
    };
  });

  return {
    events,
    totals: {
      ...progress.totals,
      eventsComplete: events.filter((e) => e.status === COMPLETE).length,
      eventsInProgress: events.filter((e) => e.status === IN_PROGRESS).length,
      eventsNotStarted: events.filter((e) => e.status === NOT_STARTED).length,
      expected: events.reduce((sum, e) => sum + e.expected, 0),
      recorded: events.reduce((sum, e) => sum + e.recorded, 0),
    },
  };
}

/** The server's own derivation (ProgressService.StatusOf), over overlaid counts. */
function statusOf(expected: number, recorded: number): string {
  if (expected === 0) return NOT_STARTED;
  if (recorded >= expected) return COMPLETE;
  return recorded === 0 ? NOT_STARTED : IN_PROGRESS;
}

/** What still owes a person, kept in the server's program order. */
export function stillToRecord(events: EventProgress[]): EventProgress[] {
  return events.filter((e) => e.status !== COMPLETE);
}
