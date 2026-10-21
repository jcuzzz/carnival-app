import { useEffect, useMemo, useRef } from "react";
import { keys } from "../api/queryKeys";
import {
  getCarnival,
  getCarnivalSettings,
  getProgress,
  getStandings,
  listCarnivalDevices,
} from "../api/resources";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { syncSelectedCarnival } from "../carnivals/selectedCarnival";
import { DEVICE_TICK_MS, useNow } from "../features/devices/DevicesPanel";
import { DevicesRow } from "../features/dashboard/DevicesRow";
import { ParticipationCard } from "../features/dashboard/ParticipationCard";
import { ProgressCard } from "../features/dashboard/ProgressCard";
import { RecordsStrip } from "../features/dashboard/RecordsStrip";
import { overlayQueued } from "../features/dashboard/overlay";
import { queuedProgressByEvent } from "../features/events/progress";
import { StandingsTable } from "../features/standings/StandingsTable";
import { clockOffsetMs } from "../devices/deviceFleet";
import { href } from "../router";
import { Freshness } from "../shell/Freshness";
import type { OutboxStatus } from "../db/db";
import { useOutboxItems } from "../sync/useOutboxStatus";

/** Awards still on their way to the server — what the drain detection watches. */
const PENDING_STATUSES: readonly OutboxStatus[] = ["queued", "syncing"];

export default function DashboardScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const standings = useApiQuery(keys.standings(carnivalId), () => getStandings(carnivalId));
  const progress = useApiQuery(keys.progress(carnivalId), () => getProgress(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );
  // Organiser-only on the server, so gated as Manage gates it; polled because a device going quiet
  // is an event only the server sees.
  const devices = useApiQuery(keys.devices(carnivalId), () => listCarnivalDevices(carnivalId), {
    enabled: canManage,
    refetchIntervalMs: DEVICE_TICK_MS,
  });
  const invalidate = useInvalidate();

  const c = carnival.data;
  useEffect(() => {
    if (c) syncSelectedCarnival(c);
  }, [c]);

  const outbox = useOutboxItems(PENDING_STATUSES);
  const queued = useMemo(() => queuedProgressByEvent(outbox), [outbox]);

  // Standings change only once queued work drains. Counted over all undelivered items: results
  // captured on the deck move the leaderboard too.
  const pendingCount = outbox?.length ?? 0;
  const previousPendingCount = useRef(pendingCount);
  useEffect(() => {
    const drained = pendingCount < previousPendingCount.current;
    previousPendingCount.current = pendingCount;
    if (!drained) return;
    invalidate(keys.standings(carnivalId), keys.progress(carnivalId));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingCount]);

  const now = useNow(DEVICE_TICK_MS) + clockOffsetMs(devices.data?.serverNowUtc, devices.updatedAt);

  const data = standings.data;
  const aggregate = progress.data;
  const overlaid = useMemo(
    () => (aggregate ? overlayQueued(aggregate, queued) : null),
    [aggregate, queued],
  );

  return (
    <>
      <div className="dash-title">
        <h1 className="flush-top">Dashboard</h1>
        {c && (
          <p className="row-sub">
            {c.name}
            {c.venue ? ` · ${c.venue}` : ""} · {c.date} · {c.status}
            <Freshness updatedAt={standings.updatedAt} isRefetching={standings.isRefetching} />
          </p>
        )}
      </div>

      <div className="dash-grid">
        <section className="card" aria-labelledby="standings-heading">
          <h2 id="standings-heading" className="flush-top">
            House standings{" "}
            {data?.provisional && <span className="badge warn">provisional</span>}
          </h2>
          {standings.loading && <p>Loading standings…</p>}
          {standings.error && (
            <div className="alert error" role="alert">
              {standings.error.message}
            </div>
          )}
          {data && data.houses.length === 0 && (
            <p>No points yet — standings appear as results and bonus points are recorded.</p>
          )}
          {data && data.houses.length > 0 && (
            <StandingsTable data={data} settings={settings.data} colour />
          )}
        </section>

        {aggregate && overlaid ? (
          <ProgressCard
            carnivalId={carnivalId}
            progress={overlaid}
            provisional={aggregate.provisional}
          />
        ) : (
          <section className="card" aria-labelledby="progress-heading">
            <h2 id="progress-heading" className="flush-top">
              Progress
            </h2>
            {progress.loading && <p>Loading progress…</p>}
            {progress.error && (
              <div className="alert error" role="alert">
                {progress.error.message}
              </div>
            )}
          </section>
        )}
      </div>

      {aggregate && (
        <RecordsStrip
          carnivalId={carnivalId}
          records={aggregate.recordsToday}
          provisional={aggregate.provisional}
        />
      )}
      {aggregate && <ParticipationCard houses={aggregate.participation} />}
      {canManage && devices.data && (
        <DevicesRow carnivalId={carnivalId} fleet={devices.data} now={now} />
      )}
      {canManage && !devices.data && devices.error && (
        <p className="row-sub">Couldn't check devices — the server can't be reached.</p>
      )}

      <div className="button-row">
        <a href={href(`/carnivals/${carnivalId}/program`)}>Printable program</a>
      </div>
    </>
  );
}
