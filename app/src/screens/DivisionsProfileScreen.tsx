import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { getCarnival, getCarnivalSettings, updateCarnivalSettings } from "../api/resources";
import type { Carnival, CompetitionCategory, DivisionProfile, EventTier } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import {
  codeRange,
  FALLBACK_AGE_CODES,
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
  const connectivity = useConnectivity();
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );

  return (
    <>
      <p>
        <a href={href(`/carnivals/${carnivalId}/setup/events`)}>← Event setup</a>
      </p>
      <h1>Divisions profile</h1>
      {carnival.data && (
        <p className="row-sub">
          {carnival.data.name} · {carnival.data.status} · {carnival.data.date}
        </p>
      )}
      <p className="row-sub">
        The ages, categories and tiers this carnival contests; editing it creates and removes no
        divisions. The ages are the roster's own — the plain age as at 31 December, so “13”.
      </p>
      {settings.error && (
        <div className="alert error" role="alert">
          {errorText(
            settings.error,
            "Can’t reach the server, so this carnival’s profile couldn’t be read.",
          )}
        </div>
      )}
      {/* No form under a failed read: an empty one beside the alert would overwrite
          the stored profile on Save. */}
      {settings.loading || carnival.loading ? (
        <p>Loading…</p>
      ) : settings.error ? null : (
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
  const pickable = roster.length > 0 ? roster : FALLBACK_AGE_CODES;
  const range = numericCodes(pickable);

  const saved = orderCodes(profile?.ageGroups ?? []);
  const seedNumerics = numericCodes(seed?.ageGroups ?? []);
  // A saved age the selects cannot offer falls back to the roster's own end of
  // the ladder; the "no longer derives" note below is what names it.
  const offeredOr = (code: string | undefined, fallback: string | undefined): string =>
    code !== undefined && range.includes(code) ? code : (fallback ?? "");
  const [from, setFrom] = useState(() => offeredOr(seedNumerics[0], range[0]));
  const [to, setTo] = useState(() => offeredOr(seedNumerics.at(-1), range.at(-1)));

  // Non-numeric codes no range can reach (SNR): the roster's, plus any a saved
  // profile still names, so one that has left the roster can be unticked.
  const others = orderCodes([...nonNumericCodes(pickable), ...nonNumericCodes(saved)]);
  const [ticked, setTicked] = useState<string[]>(() => nonNumericCodes(seed?.ageGroups ?? []));
  const [categories, setCategories] = useState<CompetitionCategory[]>(
    () => seed?.categories ?? ["M", "F"],
  );
  const [tiers, setTiers] = useState<EventTier[]>(() => seed?.tiers ?? ["Competitive"]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const selected = orderCodes([...codeRange(pickable, from, to), ...ticked]);
  const uncontested = roster.filter((code) => !selected.includes(code));
  const underived = roster.length > 0 ? saved.filter((code) => !roster.includes(code)) : [];
  // A saved set with a gap ({12, 13, 15}) is one the From/To pair cannot restate,
  // so the screen says what saving will do to it rather than silently widening it.
  const savedNumerics = numericCodes(saved);
  const gapped =
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
      showToast("Divisions profile saved.");
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
          Can’t reach the server, so the profile is read-only for now — setup keeps no offline
          copy. Reconnect and the fields come back.
        </div>
      )}
      {carnival?.status === "Finalised" && (
        <div className="alert notice" role="status">
          This carnival is finalised, so its settings can no longer be changed.
        </div>
      )}
      {profile == null && rosterDefault != null && (
        <p className="row-sub">
          Nothing is saved yet, so these ages are the roster's own. Save them as they are, or
          change them first.
        </p>
      )}
      {profile == null && rosterDefault == null && (
        <p className="row-sub">
          This carnival has no divisions profile yet. Pick the ages it contests below.
        </p>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <form className="card" onSubmit={(e) => void save(e)}>
        <h2 className="flush-top">Ages</h2>
        {roster.length === 0 && (
          <p className="row-sub">
            No roster is promoted yet, so there are no ages to pick from — the selects below offer
            5 to 21 instead, and an imported roster replaces them.
          </p>
        )}
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
          Every age from From to To is contested. Nothing reaches the server until Save profile.
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

        {uncontested.length > 0 && (
          <p className="row-sub">
            Roster ages not contested: {uncontested.join(", ")} — students with that age cannot be
            entered.
          </p>
        )}
        {underived.length > 0 && (
          <p className="row-sub">
            Saved ages the roster no longer derives: {underived.join(", ")} — the divisions
            already made from them still run.
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
              {saving ? "Saving…" : "Save profile"}
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
    return `${err.message} Untick any age the roster no longer derives and save again.`;
  return errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT);
}
