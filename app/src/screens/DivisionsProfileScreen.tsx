import { useState, type FormEvent } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  extendCarnivalAges,
  getCarnival,
  getCarnivalSettings,
  updateCarnivalSettings,
} from "../api/resources";
import type {
  Carnival,
  CompetitionCategory,
  DivisionProfile,
  EventTier,
} from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { AgesRequiredNotice } from "../features/carnivals/AgesRequiredNotice";
import {
  ageNumber,
  codesSummary,
  orderCodes,
  rangeCodes,
} from "../features/events/ageCodes";
import { categoryLabel } from "../features/events/eventLabels";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

const CATEGORIES: CompetitionCategory[] = ["M", "F", "S"];
const TIERS: EventTier[] = ["Competitive", "Social"];
/** The ages From and To offer; a seeded age outside them is offered as well. */
const YOUNGEST_OFFERED = 3;
const OLDEST_OFFERED = 25;

// Direct write, not the outbox.
export default function DivisionsProfileScreen({
  carnivalId,
}: {
  carnivalId: string;
}) {
  const session = useSession();
  const canManage = hasRole(
    session,
    "CarnivalAdministrator",
    "SystemAdministrator",
  );
  const canSetAges = hasRole(session, "SystemAdministrator");
  const connectivity = useConnectivity();
  const carnival = useApiQuery(keys.carnival(carnivalId), () =>
    getCarnival(carnivalId),
  );
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );
  const seed = settings.data?.divisions ?? settings.data?.divisionsDefault;

  return (
    <>
      <p>
        <a href={href(`/carnivals/${carnivalId}/setup/events`)}>
          ← Event setup
        </a>
      </p>
      <h1>Ages and categories</h1>
      {carnival.data && (
        <p className="row-sub">
          {carnival.data.name} · {carnival.data.status} · {carnival.data.date}
        </p>
      )}
      <p className="row-sub">
        This carnival’s ages start as the school’s; change them here if this
        carnival runs others. The oldest age takes every student that age or
        older. Categories and tiers apply to every event.
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
      ) : settings.error ? null : seed == null ? (
        <AgesRequiredNotice canSetAges={canSetAges} />
      ) : (
        <ProfileForm
          // The draft is seeded once; without this, walking from one carnival's
          // profile to another's would carry the first one's ages across.
          key={carnivalId}
          carnivalId={carnivalId}
          carnival={carnival.data}
          profile={settings.data?.divisions}
          seed={seed}
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
  seed,
  canManage,
  offline,
}: {
  carnivalId: string;
  carnival: Carnival | undefined;
  profile: DivisionProfile | null | undefined;
  /** The saved profile, else the school's ages. */
  seed: DivisionProfile;
  canManage: boolean;
  offline: boolean;
}) {
  const invalidate = useInvalidate();
  const seedAges = (seed.ageGroups ?? [])
    .map(ageNumber)
    .filter((age): age is number => age !== null);
  const [from, setFrom] = useState(() =>
    seedAges.length > 0 ? Math.min(...seedAges) : YOUNGEST_OFFERED,
  );
  const [to, setTo] = useState(() =>
    seedAges.length > 0 ? Math.max(...seedAges) : from,
  );
  const [categories, setCategories] = useState<CompetitionCategory[]>(
    () => seed.categories ?? ["M", "F"],
  );
  const [tiers, setTiers] = useState<EventTier[]>(
    () => seed.tiers ?? ["Competitive"],
  );
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const offered = ages(
    Math.min(YOUNGEST_OFFERED, ...seedAges),
    Math.max(OLDEST_OFFERED, ...seedAges),
  );
  const selected = rangeCodes(from, to);
  const lowered = seedAges.length > 0 && from < Math.min(...seedAges);
  // A saved set the range cannot restate (a gap, a plain oldest age, a senior code): say what
  // saving will do to it rather than silently replacing it.
  const saved = orderCodes(profile?.ageGroups ?? []);
  const restated =
    saved.length === 0 ||
    (saved.length === selected.length &&
      saved.every((code, i) => code === selected[i]));

  const writable = canManage && carnival?.status !== "Finalised" && !offline;

  function toggle<T>(
    values: T[],
    set: (next: T[]) => void,
    order: T[],
    value: T,
  ): void {
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
      // A lower From adds the new ages to the events as Add age does, so no student is left
      // without a division while the warning goes quiet.
      if (lowered) {
        await extendCarnivalAges(carnivalId, from);
      }
      // Only this screen's slice: an absent field leaves the stored value alone,
      // and the scoring rules and lane count are not ours to restate.
      await updateCarnivalSettings(carnivalId, {
        divisions: { ageGroups: selected, categories, tiers },
      });
      invalidate(
        keys.carnivalSettings(carnivalId),
        keys.carnivalEvents(carnivalId),
        keys.eventDefinitions(true),
      );
      showToast("Ages and categories saved.");
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setSaving(false);
    }
  }

  return (
    <>
      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so this screen is read-only for now — setup
          keeps no offline copy. Reconnect and the fields come back.
        </div>
      )}
      {carnival?.status === "Finalised" && (
        <div className="alert notice" role="status">
          This carnival is finalised, so its settings can no longer be changed.
        </div>
      )}
      {profile == null && (
        <p className="row-sub">
          Nothing is saved yet, so these are the school’s ages; they are saved
          when the first events are added.
        </p>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <form className="card" onSubmit={(e) => void save(e)}>
        <h2 className="flush-top">Ages</h2>
        {!restated && (
          <p className="row-sub">
            Saved ages: {codesSummary(saved)}. Saving replaces them with{" "}
            {codesSummary(selected, " to ")}.
          </p>
        )}
        <div className="form-grid">
          <label className="field">
            <span>From</span>
            <select
              value={from}
              onChange={(e) => setFrom(Number(e.target.value))}
              disabled={!writable}
            >
              {offered
                .filter((age) => age <= to)
                .map((age) => (
                  <option key={age} value={age}>
                    {age}
                  </option>
                ))}
            </select>
          </label>
          <label className="field">
            <span>To (and over)</span>
            <select
              value={to}
              onChange={(e) => setTo(Number(e.target.value))}
              disabled={!writable}
            >
              {offered
                .filter((age) => age >= from)
                .map((age) => (
                  <option key={age} value={age}>
                    {age}+
                  </option>
                ))}
            </select>
          </label>
        </div>
        <p className="row-sub">
          {codesSummary(selected, " to ")}: one age per division, and {to}+
          takes every student aged {to} or older. Nothing reaches the server
          until Save.
          {lowered &&
            " Saving also adds the younger ages to every event that runs the youngest age now."}
        </p>

        <div className="form-divider">
          <h2 className="flush-top">Categories</h2>
          <div className="button-row">
            {CATEGORIES.map((category) => (
              <label key={category} className="check-row">
                <input
                  type="checkbox"
                  checked={categories.includes(category)}
                  disabled={!writable}
                  onChange={() =>
                    toggle(categories, setCategories, CATEGORIES, category)
                  }
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
            <button
              className="primary"
              type="submit"
              disabled={!writable || saving}
            >
              {saving ? "Saving…" : "Save"}
            </button>
          </div>
        )}
      </form>
    </>
  );
}

function ages(from: number, to: number): number[] {
  return Array.from({ length: to - from + 1 }, (_, i) => from + i);
}
