import { useState } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { changeEventStatus } from "../../api/resources";
import type { CarnivalEvent } from "../../api/types";

// Offers the status change that fixes a refusing deck where the person who hit it is standing.
export function OpenEventNotice({
  event,
  canManage,
  onOpened,
}: {
  event: CarnivalEvent;
  canManage: boolean;
  onOpened: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function open(): Promise<void> {
    setBusy(true);
    setError(null);
    try {
      await changeEventStatus(event.carnivalId, event.id, "Open");
      onOpened();
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  // Scheduled → Open and Closed → Open are the only transitions the server
  // admits from here (`EventRow`), so no other status is offered a control.
  const openable = event.status === "Scheduled" || event.status === "Closed";

  return (
    <>
      <div className="alert notice" role="status">
        <p className="flush-top">
          Results are only accepted while the event is Open. Queued submissions for a{" "}
          {event.status} event will be held as conflicts until it reopens.
        </p>
        {canManage && openable && (
          <div className="button-row">
            <button type="button" disabled={busy} onClick={() => void open()}>
              {busy
                ? "Saving…"
                : event.status === "Scheduled"
                  ? "Open this event"
                  : "Reopen this event"}
            </button>
          </div>
        )}
      </div>
      {error !== null && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
    </>
  );
}
