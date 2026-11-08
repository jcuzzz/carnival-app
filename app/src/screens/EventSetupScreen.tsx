import { useEffect, useRef, useState } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  generateDivisions,
  getCarnival,
  getCarnivalSettings,
  listCarnivalEvents,
  listEventDefinitions,
  updateCarnivalSettings,
} from "../api/resources";
import type { CarnivalSettings, DivisionProfile, GenerateDivisionRequest } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { ApplyTemplateSection } from "../features/carnivals/ApplyTemplateSection";
import { RosterRequiredNotice } from "../features/carnivals/RosterRequiredNotice";
import {
  ensureProfile,
  fullMatrixSpecs,
  rosterDefaultToSave,
} from "../features/events/addDivisions";
import { agesLine, leftOutText, staleText } from "../features/events/agesLine";
import { counted } from "../features/events/counted";
import { carnivalSport } from "../features/events/eventLabels";
import { buildProfileReview, outstandingSummary } from "../features/events/profileReview";
import {
  buildEventSetupRows,
  setupRowSummary,
  splitSetupRows,
  usageSummary,
  type EventSetupRow,
} from "../features/events/setupRows";
import { href } from "../router";
import { Freshness, oldestUpdatedAt } from "../shell/Freshness";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

interface AddPress {
  specs: readonly GenerateDivisionRequest[];
  /** The roster default to save first, or null where a profile is stored. */
  defaultProfile: DivisionProfile | null;
  disabled: boolean;
  onAdded: (definitionId: string) => void;
}

// A retired definition this carnival contests stays listed, badged, not vanishing from the list
// that sent you to the editor. One with no division is on neither; the catalogue is the way back.
export default function EventSetupScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const [showOtherSports, setShowOtherSports] = useState(false);
  const [justAdded, setJustAdded] = useState<string | null>(null);
  const inCarnivalHeading = useRef<HTMLHeadingElement>(null);
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const canImport = hasRole(session, "SystemAdministrator");
  const connectivity = useConnectivity();
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const definitions = useApiQuery(keys.eventDefinitions(true), () => listEventDefinitions(true));
  const events = useApiQuery(keys.carnivalEvents(carnivalId), () => listCarnivalEvents(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );

  // Data, not `loading`: either query may be served from cache while the other
  // is still out, and an unread event list knows nothing about any row.
  const divisionsKnown = events.data !== undefined;
  const rows = buildEventSetupRows(definitions.data ?? [], events.data ?? []);
  const loadError = carnival.error ?? definitions.error ?? events.error;
  const outstanding = divisionsKnown ? buildProfileReview(settings.data?.divisions, rows) : [];
  const locked = carnival.data?.status === "Closed" || carnival.data?.status === "Finalised";
  // Same caution as the row summary: an unread event list cannot say a
  // definition is absent from this carnival, so nothing folds until it lands.
  const sport = divisionsKnown ? carnivalSport(carnival.data?.type) : "";
  const { inCarnival, available, otherSports } = splitSetupRows(rows, sport);

  const specs = fullMatrixSpecs(settings.data?.divisions ?? settings.data?.divisionsDefault);
  const line = agesLine(
    settings.data?.divisions,
    settings.data?.divisionsDefault,
    settings.data?.rosterAgeGroups,
  );
  // ADR-0067 §5: nothing saved and no roster to default from, so no event can be added yet.
  // As the checklist's Events step: a carnival with events was set up some other way.
  const noRoster =
    settings.data !== undefined &&
    settings.data.divisions == null &&
    settings.data.divisionsDefault == null &&
    events.data?.length === 0;
  const setupOpen = canManage && !locked;
  // Someone who cannot manage this carnival gets no Add, as New event… is withheld.
  const add: AddPress | undefined = canManage
    ? {
        specs,
        defaultProfile: rosterDefaultToSave(
          settings.data?.divisions,
          settings.data?.divisionsDefault,
        ),
        disabled: locked || connectivity !== "connected" || specs.length === 0,
        onAdded: setJustAdded,
      }
    : undefined;

  // The added row leaves the list the press was in, taking the keyboard's place
  // with it: land on the list it moved into, once the refetches have settled.
  const added = justAdded !== null && inCarnival.some((row) => row.definition.id === justAdded);
  useEffect(() => {
    if (!added) return;
    inCarnivalHeading.current?.focus();
    setJustAdded(null);
  }, [added]);

  return (
    <>
      <p>
        <a href={href(`/carnivals/${carnivalId}/manage`)}>← Manage</a>
      </p>
      <h1>Event setup</h1>
      {carnival.data && (
        <p className="row-sub">
          {carnival.data.name} · {carnival.data.status} · {carnival.data.date}
          <Freshness
            updatedAt={oldestUpdatedAt(definitions.updatedAt, events.updatedAt)}
            isRefetching={definitions.isRefetching || events.isRefetching}
          />
        </p>
      )}
      <p className="row-sub">
        What this carnival runs, and what the catalogue has left to add — editing an event
        changes it for every carnival that runs it.
      </p>
      {line && (
        <div className="button-row">
          <span className="row-sub">{line.text}</span>
          {canManage && (
            <a className="button" href={href(`/carnivals/${carnivalId}/setup/divisions`)}>
              Ages and categories
            </a>
          )}
        </div>
      )}
      {line && line.leftOut.length > 0 && (
        <p className="row-sub">{leftOutText(line.leftOut)}</p>
      )}
      {settings.error && (
        <div className="alert error" role="alert">
          {errorText(
            settings.error,
            "Can’t reach the server, so this carnival’s ages couldn’t be read.",
          )}
        </div>
      )}
      {setupOpen && line && line.stale.length > 0 && settings.data && (
        <StaleAgesNotice
          carnivalId={carnivalId}
          settings={settings.data}
          stale={line.stale}
          online={connectivity === "connected"}
        />
      )}
      {setupOpen && noRoster && (
        <RosterRequiredNotice carnivalId={carnivalId} canImport={canImport} />
      )}
      {loadError && (
        <div className="alert error" role="alert">
          {loadError.message}
        </div>
      )}
      {outstanding.length > 0 && (
        <div className="alert notice" role="status">
          <p className="flush-top">
            This carnival's ages include some these events don't run. Changing the ages adds no
            divisions — tick the new boxes on each event that should run them.
          </p>
          <ul className="row-list">
            {outstanding.map((item) => (
              <li key={item.definition.id}>
                <a href={href(`/carnivals/${carnivalId}/setup/events/${item.definition.id}`)}>
                  {item.definition.name} — {outstandingSummary(item)}
                </a>
              </li>
            ))}
          </ul>
        </div>
      )}

      {definitions.loading && <p>Loading the event catalogue…</p>}
      {!definitions.loading && !divisionsKnown && !events.error && (
        <p>Counting this carnival's divisions…</p>
      )}
      {definitions.data && rows.length === 0 && <p>The event catalogue is empty.</p>}
      {setupOpen && !noRoster && events.data && carnival.data && (
        <ApplyTemplateSection
          carnivalId={carnivalId}
          carnivalType={carnival.data.type}
          empty={events.data.length === 0}
          canImport={canImport}
        />
      )}
      {!divisionsKnown && rows.length > 0 && (
        <ul className="row-list card">
          {rows.map((row) => (
            <SetupRow key={row.definition.id} carnivalId={carnivalId} row={row} known={false} />
          ))}
        </ul>
      )}

      {divisionsKnown && rows.length > 0 && (
        <>
          <section className="subsection">
            <h2 ref={inCarnivalHeading} tabIndex={-1}>
              In this carnival
            </h2>
            {inCarnival.length > 0 ? (
              <ul className="row-list card">
                {inCarnival.map((row) => (
                  <SetupRow key={row.definition.id} carnivalId={carnivalId} row={row} known />
                ))}
              </ul>
            ) : (
              <p>No event runs here yet.</p>
            )}
          </section>
          <section className="subsection">
            <h2>Available to add</h2>
            {available.length > 0 ? (
              <ul className="row-list card">
                {available.map((row) => (
                  <SetupRow
                    key={row.definition.id}
                    carnivalId={carnivalId}
                    row={row}
                    known
                    add={add}
                  />
                ))}
              </ul>
            ) : (
              <p>{nothingToAdd(sport, inCarnival.length > 0)}</p>
            )}
          </section>
        </>
      )}

      {canManage && carnival.data !== undefined && (
        <div className="button-row">
          {!locked &&
            (connectivity === "connected" ? (
              <a className="button" href={href(`/carnivals/${carnivalId}/setup/events/new`)}>
                New event…
              </a>
            ) : (
              <span className="row-sub">
                Can’t reach the server, so an event can’t be added right now.
              </span>
            ))}
        </div>
      )}

      {otherSports.length > 0 && (
        <div className="button-row">
          <button
            type="button"
            aria-expanded={showOtherSports}
            onClick={() => setShowOtherSports((open) => !open)}
          >
            {showOtherSports ? "Hide" : "Show"} {otherSports.length} from other sports
          </button>
        </div>
      )}
      {showOtherSports && otherSports.length > 0 && (
        <ul className="row-list card">
          {otherSports.map((row) => (
            <SetupRow key={row.definition.id} carnivalId={carnivalId} row={row} known add={add} />
          ))}
        </ul>
      )}
    </>
  );
}

// ADR-0067 §6: a re-import never rewrites saved ages. Removing the stale ones keeps any
// deliberate narrowing; with nothing left, the student list's ages are what remains to use.
function StaleAgesNotice({
  carnivalId,
  settings,
  stale,
  online,
}: {
  carnivalId: string;
  settings: CarnivalSettings;
  stale: readonly string[];
  online: boolean;
}) {
  const invalidate = useInvalidate();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function adopt(): Promise<void> {
    setSaving(true);
    setError(null);
    const kept = (settings.divisions?.ageGroups ?? []).filter((code) => !stale.includes(code));
    try {
      await updateCarnivalSettings(carnivalId, {
        divisions: {
          ageGroups: kept.length > 0 ? kept : [...settings.rosterAgeGroups],
          categories: settings.divisions?.categories ?? null,
          tiers: settings.divisions?.tiers ?? null,
        },
      });
      invalidate(keys.carnivalSettings(carnivalId));
      showToast(
        kept.length > 0
          ? `${stale.join(", ")} removed from this carnival’s ages.`
          : "This carnival now uses the student list’s ages.",
      );
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      <div className="alert warn" role="status">
        <p className="flush-top">{staleText(stale)}</p>
        <div className="button-row">
          <button type="button" disabled={saving || !online} onClick={() => void adopt()}>
            {saving ? "Saving…" : "Remove those ages"}
          </button>
          {!online && <span>Can’t reach the server, so the ages can’t be changed right now.</span>}
        </div>
      </div>
      {error !== null && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
    </>
  );
}

function nothingToAdd(sport: string, anyHere: boolean): string {
  const of = sport === "" ? "event" : `${sport} event`;
  return anyHere
    ? `Every ${of} in the catalogue is already in this carnival.`
    : `No ${of}s in the catalogue yet.`;
}

function SetupRow({
  carnivalId,
  row,
  known,
  add,
}: {
  carnivalId: string;
  row: EventSetupRow;
  known: boolean;
  add?: AddPress;
}) {
  const invalidate = useInvalidate();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [raced, setRaced] = useState(false);

  async function press(): Promise<void> {
    if (add === undefined) return;
    setError(null);
    setRaced(false);
    setPending(true);
    try {
      const ensured = await ensureProfile(
        carnivalId,
        add.defaultProfile,
        () => invalidate(keys.carnivalSettings(carnivalId)),
        "added",
      );
      if (ensured.kind === "raced") {
        setRaced(true);
        return;
      }
      if (ensured.kind === "failed") {
        setError(ensured.text);
        return;
      }
      const generated = await generateDivisions(carnivalId, {
        eventDefinitionId: row.definition.id,
        divisions: [...add.specs],
      });
      invalidate(keys.carnivalEvents(carnivalId), keys.carnivalSettings(carnivalId));
      showToast(`${row.definition.name} — ${counted(generated.created, "division")} added.`);
      add.onAdded(row.definition.id);
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setPending(false);
    }
  }

  return (
    <li>
      <div className="entry-row">
        <a
          className="row-link row-grow"
          href={href(`/carnivals/${carnivalId}/setup/events/${row.definition.id}`)}
        >
          <span className="row-main">
            <span className="row-title">{row.definition.name}</span>
            <span className="row-sub">{setupRowSummary(row, known)}</span>
            <span className="row-sub">{usageSummary(row.definition.usage)}</span>
          </span>
        </a>
        {!row.definition.isActive && <span className="badge muted">Retired</span>}
        {add && (
          <button
            type="button"
            className="add-event"
            aria-label={`Add ${row.definition.name}`}
            disabled={add.disabled || pending}
            onClick={() => void press()}
          >
            {pending ? "Adding…" : "Add"}
          </button>
        )}
      </div>
      {error !== null && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      {raced && (
        <div className="alert warn" role="status">
          The ages were set elsewhere since this screen loaded — nothing was added; press Add
          again to use them.
        </div>
      )}
    </li>
  );
}
