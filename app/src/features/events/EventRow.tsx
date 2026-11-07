import { useState } from "react";
import { changeEventStatus } from "../../api/resources";
import type { CarnivalEvent } from "../../api/types";
import { href } from "../../router";
import { EventClassification } from "./EventClassification";
import { divisionLabel } from "./eventLabels";

/** One scheduled event: link through to results and per-event open/close. */
export function EventRow({
  event,
  canManage,
  busy,
  onAction,
}: {
  event: CarnivalEvent;
  canManage: boolean;
  busy: boolean;
  onAction: (action: () => Promise<unknown>) => Promise<void>;
}) {
  const [rowBusy, setRowBusy] = useState(false);

  // Per-row busy so only the tapped row reads "Saving…", while `busy` locks every other row's
  // buttons for the duration.
  async function act(action: () => Promise<unknown>) {
    setRowBusy(true);
    try {
      await onAction(action);
    } finally {
      setRowBusy(false);
    }
  }

  return (
    <li>
      <div className="entry-row">
        <a
          className="row-link row-grow"
          href={href(`/carnivals/${event.carnivalId}/events/${event.id}`)}
        >
          <span className="row-main">
            <span className="row-title">
              {event.definitionName ?? event.definitionCode ?? event.eventDefinitionId}
            </span>
            <span className="row-sub">
              {divisionLabel(event)} · {event.entryCount ?? 0} entries
              {event.officials && event.officials.length > 0
                ? ` · officials: ${event.officials.map((o) => o.subjectDisplayName ?? o.subjectId).join(", ")}`
                : " · no officials assigned"}
            </span>
          </span>
        </a>
        <EventClassification event={event} />
        <span className={`badge ${event.status === "Open" ? "ok" : "muted"}`}>{event.status}</span>
        {canManage && event.status === "Scheduled" && (
          <button
            disabled={busy}
            onClick={() => void act(() => changeEventStatus(event.carnivalId, event.id, "Open"))}
          >
            {rowBusy ? "Saving…" : "Open"}
          </button>
        )}
        {canManage && event.status === "Open" && (
          <button
            disabled={busy}
            onClick={() => void act(() => changeEventStatus(event.carnivalId, event.id, "Closed"))}
          >
            {rowBusy ? "Saving…" : "Close"}
          </button>
        )}
        {canManage && event.status === "Closed" && (
          <button
            disabled={busy}
            onClick={() => void act(() => changeEventStatus(event.carnivalId, event.id, "Open"))}
          >
            {rowBusy ? "Saving…" : "Reopen"}
          </button>
        )}
      </div>
    </li>
  );
}
