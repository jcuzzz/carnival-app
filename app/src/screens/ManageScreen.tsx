import { useEffect, useRef, useState } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  changeCarnivalStatus,
  getCarnival,
  getCarnivalSchedule,
  getCarnivalSettings,
  getProgress,
  listAdjustments,
  listCarnivalDevices,
  listCarnivalEvents,
  listEventDefinitions,
  openScheduledEvents,
} from "../api/resources";
import type { CarnivalStatus } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { adjustmentItemsForCarnival } from "../adjustments/submitAdjustment";
import { hasRole, useSession } from "../auth/session";
import { syncSelectedCarnival } from "../carnivals/selectedCarnival";
import { ApplyTemplateSection } from "../features/carnivals/ApplyTemplateSection";
import { SetupChecklist } from "../features/carnivals/SetupChecklist";
import { setupSteps } from "../features/carnivals/setupSteps";
import { counted } from "../features/events/counted";
import { carnivalSport } from "../features/events/eventLabels";
import { DEVICE_TICK_MS, DevicesPanel, useNow } from "../features/devices/DevicesPanel";
import { AdjustmentsPanel } from "../features/standings/AdjustmentsPanel";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { ScoringRulesEditor } from "../features/standings/ScoringRulesEditor";
import { VenueLanesEditor } from "../features/races/VenueLanesEditor";
import { clockOffsetMs, finaliseReason, fleetWarning } from "../devices/deviceFleet";
import { href } from "../router";
import type { OutboxStatus } from "../db/db";
import { showToast } from "../shell/toast";
import { useOutboxItems } from "../sync/useOutboxStatus";

const NEXT_CARNIVAL_STATUS: Partial<Record<CarnivalStatus, CarnivalStatus>> = {
  Draft: "Open",
  Open: "Closed",
  Closed: "Finalised",
};

const PENDING_STATUSES: readonly OutboxStatus[] = ["queued", "syncing"];

const READ_ERROR_TEXT = "Can't reach the server, so the carnival couldn't be loaded.";
/** The same failure with the cached carnival ON SCREEN: it loaded, it may be old. */
const STALE_READ_ERROR_TEXT =
  "Can't reach the server, so this carnival's details may be out of date.";

// Finalise keeps its own second tap: window.confirm is suppressed in some iOS standalone contexts.
export default function ManageScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const gate = useAccessGate(canManage);
  // Imports (manage and view) are SystemAdministrator-only on the server; offer no link that only
  // refuses.
  const canImport = hasRole(session, "SystemAdministrator");
  // Gated: a refused official should not spend a venue's uplink on three reads
  // whose screen they will never see.
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId), {
    enabled: canManage,
  });
  const settings = useApiQuery(
    keys.carnivalSettings(carnivalId),
    () => getCarnivalSettings(carnivalId),
    { enabled: canManage },
  );
  const adjustments = useApiQuery(keys.adjustments(carnivalId), () => listAdjustments(carnivalId), {
    enabled: canManage,
  });
  const events = useApiQuery(keys.carnivalEvents(carnivalId), () => listCarnivalEvents(carnivalId), {
    enabled: canManage,
  });
  const schedule = useApiQuery(keys.schedule(carnivalId), () => getCarnivalSchedule(carnivalId), {
    enabled: canManage,
  });
  // Only the catalogue knows what is left to add; the setup screen's key, so usually cached.
  const definitions = useApiQuery(keys.eventDefinitions(true), () => listEventDefinitions(true), {
    enabled: canManage,
  });
  // The only roster count below SystemAdministrator: the import surface needs imports:view.
  const progress = useApiQuery(keys.progress(carnivalId), () => getProgress(carnivalId), {
    enabled: canManage,
  });
  const devices = useApiQuery(keys.devices(carnivalId), () => listCarnivalDevices(carnivalId), {
    enabled: canManage,
    // Polled: a device going quiet is an event only the server sees.
    refetchIntervalMs: DEVICE_TICK_MS,
  });
  // A device goes stale by the clock, with no read to announce it — corrected
  // for the difference between this desk's clock and the server's.
  const now =
    useNow(DEVICE_TICK_MS) + clockOffsetMs(devices.data?.serverNowUtc, devices.updatedAt);
  const invalidate = useInvalidate();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  // Beside the bulk-open button: CARNIVAL_NOT_OPEN is about the control just pressed.
  const [openError, setOpenError] = useState<string | null>(null);

  const c = carnival.data;
  useEffect(() => {
    if (c) syncSelectedCarnival(c);
  }, [c]);

  const pending = adjustmentItemsForCarnival(useOutboxItems(PENDING_STATUSES), carnivalId);
  const pendingCount = pending.length;
  const previousPendingCount = useRef(pendingCount);
  useEffect(() => {
    const drained = pendingCount < previousPendingCount.current;
    previousPendingCount.current = pendingCount;
    if (!drained) return;
    invalidate(keys.standings(carnivalId), keys.adjustments(carnivalId));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pendingCount]);

  async function change(to: CarnivalStatus, reason?: string): Promise<boolean> {
    setError(null);
    setBusy(true);
    try {
      await (reason === undefined
        ? changeCarnivalStatus(carnivalId, to)
        : changeCarnivalStatus(carnivalId, to, reason));
      // Prefix key: a lifecycle transition can change the carnival, its events
      // and everything scored off them.
      invalidate(keys.carnival(carnivalId));
      return true;
    } catch (err) {
      // A direct write: the "saved on this device" default would be false.
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
      return false;
    } finally {
      setBusy(false);
      setConfirming(false);
    }
  }

  async function openCarnivalAndDivisions(): Promise<void> {
    if (await change("Open")) await openAll();
  }

  async function openAll(): Promise<void> {
    setOpenError(null);
    setBusy(true);
    try {
      const result = await openScheduledEvents(carnivalId);
      invalidate(keys.carnival(carnivalId));
      showToast(`${counted(result.opened, "division")} opened.`);
    } catch (err) {
      setOpenError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="Manage carnival">
        <p>Sign in to manage this carnival.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>Manage carnival</h1>
        <div className="alert error" role="alert">
          Managing a carnival needs the carnival administrator role.
        </div>
        <p>
          <a href={href(`/carnivals/${carnivalId}`)}>← Back to the deck</a>
        </p>
      </>
    );
  }

  if (carnival.loading && !c) return <p>Loading…</p>;
  if (!c)
    return carnival.error ? (
      <div className="alert error" role="alert">
        {errorText(carnival.error, READ_ERROR_TEXT)}
      </div>
    ) : null;

  const next = NEXT_CARNIVAL_STATUS[c.status];
  const isFinal = c.status === "Finalised";
  const warning = fleetWarning(devices.data?.devices, now, devices.error !== undefined);
  const overrideReason = finaliseReason(devices.data?.devices, now, devices.error !== undefined);
  const steps = setupSteps({
    carnivalId,
    status: c.status,
    participation: progress.error ? null : progress.data?.participation,
    canImport,
    rosterYear: settings.data?.rosterYear,
    carnivalDate: c.date,
    profile: settings.data?.divisions,
    profileDefault: settings.data?.divisionsDefault,
    events: events.data,
    definitions: definitions.data,
    sport: carnivalSport(c.type),
    schedule: schedule.data,
  });
  const scheduled = (events.data ?? []).filter((e) => e.status === "Scheduled").length;

  return (
    <>
      <h1>Manage carnival</h1>
      <p className="row-sub">
        {c.name} · {c.type} · {c.date} · {c.status}
      </p>
      {carnival.error && (
        <div className="alert error" role="alert">
          {errorText(carnival.error, STALE_READ_ERROR_TEXT)}
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <section className="subsection">
        <h2>Set the carnival up</h2>
        <SetupChecklist steps={steps} currentPath={`/carnivals/${carnivalId}/manage`} />
      </section>

      {!isFinal && (
        <ApplyTemplateSection
          carnivalId={carnivalId}
          carnivalType={c.type}
          locked={c.status === "Closed"}
        />
      )}

      <section className="subsection">
        <h2>Reports</h2>
        <ul className="row-list card">
          <li>
            <a className="row-link" href={href(`/carnivals/${carnivalId}/reports`)}>
              <span className="row-main">
                <span className="row-title">Standings and results book</span>
                <span className="row-sub">
                  Print, save as PDF, or export either as a CSV — every export is audited
                </span>
              </span>
            </a>
          </li>
          <li>
            <a className="row-link" href={href("/school/records")}>
              <span className="row-main">
                <span className="row-title">School records</span>
                <span className="row-sub">
                  School-wide, not per carnival — withdraw a record minted in error
                </span>
              </span>
            </a>
          </li>
        </ul>
      </section>

      <DevicesPanel devices={devices} now={now} />

      <section className="subsection">
        <AdjustmentsPanel
          carnivalId={carnivalId}
          canManage={!isFinal}
          settings={settings.data}
          adjustments={adjustments}
          pending={pending}
          actorSubject={session?.subject ?? null}
          actorDisplayName={session?.displayName}
        />
        {!isFinal && settings.data && (
          <ScoringRulesEditor
            carnivalId={carnivalId}
            settings={settings.data}
            onSaved={() =>
              invalidate(keys.carnivalSettings(carnivalId), keys.standings(carnivalId))
            }
          />
        )}
      </section>

      {!isFinal && settings.data && (
        <VenueLanesEditor
          carnivalId={carnivalId}
          settings={settings.data}
          onSaved={() => invalidate(keys.carnivalSettings(carnivalId))}
        />
      )}

      <section className="subsection">
        <h2>Carnival status</h2>
        <p className="row-sub">
          Currently <strong>{c.status}</strong>. Closing is recoverable; finalising is not.
        </p>
        {isFinal && <p className="row-sub">Finalised carnivals cannot be reopened.</p>}
        <div className="button-row">
          {next && next !== "Finalised" && (
            <button className="primary" disabled={busy} onClick={() => void change(next)}>
              {busy ? "Saving…" : next === "Open" ? "Open carnival" : "Close carnival"}
            </button>
          )}
          {/* Every division is born Scheduled, so opening the carnival offers the bulk open. */}
          {c.status === "Draft" && scheduled > 0 && (
            <button disabled={busy} onClick={() => void openCarnivalAndDivisions()}>
              {busy ? "Saving…" : `Open carnival and all ${scheduled} divisions`}
            </button>
          )}
          {c.status !== "Draft" && !isFinal && scheduled > 0 && (
            <button disabled={busy} onClick={() => void openAll()}>
              {busy ? "Saving…" : `Open all divisions (${scheduled})`}
            </button>
          )}
          {/* The escape hatch for a carnival closed one result early: the server
              admits Closed → Open, and without this the only way there is curl. */}
          {c.status === "Closed" && (
            <button disabled={busy} onClick={() => void change("Open")}>
              {busy ? "Saving…" : "Reopen carnival"}
            </button>
          )}
        </div>
        {openError !== null && (
          <div className="alert error" role="alert">
            {openError}
          </div>
        )}
        {c.status === "Closed" && scheduled > 0 && (
          <p className="row-sub">
            Opening divisions on a Closed carnival readies them; results are still only accepted
            once the carnival is reopened.
          </p>
        )}
      </section>

      {next === "Finalised" && (
        <section className="subsection danger-zone">
          <h2>Finalise</h2>
          <p className="row-sub">
            Finalising publishes the results. The record book and the district list are derived
            from it, and it cannot be undone.
          </p>
          {/* A warning, never a block: an offline device and a lost one look identical
              from the server. */}
          {warning && (
            <div
              className={`alert ${warning.level === "parked" || warning.level === "unreadable"
                ? "notice"
                : "error"}`}
              role="status"
            >
              {warning.text}
            </div>
          )}
          <div className="button-row">
            {confirming ? (
              <>
                <button
                  className="danger"
                  disabled={busy}
                  onClick={() => void change("Finalised", overrideReason)}
                >
                  {busy ? "Saving…" : "Confirm finalise?"}
                </button>
                <button type="button" disabled={busy} onClick={() => setConfirming(false)}>
                  Cancel
                </button>
              </>
            ) : (
              <button className="danger" disabled={busy} onClick={() => setConfirming(true)}>
                Finalise carnival
              </button>
            )}
          </div>
        </section>
      )}
    </>
  );
}
