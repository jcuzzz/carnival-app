import { useEffect, useMemo, useState } from "react";
import { errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { getCarnival, listCarnivalEvents } from "../api/resources";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { syncSelectedCarnival } from "../carnivals/selectedCarnival";
import { EventList } from "../features/events/EventList";
import { carnivalTypeLabel } from "../features/events/eventLabels";
import { EventTiles } from "../features/events/EventTiles";
import { queuedProgressByEvent, UNDELIVERED_STATUSES } from "../features/events/progress";
import { href } from "../router";
import { Freshness, oldestUpdatedAt } from "../shell/Freshness";
import { useOutboxItems } from "../sync/useOutboxStatus";

// Tiles and list render from the one cached event payload, so the deck works with no uplink.
export default function CarnivalDetailScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const events = useApiQuery(keys.carnivalEvents(carnivalId), () =>
    listCarnivalEvents(carnivalId),
  );
  const empty = events.data !== undefined && events.data.length === 0;
  const invalidate = useInvalidate();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const c = carnival.data;
  useEffect(() => {
    if (c) syncSelectedCarnival(c);
  }, [c]);

  // Tile progress is a server aggregate and the uplink may have died hours ago, so undelivered
  // captures are overlaid. Index-scoped: this re-runs on every Dexie change.
  const undelivered = useOutboxItems(UNDELIVERED_STATUSES);
  const queued = useMemo(() => queuedProgressByEvent(undelivered), [undelivered]);

  async function run(action: () => Promise<unknown>) {
    setError(null);
    setBusy(true);
    try {
      await action();
      invalidate(keys.carnival(carnivalId));
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  if (carnival.loading && !c) return <p>Loading…</p>;
  // Only a load with nothing cached is a dead end; a failed revalidation shows the banner above
  // the stale carnival, never instead of it.
  if (!c)
    return carnival.error ? (
      <div className="alert error" role="alert">
        {carnival.error.message}
      </div>
    ) : null;
  const loadError = carnival.error ?? events.error;

  return (
    <>
      <h1>
        {c.name} <span className={`badge ${c.status === "Open" ? "ok" : "muted"}`}>{c.status}</span>
      </h1>
      <p className="row-sub">
        {carnivalTypeLabel(c.type)} · {c.date}
        {c.venue ? ` · ${c.venue}` : ""}
        <Freshness
          updatedAt={oldestUpdatedAt(carnival.updatedAt, events.updatedAt)}
          isRefetching={carnival.isRefetching || events.isRefetching}
        />
      </p>
      {loadError && (
        <div className="alert error" role="alert">
          {loadError.message}
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      {events.loading && <p>Loading events…</p>}
      {empty && (
        <>
          <h2>Events</h2>
          {/* The Deck is for the day (ADR-0066 §7): setup lives on Manage. */}
          {c.status !== "Draft" ? (
            <p>No events in this carnival.</p>
          ) : canManage ? (
            <>
              <p>This carnival isn't set up yet.</p>
              <div className="button-row">
                <a className="button primary-link" href={href(`/carnivals/${carnivalId}/manage`)}>
                  Set the carnival up
                </a>
              </div>
            </>
          ) : (
            <p>Nothing to capture yet: this carnival is still being set up.</p>
          )}
        </>
      )}
      {events.data && events.data.length > 0 && (
        <>
          {c.status === "Draft" && (
            <div className="alert notice" role="status">
              This carnival isn't open yet.
              {canManage && (
                <>
                  {" "}
                  Results can be entered once it's opened on{" "}
                  <a href={href(`/carnivals/${carnivalId}/manage`)}>Manage</a>.
                </>
              )}
            </div>
          )}
          <EventTiles events={events.data} subject={session?.subject ?? null} queued={queued} />

          <details className="subsection">
            <summary>Find an event — search, filter and manage all {events.data.length} divisions</summary>
            <div className="disclosure-body">
              <EventList events={events.data} canManage={canManage} busy={busy} onAction={run} />
            </div>
          </details>
        </>
      )}
    </>
  );
}
