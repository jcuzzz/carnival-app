import { useState, type FormEvent } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  createEventDefinition,
  generateDivisions,
  getCarnival,
  getCarnivalSettings,
  listEventDefinitions,
} from "../api/resources";
import type { Carnival, DivisionProfile, EventDefinition } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { ensureProfile, rosterDefaultToSave } from "../features/events/addDivisions";
import { codesSummary } from "../features/events/ageCodes";
import { knownSports } from "../features/events/catalogueRows";
import { DefinitionFields, useDefinitionFields } from "../features/events/definitionFields";
import { DivisionGrid } from "../features/events/DivisionGrid";
import { counted } from "../features/events/counted";
import { carnivalSport, categoryLabel } from "../features/events/eventLabels";
import { allCellKeys, buildDivisionGrid, tickedSpecs } from "../features/events/divisionGridModel";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href, navigate } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

// Direct write, not the outbox.
export default function NewEventScreen({ carnivalId }: { carnivalId: string | null }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const connectivity = useConnectivity();
  const offline = connectivity !== "connected";

  return carnivalId === null ? (
    <NewDefinition canManage={canManage} offline={offline} />
  ) : (
    <NewCarnivalEvent carnivalId={carnivalId} canManage={canManage} offline={offline} />
  );
}

function NewCarnivalEvent({
  carnivalId,
  canManage,
  offline,
}: {
  carnivalId: string;
  canManage: boolean;
  offline: boolean;
}) {
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );

  return (
    <>
      <p>
        <a href={href(`/carnivals/${carnivalId}/setup/events`)}>← Event setup</a>
      </p>
      <h1>Add an event</h1>
      {carnival.data && (
        <p className="row-sub">
          {carnival.data.name} · {carnival.data.status} · {carnival.data.date}
        </p>
      )}
      {carnival.error && (
        <div className="alert error" role="alert">
          {errorText(
            carnival.error,
            "Can’t reach the server, so this carnival couldn’t be read.",
          )}{" "}
          Until it can be, this screen cannot tell whether the carnival still takes new divisions
          — the server decides, and it may refuse.
        </div>
      )}
      {settings.error && (
        <div className="alert error" role="alert">
          {errorText(
            settings.error,
            "Can’t reach the server, so this carnival’s division profile couldn’t be read.",
          )}
        </div>
      )}
      {carnival.loading || settings.loading ? (
        <p>Loading…</p>
      ) : (
        <NewEventForm
          key={carnivalId}
          carnivalId={carnivalId}
          carnival={carnival.data}
          profile={settings.data?.divisions}
          rosterDefault={settings.data?.divisionsDefault}
          profileKnown={settings.data !== undefined}
          canManage={canManage}
          offline={offline}
        />
      )}
    </>
  );
}

function NewEventForm({
  carnivalId,
  carnival,
  profile,
  rosterDefault,
  profileKnown,
  canManage,
  offline,
}: {
  carnivalId: string;
  carnival: Carnival | undefined;
  profile: DivisionProfile | null | undefined;
  /** In use until the profile is saved. */
  rosterDefault: DivisionProfile | null | undefined;
  /** The settings read landed, so an unconfigured profile is a fact. */
  profileKnown: boolean;
  canManage: boolean;
  offline: boolean;
}) {
  const invalidate = useInvalidate();
  const defaultProfile = rosterDefaultToSave(profile, rosterDefault);
  const effective = profile ?? rosterDefault;
  const model = buildDivisionGrid(effective, []);
  const cells = allCellKeys(model);
  const fields = useDefinitionFields();
  const [sport, setSport] = useState(() => carnivalSport(carnival?.type));
  // The unticking is remembered, so a late or refetched profile grows the grid ticked and never
  // revives a box that was deliberately cleared.
  const [unticked, setUnticked] = useState<ReadonlySet<string>>(() => new Set());
  const ticked = new Set([...cells].filter((key) => !unticked.has(key)));
  const setTicked = (next: ReadonlySet<string>): void =>
    setUnticked(new Set([...unticked, ...cells].filter((key) => !next.has(key))));
  const [created, setCreated] = useState<EventDefinition | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [profileError, setProfileError] = useState<string | null>(null);
  const [profileRaced, setProfileRaced] = useState(false);
  const [saving, setSaving] = useState(false);

  const locked = carnival?.status === "Closed" || carnival?.status === "Finalised";
  const writable = canManage && !locked && !offline;
  // Configured: a saved profile, or a roster to derive one from.
  const blocked = !model.configured;
  // Once the definition exists these fields are saved, and this screen has no
  // update call: editing them here would show an edit that never happened.
  const fieldsWritable = writable && created === null;
  const incomplete = fields.incompleteWithoutSport || sport.trim() === "";

  async function submit(e: FormEvent): Promise<void> {
    e.preventDefault();
    setError(null);
    setProfileError(null);
    setProfileRaced(false);
    setSaving(true);
    const divisions = tickedSpecs(model, ticked);
    try {
      const ensured = await ensureProfile(
        carnivalId,
        created === null ? defaultProfile : null,
        () => invalidate(keys.carnivalSettings(carnivalId)),
        "created",
      );
      if (ensured.kind === "raced") {
        setProfileRaced(true);
        return;
      }
      if (ensured.kind === "failed") {
        setProfileError(ensured.text);
        return;
      }
      const definition =
        created ??
        (await createEventDefinition({
          code: fields.code.trim(),
          name: fields.name.trim(),
          sport: sport.trim(),
          measureType: fields.measureType,
          units: fields.units.trim(),
          ...(fields.attemptCount === null ? {} : { attemptCount: fields.attemptCount }),
          ...(fields.ladder === null ? {} : { ladder: fields.ladder }),
          ...(fields.attemptsPerHeight === null
            ? {}
            : { attemptsPerHeight: fields.attemptsPerHeight }),
          tagsJson: JSON.stringify(fields.tags),
        }));
      setCreated(definition);
      invalidate(keys.eventDefinitions(true));
      if (divisions.length > 0) {
        const generated = await generateDivisions(carnivalId, {
          eventDefinitionId: definition.id,
          divisions,
        });
        invalidate(keys.carnivalEvents(carnivalId));
        showToast(`${definition.name} — ${counted(generated.created, "division")} scheduled.`);
      } else {
        showToast(`${definition.name} added.`);
      }
      navigate(`/carnivals/${carnivalId}/setup/events/${definition.id}`);
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
          Can’t reach the server, so nothing can be added for now — setup keeps no offline copy,
          and a code clash is a server answer. Reconnect and the fields come back.
        </div>
      )}
      {locked && (
        <div className="alert notice" role="status">
          This carnival is {carnival?.status}, so no divisions can be added to it.
        </div>
      )}
      {blocked && profileKnown && !locked && (
        <div className="alert notice" role="status">
          <p className="flush-top">Import a roster or add ages first.</p>
          <div className="button-row">
            <a
              className="button primary-link"
              href={href(`/carnivals/${carnivalId}/setup/divisions`)}
            >
              Divisions setup
            </a>
            <a className="button" href={href(`/carnivals/${carnivalId}/manage`)}>
              Apply a template on Manage
            </a>
          </div>
        </div>
      )}
      {created !== null && error !== null && (
        <div className="alert notice" role="alert">
          <p className="flush-top">
            {created.name} ({created.code}) was created, but its divisions were not: {error}
          </p>
          <p>
            The event exists, so pressing again adds only the divisions — nothing is created
            twice. You can also tick them on{" "}
            <a href={href(`/carnivals/${carnivalId}/setup/events/${created.id}`)}>
              the event itself
            </a>
            .
          </p>
        </div>
      )}
      {created === null && error !== null && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <form className="card" onSubmit={(e) => void submit(e)}>
        <DefinitionFields
          fields={fields}
          writable={fieldsWritable}
          sportField={
            <label className="field">
              <span>Sport</span>
              <input
                value={sport}
                onChange={(e) => setSport(e.target.value)}
                disabled={!fieldsWritable}
              />
            </label>
          }
        />

        <div className="form-divider">
          <h2 className="flush-top">Divisions</h2>
          {defaultProfile !== null && (
            <p className="row-sub">
              Ages from the roster: {rosterSummary(defaultProfile)}.{" "}
              <a href={href(`/carnivals/${carnivalId}/setup/divisions`)}>Change</a>
            </p>
          )}
          <DivisionGrid
            profile={effective}
            divisions={[]}
            writable={writable}
            profilePath={`/carnivals/${carnivalId}/setup/divisions`}
            mode={{ kind: "draft", ticked, onTickedChange: setTicked }}
          />
        </div>

        {profileError !== null && (
          <div className="alert error" role="alert">
            {profileError}
          </div>
        )}
        {profileRaced && (
          <div className="alert warn" role="status">
            The ages were set elsewhere since this screen loaded — the grid now shows them;
            tick again and press Create.
          </div>
        )}
        {canManage && (
          <div className="button-row">
            <button
              className="primary"
              type="submit"
              disabled={
                !writable ||
                saving ||
                (created === null && (incomplete || blocked || ticked.size === 0))
              }
            >
              {submitLabel(saving, created !== null, ticked.size, blocked)}
            </button>
          </div>
        )}
      </form>
    </>
  );
}

/** "12–18, SNR · Boys, Girls · Competitive" — what the default contests, in one line. */
function rosterSummary(profile: DivisionProfile): string {
  return [
    codesSummary(profile.ageGroups ?? []),
    (profile.categories ?? []).map(categoryLabel).join(", "),
    (profile.tiers ?? []).join(", "),
  ]
    .filter((part) => part !== "")
    .join(" · ");
}

function submitLabel(
  saving: boolean,
  exists: boolean,
  ticked: number,
  blocked: boolean,
): string {
  if (saving) return exists ? "Adding divisions…" : "Creating…";
  if (exists && ticked === 0) return "Finish";
  if (blocked) return "Add divisions first";
  if (ticked === 0) return "Tick at least one division";
  return `${exists ? "Add" : "Create"} ${counted(ticked, "division")}`;
}

/** The sport `<select>`'s escape into a name the catalogue does not hold yet:
 *  NUL-prefixed, so no sport the server would accept can collide with it. */
const OTHER_SPORT = "\u0000other";

function NewDefinition({ canManage, offline }: { canManage: boolean; offline: boolean }) {
  const invalidate = useInvalidate();
  const definitions = useApiQuery(keys.eventDefinitions(true), () => listEventDefinitions(true), {
    enabled: canManage,
  });
  const fields = useDefinitionFields();
  const [sportChoice, setSportChoice] = useState("");
  const [typedSport, setTypedSport] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const sports = knownSports(definitions.data ?? []);
  // A typed sport that only differs in case is the same sport: send the
  // spelling the catalogue already holds, or the grouping splits in two.
  const typed = typedSport.trim();
  const sport =
    sportChoice === OTHER_SPORT
      ? (sports.find((known) => known.toLowerCase() === typed.toLowerCase()) ?? typed)
      : sportChoice;
  const writable = canManage && !offline;
  const incomplete = fields.incompleteWithoutSport || sport === "";
  const gate = useAccessGate(canManage);

  async function submit(e: FormEvent): Promise<void> {
    e.preventDefault();
    setError(null);
    setSaving(true);
    try {
      const definition = await createEventDefinition({
        code: fields.code.trim(),
        name: fields.name.trim(),
        sport,
        measureType: fields.measureType,
        units: fields.units.trim(),
        ...(fields.attemptCount === null ? {} : { attemptCount: fields.attemptCount }),
        ...(fields.ladder === null ? {} : { ladder: fields.ladder }),
        ...(fields.attemptsPerHeight === null
          ? {}
          : { attemptsPerHeight: fields.attemptsPerHeight }),
        tagsJson: JSON.stringify(fields.tags),
      });
      invalidate(keys.eventDefinitions(true));
      showToast(`${definition.name} added to the catalogue.`);
      navigate(`/school/events/${definition.id}`);
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setSaving(false);
    }
  }

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="New definition">
        <p>Sign in to add an event to the school's catalogue.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>New definition</h1>
        <div className="alert error" role="alert">
          Adding to the event catalogue needs the carnival administrator role.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  return (
    <>
      <p>
        <a href={href("/school/events")}>← Event catalogue</a>
      </p>
      <h1>New definition</h1>
      <p className="row-sub">
        An event the school holds, in no carnival yet. A carnival schedules divisions of it on its
        own event setup.
      </p>

      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so nothing can be added for now — the catalogue keeps no offline
          copy, and a code clash is a server answer. Reconnect and the fields come back.
        </div>
      )}
      {definitions.error && (
        <div className="alert error" role="alert">
          {errorText(
            definitions.error,
            "Can’t reach the server, so the sports already in the catalogue couldn’t be read.",
          )}{" "}
          Type the sport instead.
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <form className="card" onSubmit={(e) => void submit(e)}>
        <DefinitionFields
          fields={fields}
          writable={writable}
          sportField={
            <>
              <label className="field">
                <span>Sport</span>
                <select
                  value={sportChoice}
                  onChange={(e) => setSportChoice(e.target.value)}
                  disabled={!writable}
                >
                  <option value="">Choose a sport</option>
                  {sports.map((name) => (
                    <option key={name} value={name}>
                      {name}
                    </option>
                  ))}
                  <option value={OTHER_SPORT}>Another sport…</option>
                </select>
              </label>
              {sportChoice === OTHER_SPORT && (
                <label className="field">
                  <span>Name the sport</span>
                  <input
                    value={typedSport}
                    placeholder="Cross country"
                    onChange={(e) => setTypedSport(e.target.value)}
                    disabled={!writable}
                  />
                </label>
              )}
            </>
          }
        />

        <div className="button-row">
          <button className="primary" type="submit" disabled={!writable || saving || incomplete}>
            {saving ? "Creating…" : "Create definition"}
          </button>
        </div>
      </form>
    </>
  );
}
