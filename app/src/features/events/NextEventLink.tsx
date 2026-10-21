import type { CarnivalEvent, Schedule } from "../../api/types";
import { href } from "../../router";
import { divisionChipLabel } from "./divisions";
import { nextScheduledEventId } from "./schedule";

/** A suggestion, not a wizard: it never gates or warns, and is absent when nothing follows. */
export function NextEventLink({
  event,
  events,
  schedule,
}: {
  event: CarnivalEvent;
  events: CarnivalEvent[] | undefined;
  schedule: Schedule | undefined;
}) {
  const nextId = nextScheduledEventId(schedule, event.id);
  if (!nextId) return null;
  // Without the cached event list (a cold offline start) the suggestion is skipped rather than
  // shown as a link to a UUID.
  const next = (events ?? []).find((e) => e.id === nextId);
  if (!next) return null;

  return (
    <a
      className="next-event"
      href={href(`/carnivals/${next.carnivalId}/events/${next.id}`)}
      title="The next event on the program. Events run in any order — this is only a suggestion."
    >
      <span className="next-event-label">Next event</span>
      <span className="next-event-name">
        {next.definitionName ?? next.definitionCode} · {divisionChipLabel(next)}
      </span>
      <span aria-hidden="true">→</span>
    </a>
  );
}
