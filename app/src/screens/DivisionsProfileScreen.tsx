import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { getCarnival, getCarnivalSettings, updateCarnivalSettings } from "../api/resources";
import type { Carnival, CompetitionCategory, DivisionProfile, EventTier } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { RosterRequiredNotice } from "../features/carnivals/RosterRequiredNotice";
import {
  codeRange,
  codesSummary,
  nonNumericCodes,
  numericCodes,
  orderCodes,
} from "../features/events/ageCodes";
import { categoryLabel } from "../features/events/eventLabels";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

const CATEGORIES: CompetitionCategory[] = ["M", "F", "S"];
const TIERS: EventTier[] = ["Competitive", "Social"];

// Direct write, not the outbox.
export default function DivisionsProfileScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const canImport = hasRole(session, "SystemAdministrator");
  const connectivity = useConnectivity();
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );
  // No fallback range (ADR-0067 §5): with no roster and no saved ages there is nothing to edit.
  const noAges =
    orderCodes(settings.data?.rosterAgeGroups ?? []).length === 0 &&
    orderCodes(settings.data?.divisions?.ageGroups ?? []).length === 0;

  return (
    <>
      <p>
        <a href={href(`/carnivals/${carnivalId}/setup/events`)}>← Event setup</a>
      </p>
      <h1>Ages and categories</h1>
      {carnival.data && (
        <p className="row-sub">
          {carnival.data.name} · {carnival.data.status} · {carnival.data.date}
        </p>
      )}
      <p className="row-sub">
        Ages come from the student list; narrow them here if this carnival runs fewer. Categories
        and tiers apply to every event.
      </p>
      {settings.error && (
        <div className="alert error" role="alert">
          {errorText(
            settings.error,
            "Can’t reach the server, so this carnival’s ages couldn’t be read.",
          )}
        </div>
      )}
      {/* No form under a failed read: an empty one beside the alert would overwrite
          the stored profile on Save. */}
      {settings.loading || carnival.loading ? (
        <p>Loading…</p>
      ) : settings.error ? null : noAges ? (
        <RosterRequiredNotice carnivalId={carnivalId} canImport={canImport} />
      ) : (
        <ProfileForm
          // The draft is seeded once; without this, walking from one carnival's
          // profile to another's would carry the first one's ages across.
          key={carnivalId}
          carnivalId={carnivalId}
          carnival={carnival.data}
          profile={settings.data?.divisions}
          rosterDefault={settings.data?.divisionsDefault}
          rosterCodes={settings.data?.rosterAgeGroups ?? []}
          canManage={canManage}
          offline={connectivity !== "connected"}
        />
      )}
    </>
  );
}

function ProfileForm({
  carnivalId,
  carnival,
  profile,
  rosterDefault,
  rosterCodes,
  canManage,
  offline,
}: {
  carnivalId: string;
  carnival: Carnival | undefined;
  profile: DivisionProfile | null | undefined;
  /** Pre-fills when nothing is saved. */
  rosterDefault: DivisionProfile | null | undefined;
  rosterCodes: readonly string[];
  canManage: boolean;
  offline: boolean;
}) {
  const invalidate = useInvalidate();
  const seed = profile ?? rosterDefault;
  const roster = orderCodes(rosterCodes);
  const saved = orderCodes(profile?.ageGroups ?? []);
  // With no roster there is nothing to narrow the saved ages against, so they stay as saved.
  const agesFixed = roster.length === 0;
  // Narrowing only: the roster's ages, plus saved ones it no longer has, so they can be left out.
  const pickable = orderCodes([...roster, ...saved]);
  const range = numericCodes(pickable);
  const seedNumerics = numericCodes(seed?.ageGroups ?? []).filter((code) => range.includes(code));
  const [from, setFrom] = useState(() => seedNumerics[0] ?? range[0] ?? "");
  const [to, setTo] = useState(() => seedNumerics.at(-1) ?? range.at(-1) ?? "");

  // Non-numeric codes no range can reach (SNR).
  const others = nonNumericCodes(pickable);
  const [ticked, setTicked] = useState<string[]>(() => nonNumericCodes(seed?.ageGroups ?? []));
  const [categories, setCategories] = useState<CompetitionCategory[]>(
    () => seed?.categories ?? ["M", "F"],
  );
  const [tiers, setTiers] = useState<EventTier[]>(() => seed?.tiers ?? ["Competitive"]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const selected = agesFixed ? saved : orderCodes([...codeRange(pickable, from, to), ...ticked]);
  const uncontested = roster.filter((code) => !selected.includes(code));
  const underived = agesFixed ? [] : saved.filter((code) => !roster.includes(code));
  // A saved set with a gap ({12, 13, 15}) is one the From/To pair cannot restate,
  // so the screen says what saving will do to it rather than silently widening it.
  const savedNumerics = numericCodes(saved);
  const gapped =
    !agesFixed &&
    savedNumerics.length > 0 &&
    savedNumerics.length !== Number(savedNumerics.at(-1)) - Number(savedNumerics[0]) + 1;

  const writable = canManage && carnival?.status !== "Finalised" && !offline;

  function toggle<T>(values: T[], set: (next: T[]) => void, order: T[], value: T): void {
    const next = new Set(values);
    if (next.has(value)) next.delete(value);
    else next.add(value);
    set(order.filter((v) => next.has(v)));
  }

  async function save(e: FormEvent): Promise<void> {
    e.preventDefault();
    setError(null);
    setSaving(true);
    try {
      // Only this screen's slice: an absent field leaves the stored value alone,
      // and the scoring rules and lane count are not ours to restate.
      await updateCarnivalSettings(carnivalId, {
        divisions: { ageGroups: selected, categories, tiers },
      });
      invalidate(keys.carnivalSettings(carnivalId));
      showToast("Ages and categories saved.");
    } catch (err) {
      setError(saveErrorText(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so this screen is read-only for now — setup keeps no offline copy.
          Reconnect and the fields come back.
        </div>
      )}
      {carnival?.status === "Finalised" && (
        <div className="alert notice" role="status">
          This carnival is finalised, so its settings can no longer be changed.
        </div>
      )}
      {profile == null && (
        <p className="row-sub">
          Nothing is saved yet, so these are the student list’s ages; they are saved when the first
          events are added.
        </p>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <form className="card" onSubmit={(e) => void save(e)}>
        <h2 className="flush-top">Ages</h2>
        {agesFixed ? (
          <>
            <p>{codesSummary(saved)}</p>
            <p className="row-sub">
              No student list is imported yet, so the saved ages stay as they are; categories and
              tiers can still change.
            </p>
          </>
        ) : (
          <>
            {gapped && (
              <p className="row-sub">
                Saved ages: {savedNumerics.join(", ")} — saving replaces them with the range below.
              </p>
            )}
            <div className="form-grid">
              <label className="field">
                <span>From</span>
                <select
                  value={from}
                  onChange={(e) => setFrom(e.target.value)}
                  disabled={!writable || range.length === 0}
                >
                  {range
                    .filter((code) => to === "" || Number(code) <= Number(to))
                    .map((code) => (
                      <option key={code} value={code}>
                        {code}
                      </option>
                    ))}
                </select>
              </label>
              <label className="field">
                <span>To</span>
                <select
                  value={to}
                  onChange={(e) => setTo(e.target.value)}
                  disabled={!writable || range.length === 0}
                >
                  {range
                    .filter((code) => from === "" || Number(code) >= Number(from))
                    .map((code) => (
                      <option key={code} value={code}>
                        {code}
                      </option>
                    ))}
                </select>
              </label>
            </div>
            <p className="row-sub">
              Every age from From to To is contested. Nothing reaches the server until Save.
            </p>

            {others.length > 0 && (
              <div className="form-divider">
                <h2 className="flush-top">Other ages</h2>
                <div className="button-row">
                  {others.map((code) => (
                    <label key={code} className="check-row">
                      <input
                        type="checkbox"
                        checked={ticked.includes(code)}
                        disabled={!writable}
                        onChange={() => toggle(ticked, setTicked, others, code)}
                      />
                      {code}
                    </label>
                  ))}
                </div>
                <p className="row-sub">An age no range can reach, such as a senior code.</p>
              </div>
            )}
          </>
        )}

        {uncontested.length > 0 && (
          <p className="row-sub">
            Ages on the student list left out: {uncontested.join(", ")} — students of that age
            cannot be entered.
          </p>
        )}
        {underived.length > 0 && (
          <p className="row-sub">
            Saved ages no current student has: {underived.join(", ")} — the divisions already made
            from them still run.
          </p>
        )}

        <div className="form-divider">
          <h2 className="flush-top">Categories</h2>
          <div className="button-row">
            {CATEGORIES.map((category) => (
              <label key={category} className="check-row">
                <input
                  type="checkbox"
                  checked={categories.includes(category)}
                  disabled={!writable}
                  onChange={() => toggle(categories, setCategories, CATEGORIES, category)}
                />
                {categoryLabel(category)}
              </label>
            ))}
          </div>
        </div>

        <div className="form-divider">
          <h2 className="flush-top">Tiers</h2>
          <div className="button-row">
            {TIERS.map((tier) => (
              <label key={tier} className="check-row">
                <input
                  type="checkbox"
                  checked={tiers.includes(tier)}
                  disabled={!writable}
                  onChange={() => toggle(tiers, setTiers, TIERS, tier)}
                />
                {tier}
              </label>
            ))}
          </div>
        </div>

        {canManage && (
          <div className="button-row">
            <button className="primary" type="submit" disabled={!writable || saving}>
              {saving ? "Saving…" : "Save"}
            </button>
          </div>
        )}
      </form>
    </>
  );
}

// The server's sentence names the offending code; what it cannot know is that
// nothing here is typed, so the only code it can be refusing is a saved one.
function saveErrorText(err: unknown): string {
  if (err instanceof ApiError && err.code === "AGE_SCOPE_INVALID")
    return `${err.message} Leave out any age no current student has and save again.`;
  return errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT);
}
