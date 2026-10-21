import { useState } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  getCarnival,
  getCarnivalSchedule,
  getCarnivalSettings,
  listCarnivalEvents,
} from "../api/resources";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { ScheduleBuilder } from "../features/schedule/ScheduleBuilder";
import { href } from "../router";
import { Freshness, oldestUpdatedAt } from "../shell/Freshness";

// Advisory: nothing built here gates entry, result capture or the event lifecycle.
export default function ScheduleBuilderScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const schedule = useApiQuery(keys.schedule(carnivalId), () => getCarnivalSchedule(carnivalId));
  const events = useApiQuery(keys.carnivalEvents(carnivalId), () => listCarnivalEvents(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );
  const invalidate = useInvalidate();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const c = carnival.data;
  const isFinal = c?.status === "Finalised";
  // Only avoids offering controls that would come back 403; the server is the gate.
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator") && !isFinal;

  // Reports whether the write landed: forms clear themselves only then, or a refusal loses typing.
  async function run(action: () => Promise<unknown>): Promise<boolean> {
    setError(null);
    setBusy(true);
    try {
      await action();
      return true;
    } catch (err) {
      // Direct writes: the shared offline copy would promise a save this screen cannot make.
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
      return false;
    } finally {
      // Even on failure: a re-order is several PUTs and a part-way failure leaves the earlier ones
      // applied. A placement moves events between slots, so the event list is stale too.
      invalidate(keys.schedule(carnivalId), keys.carnivalEvents(carnivalId));
      setBusy(false);
    }
  }

  if (carnival.loading) return <p>Loading…</p>;
  if (!c)
    return carnival.error ? (
      <div className="alert error" role="alert">
        {carnival.error.message}
      </div>
    ) : null;

  const loadError = carnival.error ?? schedule.error ?? events.error;

  return (
    <>
      <p>
        <a href={href(`/carnivals/${carnivalId}`)}>← Carnival</a>
      </p>
      <h1>Program</h1>
      <p className="row-sub">
        {c.name} · {c.status} · {c.date}
        {c.venue ? ` · ${c.venue}` : ""}
        <Freshness
          updatedAt={oldestUpdatedAt(schedule.updatedAt, events.updatedAt)}
          isRefetching={schedule.isRefetching || events.isRefetching}
        />
      </p>
      <div className="button-row">
        <a className="button primary-link" href={href(`/carnivals/${carnivalId}/program`)}>
          🖨 Printable program
        </a>
      </div>

      {isFinal && (
        <div className="alert notice" role="status">
          This carnival is finalised, so the program is read-only — it is evidence of what was run.
        </div>
      )}
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

      {(schedule.loading || events.loading) && <p>Loading the program…</p>}
      {events.data && (
        <ScheduleBuilder
          carnivalId={carnivalId}
          schedule={schedule.data}
          events={events.data}
          adjustmentCategories={settings.data?.adjustmentCategories ?? []}
          canManage={canManage}
          busy={busy}
          onRun={run}
        />
      )}
    </>
  );
}
