import { useEffect, useState } from "react";
import { keys } from "../../api/queryKeys";
import { listHouses, searchEligibleStudentsPage } from "../../api/resources";
import type { EligibleStudent } from "../../api/types";
import { useApiQuery } from "../../api/useApiQuery";
import { filterRoster, loadCachedRosterPage, saveCachedRoster } from "../../db/roster";
import { submitTeamViaOutbox } from "../../entries/submitTeam";
import { showToast } from "../../shell/toast";

/** `limit: null` is a snapshot saved before the metadata was recorded: capped, extent unknown. */
interface RosterPage {
  students: EligibleStudent[];
  truncated: boolean;
  limit: number | null;
}

/**
 * Captured through the outbox as `team.create` so it can name a heat this device has only queued;
 * the server re-validates every leg when it drains.
 */
export function AddTeam({
  carnivalId,
  eventId,
  teamSize,
  legs,
  ageGroup,
  actorSubject,
  actorDisplayName,
  disabled,
  raceId,
  blocked,
  onAdded,
}: {
  carnivalId: string;
  eventId: string;
  teamSize: number;
  legs: string[] | null | undefined;
  /** The event's division, snapshotted onto a team entry server-side. */
  ageGroup: string;
  actorSubject: string | null;
  actorDisplayName?: string;
  disabled: boolean;
  /** A heat this device has only queued may be named: the capture sorts behind its mint. */
  raceId?: string | null;
  /** Why nothing can be added: a refused heat, whose adds would queue behind a race never made. */
  blocked?: string | undefined;
  onAdded: () => void;
}) {
  // From the read cache, so the house list still renders once the uplink is gone.
  const houses = useApiQuery(keys.houses(), () => listHouses()).data ?? [];
  const [house, setHouse] = useState<string | null>(null);
  const [slots, setSlots] = useState<(EligibleStudent | null)[]>(() => Array(teamSize).fill(null));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [roster, setRoster] = useState<RosterPage | null>(null);

  // Shares Add-entry's roster cache so an unreachable leg search falls back to the saved roster.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const cached = await loadCachedRosterPage(eventId);
      if (!cancelled && cached) setRoster(cached);
      try {
        const fresh = await searchEligibleStudentsPage(carnivalId, eventId, "");
        if (cancelled) return;
        setRoster(fresh);
        await saveCachedRoster(eventId, fresh.students, {
          truncated: fresh.truncated,
          limit: fresh.limit,
        });
      } catch {
        /* keep whatever was cached — the leg search falls back to it */
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [carnivalId, eventId]);

  function pickHouse(h: string) {
    setHouse(h);
    setSlots(Array(teamSize).fill(null));
    setError(null);
  }

  const chosenIds = slots.filter((s): s is EligibleStudent => s !== null).map((s) => s.studentId);
  const complete = slots.every((s) => s !== null);
  const allDistinct = new Set(chosenIds).size === chosenIds.length;

  async function submit() {
    if (blocked) return;
    if (!actorSubject) {
      setError("Sign in to add a team — the actor is captured on every entry.");
      return;
    }
    if (!house || !complete || !allDistinct) return;
    setBusy(true);
    setError(null);
    try {
      await submitTeamViaOutbox({
        carnivalId,
        carnivalEventId: eventId,
        houseCode: house,
        swimmers: slots.map((s) => s!),
        strokes: legs ?? null,
        ageGroup,
        actorSubject,
        ...(actorDisplayName !== undefined ? { actorDisplayName } : {}),
        raceId: raceId ?? null,
      });
      // A form clearing itself looks the same as a reset, so say it landed.
      showToast(`${house} team added.`);
      setHouse(null);
      setSlots(Array(teamSize).fill(null));
      onAdded();
    } catch {
      setError(`Couldn't queue the ${house} team on this device — try again.`);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="card">
      <h2 className="flush-top">Add relay team</h2>
      {disabled && <div className="alert notice">Entry is closed for this event.</div>}
      {blocked && (
        <div className="alert notice" role="status">
          {blocked}
        </div>
      )}
      <p className="row-sub flush-top">Pick a house, then fill each leg from that house.</p>
      <div className="button-row">
        {houses.map((h) => (
          <button
            key={h}
            type="button"
            className={h === house ? "primary" : ""}
            disabled={disabled}
            onClick={() => pickHouse(h)}
          >
            {h}
          </button>
        ))}
      </div>

      {house &&
        slots.map((slot, i) => (
          <TeamLegSlot
            key={`${house}-${i}`}
            legNo={i + 1}
            stroke={legs?.[i] ?? null}
            carnivalId={carnivalId}
            eventId={eventId}
            house={house}
            roster={roster}
            chosen={slot}
            excludeIds={chosenIds}
            disabled={disabled}
            onPick={(s) => setSlots((prev) => prev.map((v, j) => (j === i ? s : v)))}
            onClear={() => setSlots((prev) => prev.map((v, j) => (j === i ? null : v)))}
          />
        ))}

      {house && (
        <div className="button-row">
          <button
            type="button"
            className="primary"
            disabled={disabled || busy || blocked !== undefined || !complete || !allDistinct}
            onClick={() => void submit()}
          >
            Add {house} team
          </button>
          {!allDistinct && <span className="field-error">A swimmer can only take one leg.</span>}
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
    </div>
  );
}

function TeamLegSlot({
  legNo,
  stroke,
  carnivalId,
  eventId,
  house,
  roster,
  chosen,
  excludeIds,
  disabled,
  onPick,
  onClear,
}: {
  legNo: number;
  stroke: string | null;
  carnivalId: string;
  eventId: string;
  house: string;
  /** Cached eligible roster for this event (all houses); the search fallback. */
  roster: RosterPage | null;
  chosen: EligibleStudent | null;
  excludeIds: string[];
  disabled: boolean;
  onPick: (s: EligibleStudent) => void;
  onClear: () => void;
}) {
  const [term, setTerm] = useState("");
  const [page, setPage] = useState<RosterPage | null>(null);
  const [searchFailed, setSearchFailed] = useState(false);

  useEffect(() => {
    if (chosen) return;
    const handle = setTimeout(() => {
      void searchEligibleStudentsPage(carnivalId, eventId, term, house)
        .then((r) => {
          setPage(r);
          setSearchFailed(false);
        })
        .catch(() => setSearchFailed(true));
    }, 200);
    return () => clearTimeout(handle);
  }, [term, house, carnivalId, eventId, chosen]);

  // Unreachable server: search the saved roster; the house filter runs client-side because the
  // cache is stored per event.
  const source = searchFailed ? roster : page;
  const matched = searchFailed
    ? filterRoster(source?.students ?? [], term).filter((s) => s.houseCode === house)
    : (source?.students ?? []);
  const available = matched.filter((s) => !excludeIds.includes(s.studentId));
  // The six-row display cap below is silent on top of this: 7-50 matches show six, unremarked.
  const truncated = source?.truncated ?? false;
  const limit = source?.limit ?? null;

  return (
    <div className="team-leg">
      <span className="leg-label">
        Leg {legNo}
        {stroke ? ` · ${stroke}` : ""}
      </span>
      {chosen ? (
        <span className="team-leg-chosen">
          <span className="row-title">{chosen.displayName}</span>
          <span className="row-sub"> · Yr {chosen.yearLevel}</span>
          <button type="button" onClick={onClear} disabled={disabled}>
            change
          </button>
        </span>
      ) : (
        <div className="team-leg-search">
          <input
            type="search"
            placeholder={`Search ${house} swimmers`}
            enterKeyHint="search"
            autoComplete="off"
            autoCorrect="off"
            autoCapitalize="off"
            spellCheck={false}
            value={term}
            disabled={disabled}
            onChange={(e) => setTerm(e.target.value)}
          />
          {searchFailed && (
            <span className="row-sub" role="status">
              Can't reach the server — showing the saved roster.
            </span>
          )}
          {term.trim() !== "" && truncated && (
            <span className="row-sub">
              {/* Offline the filter runs over a fixed snapshot: typing cannot reach
                  anyone this device never cached, whatever metadata it carries. */}
              {searchFailed
                ? "The saved roster may be incomplete — type to narrow."
                : `More matches than fit — showing the first ${limit}. Keep typing to narrow.`}
            </span>
          )}
          {term.trim() !== "" && available.length === 0 && (
            <span className="row-sub">
              No eligible {house} swimmers match{searchFailed ? " in the saved roster." : "."}
            </span>
          )}
          {term.trim() !== "" &&
            available.slice(0, 6).map((s) => (
              <button key={s.studentId} type="button" onClick={() => onPick(s)}>
                {s.displayName} · Yr {s.yearLevel}
              </button>
            ))}
        </div>
      )}
    </div>
  );
}
