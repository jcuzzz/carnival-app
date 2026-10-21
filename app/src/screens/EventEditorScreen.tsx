import { useEffect, useState, type FormEvent } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  deactivateEventDefinition,
  getCarnival,
  getCarnivalSettings,
  listCarnivalEvents,
  listEventDefinitions,
  updateEventDefinition,
} from "../api/resources";
import type { EventDefinition, MeasureType } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import {
  EventTypeFields,
  EventTypeSelect,
  nextFieldsForMeasure,
  ownsAttempts,
  readDefinitionFields,
  type MeasureFields,
} from "../features/events/definitionFields";
import { DivisionGrid } from "../features/events/DivisionGrid";
import { TagEditor } from "../features/events/TagEditor";
import { definitionTags, usageSummary } from "../features/events/setupRows";
import { measureRaw } from "../results/measureFormat";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

// Direct write, not the outbox.
export default function EventEditorScreen({
  carnivalId,
  definitionId,
}: {
  /** null = the catalogue's own editor: the definition, no carnival. */
  carnivalId: string | null;
  definitionId: string;
}) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const connectivity = useConnectivity();
  // Inactive included: a definition retired on this screen must stay readable
  // where it was retired, not 404 under whoever pressed the button.
  const definitions = useApiQuery(keys.eventDefinitions(true), () => listEventDefinitions(true));
  const definition = definitions.data?.find((d) => d.id === definitionId);

  if (definitions.loading) return <p>Loading…</p>;
  if (!definition) return <NoEditor carnivalId={carnivalId} error={definitions.error} />;

  return (
    <EventEditor
      key={definition.id}
      carnivalId={carnivalId}
      definition={definition}
      canManage={canManage}
      offline={connectivity !== "connected"}
    />
  );
}

/** A catalogue that would not load says nothing about whether the event exists. */
function NoEditor({ carnivalId, error }: { carnivalId: string | null; error: Error | undefined }) {
  return (
    <>
      <BackLink carnivalId={carnivalId} />
      <h1>{error ? "Couldn’t load this event" : "Event not found"}</h1>
      {error ? (
        <>
          <div className="alert error" role="alert">
            {errorText(error, "Can’t reach the server, so the event catalogue couldn’t be read.")}
          </div>
          <p>
            The event is most likely still there — it is the catalogue read that failed, not the
            event that went missing. Try again once you are back on the school network.
          </p>
        </>
      ) : (
        <p>No event in the catalogue has that id.</p>
      )}
    </>
  );
}

type EditedFields = Pick<
  EventDefinition,
  | "name"
  | "code"
  | "measureType"
  | "units"
  | "attemptCount"
  | "ladder"
  | "attemptsPerHeight"
  | "tagsJson"
>;

// `tagsJson` is normalised through the parse the editor renders, so a blob that
// differs only in spelling does not read as somebody else’s edit.
function editedFields(d: EditedFields): EditedFields {
  return {
    name: d.name,
    code: d.code,
    measureType: d.measureType,
    units: d.units,
    attemptCount: d.attemptCount,
    ladder: d.ladder,
    attemptsPerHeight: d.attemptsPerHeight,
    tagsJson: JSON.stringify(definitionTags(d.tagsJson)),
  };
}

function ladderText(ladder: EventDefinition["ladder"], end: "start" | "step"): string {
  return ladder === null ? "" : measureRaw("Height", ladder[end]);
}

function storedMeasureFields(d: EditedFields): MeasureFields {
  return {
    attempts: d.attemptCount == null ? "" : String(d.attemptCount),
    barStart: ladderText(d.ladder, "start"),
    barStep: ladderText(d.ladder, "step"),
    perHeight: d.attemptsPerHeight == null ? "" : String(d.attemptsPerHeight),
  };
}

// One comparable string, so "unchanged" is not object identity.
function ladderKey(ladder: EventDefinition["ladder"]): string {
  return ladder === null ? "" : `${ladder.start}/${ladder.step}`;
}

function sameFields(a: EditedFields, b: EditedFields): boolean {
  return (
    a.name === b.name &&
    a.code === b.code &&
    a.measureType === b.measureType &&
    a.units === b.units &&
    a.attemptCount === b.attemptCount &&
    ladderKey(a.ladder) === ladderKey(b.ladder) &&
    a.attemptsPerHeight === b.attemptsPerHeight &&
    a.tagsJson === b.tagsJson
  );
}

function BackLink({ carnivalId }: { carnivalId: string | null }) {
  return carnivalId === null ? (
    <p>
      <a href={href("/school/events")}>← Event catalogue</a>
    </p>
  ) : (
    <p>
      <a href={href(`/carnivals/${carnivalId}/setup/events`)}>← Event setup</a>
    </p>
  );
}

function EventEditor({
  carnivalId,
  definition,
  canManage,
  offline,
}: {
  carnivalId: string | null;
  definition: EventDefinition;
  canManage: boolean;
  offline: boolean;
}) {
  const invalidate = useInvalidate();
  // The fields seed from the server once: a later server-side change is taken up while nothing is
  // typed, and is offered rather than applied once something is.
  const [seed, setSeed] = useState<EditedFields>(() => editedFields(definition));
  const [name, setName] = useState(definition.name);
  const [code, setCode] = useState(definition.code);
  const [measureType, setMeasureType] = useState<MeasureType>(definition.measureType);
  const [units, setUnits] = useState(definition.units);
  // Blank = a single typed mark. The server refuses a change once the definition has results.
  const [attempts, setAttempts] = useState(
    definition.attemptCount == null ? "" : String(definition.attemptCount),
  );
  // Deck suggestions only: blank means none, and neither changes how the event is captured.
  const [barStart, setBarStart] = useState(ladderText(definition.ladder, "start"));
  const [barStep, setBarStep] = useState(ladderText(definition.ladder, "step"));
  const [perHeight, setPerHeight] = useState(storedMeasureFields(definition).perHeight);
  const [tags, setTags] = useState<string[]>(() => definitionTags(definition.tagsJson));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [confirmingRetire, setConfirmingRetire] = useState(false);

  const writable = canManage && definition.isActive && !offline;
  const frozen = definition.usage.results > 0;
  const {
    attemptCount: typedCount,
    ladder,
    attemptsPerHeight,
    incompleteWithoutSport: incomplete,
  } = readDefinitionFields({
    name,
    code,
    measureType,
    units,
    attempts,
    barStart,
    barStep,
    perHeight,
  });
  // A count stored on another type is re-sent, not cleared: the server freezes it once results
  // exist.
  const attemptCount =
    ownsAttempts(measureType)
      ? typedCount
      : measureType === definition.measureType
        ? definition.attemptCount
        : null;

  const stored = editedFields(definition);
  const typed = editedFields({
    name,
    code,
    measureType,
    units,
    attemptCount,
    ladder,
    attemptsPerHeight,
    tagsJson: JSON.stringify(tags),
  });
  const moved = !sameFields(stored, seed);
  const conflict = moved && !sameFields(typed, seed) && !sameFields(typed, stored);

  function changeMeasure(next: MeasureType): void {
    // Back to the stored measure is back to the stored fields: the helper's
    // shipped default would otherwise rewrite a ladder the event already has.
    const fields =
      next === stored.measureType
        ? storedMeasureFields(stored)
        : nextFieldsForMeasure({ attempts, barStart, barStep, perHeight }, next);
    setAttempts(fields.attempts);
    setBarStart(fields.barStart);
    setBarStep(fields.barStep);
    setPerHeight(fields.perHeight);
    setMeasureType(next);
  }

  function showStored(): void {
    setSeed(stored);
    setName(stored.name);
    setCode(stored.code);
    setMeasureType(stored.measureType);
    setUnits(stored.units);
    const fields = storedMeasureFields(stored);
    setAttempts(fields.attempts);
    setBarStart(fields.barStart);
    setBarStep(fields.barStep);
    setPerHeight(fields.perHeight);
    setTags(definitionTags(stored.tagsJson));
  }

  useEffect(() => {
    if (moved && !conflict) showStored();
  });

  async function run(action: () => Promise<unknown>, done: string): Promise<boolean> {
    setError(null);
    setSaving(true);
    try {
      await action();
      // A catalogue edit reaches every carnival that scheduled the event, and
      // this screen knows none of them: the prefix covers all of their reads.
      if (carnivalId === null) invalidate(keys.eventDefinitions(true), keys.carnivals());
      else invalidate(keys.eventDefinitions(true), keys.carnivalEvents(carnivalId));
      showToast(done);
      return true;
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
      return false;
    } finally {
      setSaving(false);
    }
  }

  // The blobs are omitted, not nulled: absent means "leave unchanged", and this editor cannot show
  // what it would overwrite. Tags are sent only once edited.
  function save(e: FormEvent) {
    e.preventDefault();
    void run(
      () =>
        updateEventDefinition(definition.id, {
          code: code.trim(),
          name: name.trim(),
          sport: definition.sport,
          measureType,
          units: units.trim(),
          attemptCount,
          // Always sent, like `attemptCount`: an omitted ladder CLEARS it, and
          // that is how a Height event goes back to suggesting no bar at all.
          ladder,
          ...(attemptsPerHeight === null ? {} : { attemptsPerHeight }),
          ...(typed.tagsJson === stored.tagsJson ? {} : { tagsJson: typed.tagsJson }),
        }),
      "Event saved.",
    );
  }

  async function retire(): Promise<void> {
    const retired = await run(() => deactivateEventDefinition(definition.id), "Event retired.");
    if (retired) setConfirmingRetire(false);
  }

  return (
    <>
      <BackLink carnivalId={carnivalId} />
      <h1>
        {definition.name}{" "}
        {!definition.isActive && <span className="badge muted">Retired</span>}
      </h1>
      <p className="row-sub">
        {definition.sport} · {definition.code}
      </p>

      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so this event is read-only for now — setup keeps no offline
          copy, and a code clash is a server answer. Reconnect and the fields come back.
        </div>
      )}
      {!definition.isActive && (
        <div className="alert notice" role="status">
          This event is retired, so it can no longer be edited or added to a carnival. Every
          division, entry, result and record that used it is untouched.
        </div>
      )}
      {conflict && (
        <div className="alert notice" role="alert">
          <p className="flush-top">
            Someone else changed this event while you had it open — it now reads “{stored.name}”
            ({stored.code}). Nothing you have typed was touched, and saving replaces their change
            with yours.
          </p>
          <div className="button-row">
            <button type="button" onClick={showStored}>
              Discard mine and show theirs
            </button>
          </div>
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      <form className="card" onSubmit={save}>
        <p className="row-sub flush-top">{usageSummary(definition.usage)}</p>
        <label className="field wide">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} disabled={!writable} />
        </label>
        <p className="row-sub">
          Renaming this event renames every division of it, in every carnival — this year's and
          every past one, including programs already printed and the record book.
        </p>
        <div className="form-grid">
          <label className="field">
            <span>Code</span>
            <input value={code} onChange={(e) => setCode(e.target.value)} disabled={!writable} />
          </label>
          <EventTypeSelect
            measureType={measureType}
            changeMeasure={changeMeasure}
            writable={writable}
            frozen={frozen}
          />
          <label className="field">
            <span>Units</span>
            <input
              value={units}
              onChange={(e) => setUnits(e.target.value)}
              disabled={!writable || frozen}
            />
          </label>
          {frozen && (
            <p className="row-sub field-note">Frozen: results are recorded in these units.</p>
          )}
          <EventTypeFields
            measureType={measureType}
            attempts={attempts}
            setAttempts={setAttempts}
            barStart={barStart}
            setBarStart={setBarStart}
            barStep={barStep}
            setBarStep={setBarStep}
            perHeight={perHeight}
            setPerHeight={setPerHeight}
            writable={writable}
            frozen={frozen}
            legacyAttemptCount={
              measureType === "Height" && definition.measureType === "Height"
                ? definition.attemptCount
                : null
            }
          />
        </div>
        <TagEditor tags={tags} onChange={setTags} writable={writable} />
        {canManage && (
          <div className="button-row">
            <button className="primary" type="submit" disabled={!writable || saving || incomplete}>
              {saving ? "Saving…" : "Save event"}
            </button>
            {definition.isActive && !confirmingRetire && (
              <button
                type="button"
                className="danger"
                disabled={!writable || saving}
                onClick={() => setConfirmingRetire(true)}
              >
                Retire event…
              </button>
            )}
          </div>
        )}
      </form>

      {carnivalId !== null && (
        <DivisionsSection
          carnivalId={carnivalId}
          definitionId={definition.id}
          writable={writable}
        />
      )}

      {canManage && confirmingRetire && (
        <div className="card">
          <p className="row-sub flush-top">
            Retiring deactivates this event: it can no longer be added to a carnival, and nothing is
            deleted — its divisions, entries, results and school records all stand. It is how an
            event is withdrawn or its code freed up, and there is no un-retire on this screen.
          </p>
          <div className="button-row">
            <button
              type="button"
              className="danger"
              disabled={!writable || saving}
              onClick={() => void retire()}
            >
              {saving ? "Retiring…" : "Confirm retire"}
            </button>
            <button type="button" disabled={saving} onClick={() => setConfirmingRetire(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </>
  );
}

// Writes behind `RequireEditable`: a Closed carnival keeps the editor but loses the grid.
function DivisionsSection({
  carnivalId,
  definitionId,
  writable,
}: {
  carnivalId: string;
  definitionId: string;
  writable: boolean;
}) {
  const invalidate = useInvalidate();
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );
  const events = useApiQuery(keys.carnivalEvents(carnivalId), () => listCarnivalEvents(carnivalId));
  const loadError = carnival.error ?? settings.error ?? events.error;
  const locked = carnival.data?.status === "Closed" || carnival.data?.status === "Finalised";

  return (
    <section className="card subsection">
      <h2 className="flush-top">Divisions</h2>
      {loadError && (
        <div className="alert error" role="alert">
          {errorText(
            loadError,
            "Can’t reach the server, so this carnival’s divisions couldn’t be read.",
          )}
        </div>
      )}
      {locked && (
        <div className="alert notice" role="status">
          This carnival is {carnival.data?.status}, so its divisions can no longer be added to or
          removed. The event itself is still editable — it belongs to the catalogue, not to this
          carnival.
        </div>
      )}
      {carnival.loading || settings.loading || events.loading ? (
        <p>Loading this carnival’s divisions…</p>
      ) : (
        <DivisionGrid
          profile={settings.data?.divisions}
          divisions={(events.data ?? []).filter((e) => e.eventDefinitionId === definitionId)}
          writable={writable && !locked}
          profilePath={`/carnivals/${carnivalId}/setup/divisions`}
          mode={{
            kind: "live",
            carnivalId,
            eventDefinitionId: definitionId,
            onChanged: () =>
              invalidate(keys.carnivalEvents(carnivalId), keys.eventDefinitions(true)),
          }}
        />
      )}
    </section>
  );
}
