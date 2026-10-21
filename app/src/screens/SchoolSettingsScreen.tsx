import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type FormEvent,
} from "react";
import { ApiError } from "../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { getSchoolSettings, putSchoolSettings } from "../api/resources";
import type { HouseSettingRequest, SchoolSettings } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import {
  BRAND_INK_LIGHT,
  MIN_BRAND_CONTRAST,
  contrastRatio,
  getBrandingState,
  inkFor,
  normaliseBrandColour,
  refreshBranding,
} from "../branding/branding";
import { refreshHouseMarks } from "../branding/houseMarks";
import { ColourPickerField } from "../features/ui/ColourPickerField";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";
import { color } from "../tokens.json";

// One form, one full replacement; every hint here previews a rule the server applies again.

const MAX_NAME = 200;
const MAX_HOUSE_CODE = 32;
const MAX_HOUSE_NAME = 60;

/** Last resort where nothing is painted and no colour is set. */
const PICKER_FALLBACK = color.paper.$value;

const IN_USE_NOTE = "in use — referenced by roster or carnival data";

interface HouseRow {
  /** Stable across edits — a house is identified by its position, not its code. */
  key: string;
  code: string;
  name: string;
  colour: string;
  /** The server's own removal guard, read ahead. */
  inUse: boolean;
}

interface FormState {
  name: string;
  brandColour: string;
  houses: HouseRow[];
}

let rowKeySeed = 0;

function formOf(settings: SchoolSettings): FormState {
  return {
    name: settings.name,
    brandColour: settings.brandColour ?? "",
    houses: settings.houses.map((house) => ({
      key: `h${++rowKeySeed}`,
      code: house.code,
      name: house.name,
      colour: house.colour ?? "",
      inUse: house.inUse,
    })),
  };
}

/** What "changed" means here: the values a save would send. */
function signatureOf(form: FormState): string {
  return JSON.stringify([
    form.name.trim(),
    form.brandColour.trim().toLowerCase(),
    form.houses.map((h) => [h.code.trim(), h.name.trim(), h.colour.trim().toLowerCase()]),
  ]);
}

/** The picker needs a colour to open on; an unset one starts from the painted brand. */
function pickerValue(colour: string | null): string {
  return colour ?? getBrandingState().brandColour ?? PICKER_FALLBACK;
}

interface ServerFieldError {
  /** `name`, `brandColour`, `houses`, or `houses[i].code|name|colour`. */
  field: string;
  detail: string;
}

function fieldErrorOf(err: unknown): ServerFieldError | null {
  if (!(err instanceof ApiError) || err.status !== 422) return null;
  const field = err.body?.field;
  return typeof field === "string" ? { field, detail: errorText(err) } : null;
}

/** The control a named refusal is about, so focus can land on it. */
function controlIdOf(field: string): string | null {
  if (field === "name") return "school-name";
  if (field === "brandColour") return "brand-colour";
  const house = /^houses\[(\d+)\]\.(code|name|colour)$/.exec(field);
  return house ? houseFieldId(Number(house[1]), house[2]!) : null;
}

function houseFieldId(index: number, part: string): string {
  return `house-${index}-${part}`;
}

function describedBy(...ids: (string | false | null | undefined)[]): string | undefined {
  const named = ids.filter((id): id is string => typeof id === "string");
  return named.length > 0 ? named.join(" ") : undefined;
}

export default function SchoolSettingsScreen() {
  const session = useSession();
  const isAdmin = hasRole(session, "SystemAdministrator");
  const gate = useAccessGate(isAdmin);
  const connectivity = useConnectivity();
  const offline = connectivity !== "connected";
  const settings = useApiQuery(keys.schoolSettings(), getSchoolSettings, { enabled: isAdmin });
  const invalidate = useInvalidate();
  const [baseline, setBaseline] = useState<SchoolSettings | null>(null);
  const [form, setForm] = useState<FormState | null>(null);
  const [serverError, setServerError] = useState<ServerFieldError | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [saving, setSaving] = useState(false);
  const adopted = useRef<string | null | undefined>(undefined);
  const wantsReload = useRef(false);
  const focusRow = useRef<string | null>(null);

  const adopt = useCallback((data: SchoolSettings) => {
    wantsReload.current = false;
    adopted.current = data.updatedAtUtc;
    setBaseline(data);
    setForm(formOf(data));
    setStale(false);
    setServerError(null);
  }, []);

  const baselineForm = useMemo(() => (baseline ? formOf(baseline) : null), [baseline]);
  const dirty =
    form !== null && baselineForm !== null && signatureOf(form) !== signatureOf(baselineForm);

  // A background refetch must never discard typed edits: a newer stamp arriving
  // mid-edit says so and leaves the form alone until Reload asks for it.
  useEffect(() => {
    const data = settings.data;
    if (!data || adopted.current === data.updatedAtUtc) return;
    if (dirty && !wantsReload.current) {
      setStale(true);
      return;
    }
    adopt(data);
  }, [settings.data, dirty, adopt]);

  // A refusal is only useful where the administrator can act on it.
  useEffect(() => {
    if (!serverError) return;
    const id = controlIdOf(serverError.field);
    if (id) document.getElementById(id)?.focus();
  }, [serverError]);

  function change(next: FormState): void {
    setForm(next);
    setServerError(null);
    setError(null);
  }

  function changeHouse(key: string, patch: Partial<HouseRow>): void {
    if (!form) return;
    change({
      ...form,
      houses: form.houses.map((row) => (row.key === key ? { ...row, ...patch } : row)),
    });
  }

  function addHouse(): void {
    if (!form) return;
    const row: HouseRow = { key: `h${++rowKeySeed}`, code: "", name: "", colour: "", inUse: false };
    focusRow.current = row.key;
    change({ ...form, houses: [...form.houses, row] });
  }

  function removeHouse(key: string): void {
    if (!form) return;
    change({ ...form, houses: form.houses.filter((row) => row.key !== key) });
  }

  function reset(): void {
    if (!baseline) return;
    setForm(formOf(baseline));
    setServerError(null);
    setError(null);
  }

  function reload(): void {
    wantsReload.current = true;
    setStale(false);
    const data = settings.data;
    if (data && adopted.current !== data.updatedAtUtc) adopt(data);
    else invalidate(keys.schoolSettings());
  }

  async function save(e: FormEvent): Promise<void> {
    e.preventDefault();
    if (saving) return;
    if (!form || !baseline) return;
    setSaving(true);
    setServerError(null);
    setError(null);
    setStale(false);
    try {
      const saved = await putSchoolSettings({
        name: form.name.trim(),
        brandColour: blankToNull(form.brandColour),
        houses: form.houses.map((row): HouseSettingRequest => ({
          code: row.code.trim(),
          name: blankToNull(row.name),
          colour: blankToNull(row.colour),
        })),
        updatedAtUtc: baseline.updatedAtUtc,
      });
      adopt(saved);
      showToast("School settings saved");
      invalidate(keys.houses());
      // The chrome, the sign-in screen and the dashboard read these, not the
      // form — repaint them here rather than making the administrator reload.
      await refreshBranding();
      await refreshHouseMarks();
    } catch (err) {
      const field = fieldErrorOf(err);
      if (err instanceof ApiError && err.code === "SETTINGS_STALE") setStale(true);
      else if (field) setServerError(field);
      else setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setSaving(false);
    }
  }

  const fieldError = (field: string): string | null =>
    serverError?.field === field ? serverError.detail : null;

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="School settings">
        <p>Sign in to administer the school's name, colour and houses.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>School settings</h1>
        <div className="alert error" role="alert">
          Administering school settings needs the system administrator role.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  const nameError = fieldError("name");
  const brandError = fieldError("brandColour");
  const housesError = fieldError("houses");

  return (
    <>
      <p>
        <a href={href("/school")}>← School</a>
      </p>
      <h1>School settings</h1>
      <p className="row-sub">
        The school's name, its brand colour and its houses. Saving repaints the app for every
        device the next time it reaches the server.
      </p>

      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so school settings can’t be saved — reconnect and Save comes
          back.
        </div>
      )}
      {settings.error && (
        <div className="alert error" role="alert">
          {errorText(
            settings.error,
            "Can’t reach the server, so the school's settings couldn’t be read.",
          )}
        </div>
      )}
      {stale && (
        <div className="alert notice" role="alert">
          Someone else saved school settings first — reload to see their changes.
          <div className="button-row">
            <button type="button" onClick={reload}>
              Reload
            </button>
          </div>
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      {settings.loading && <p>Loading…</p>}

      {form && (
        <form className="card settings-form" onSubmit={(e) => void save(e)}>
          <label className="field wide">
            <span>School name</span>
            <input
              id="school-name"
              value={form.name}
              maxLength={MAX_NAME}
              aria-describedby={describedBy(nameError && "school-name-error")}
              aria-invalid={nameError ? true : undefined}
              onChange={(e) => change({ ...form, name: e.target.value })}
            />
          </label>
          {nameError && (
            <p className="field-error" id="school-name-error" role="alert">
              {nameError}
            </p>
          )}

          <BrandColourField
            value={form.brandColour}
            schoolName={form.name}
            error={brandError}
            onChange={(brandColour) => change({ ...form, brandColour })}
          />

          <h2>Houses</h2>
          <p className="row-sub">
            A house that roster or carnival data already references cannot be removed or
            renamed. Adding one is always safe.
          </p>
          {housesError && (
            <p className="field-error" id="houses-error" role="alert">
              {housesError}
            </p>
          )}
          <ul className="house-rows">
            {form.houses.map((row, index) => (
              <HouseRowFields
                key={row.key}
                row={row}
                index={index}
                rows={form.houses}
                serverError={serverError}
                focusRow={focusRow}
                onChange={(patch) => changeHouse(row.key, patch)}
                onRemove={() => removeHouse(row.key)}
              />
            ))}
          </ul>
          <div className="button-row">
            <button type="button" onClick={addHouse}>
              Add house
            </button>
          </div>

          <div className="button-row">
            <button className="primary" type="submit" disabled={saving || !dirty || offline}>
              {saving ? "Saving…" : "Save"}
            </button>
            <button type="button" onClick={reset} disabled={saving || !dirty}>
              Cancel
            </button>
          </div>
        </form>
      )}
    </>
  );
}

function blankToNull(value: string): string | null {
  const text = value.trim();
  return text === "" ? null : text;
}

function BrandColourField({
  value,
  schoolName,
  error,
  onChange,
}: {
  value: string;
  schoolName: string;
  error: string | null;
  onChange: (value: string) => void;
}) {
  const candidate = normaliseBrandColour(value);
  const blank = value.trim() === "";
  const ink = candidate ? inkFor(candidate) : null;
  const ratio = candidate && ink ? contrastRatio(candidate, ink) : null;

  const band: CSSProperties = candidate
    ? ({ "--band": candidate, "--band-ink": ink } as CSSProperties)
    : ({ "--band": "var(--wash)", "--band-ink": "var(--ink)" } as CSSProperties);

  return (
    <>
      <ColourPickerField
        className="wide"
        id="brand-colour"
        label="Brand colour"
        clearLabel="No brand colour — default palette"
        value={value}
        colour={candidate}
        pickerColour={pickerValue(candidate)}
        describedBy={describedBy(error && "brand-colour-error")}
        invalid={error !== null}
        onChange={onChange}
      />
      {error && (
        <p className="field-error" id="brand-colour-error" role="alert">
          {error}
        </p>
      )}
      <div className="brand-preview" style={band}>
        {schoolName.trim() || "Your school"}
      </div>
      {blank ? (
        <p className="row-sub">No brand colour — the app keeps its built-in default.</p>
      ) : !candidate ? (
        <p className="field-error">Not a colour. Write it as #rrggbb or #rgb.</p>
      ) : ratio !== null && ratio < MIN_BRAND_CONTRAST ? (
        // Not "the server will refuse this": it compares against pure black, this against the
        // ink the chrome paints, so a colour can be unreadable here and accepted there.
        <p className="field-error">
          {inkLabel(ink)} · {ratio.toFixed(1)}:1 — unreadable, no ink reaches{" "}
          {MIN_BRAND_CONTRAST}:1 on this colour
        </p>
      ) : (
        <p className="row-sub">
          {inkLabel(ink)} · {ratio?.toFixed(1)}:1
        </p>
      )}
    </>
  );
}

function inkLabel(ink: string | null): string {
  return ink === BRAND_INK_LIGHT ? "white text" : "dark text";
}

function HouseRowFields({
  row,
  index,
  rows,
  serverError,
  focusRow,
  onChange,
  onRemove,
}: {
  row: HouseRow;
  index: number;
  rows: HouseRow[];
  serverError: ServerFieldError | null;
  focusRow: { current: string | null };
  onChange: (patch: Partial<HouseRow>) => void;
  onRemove: () => void;
}) {
  const [armed, setArmed] = useState(false);
  const confirmRef = useRef<HTMLButtonElement | null>(null);
  const removeRef = useRef<HTMLButtonElement | null>(null);
  const wasArmed = useRef(false);
  const label = row.name.trim() || row.code.trim() || `house ${index + 1}`;
  const colour = normaliseBrandColour(row.colour);
  const hint = houseHint(row, index, rows, colour);
  const at = (part: string): string | null =>
    serverError?.field === `houses[${index}].${part}` ? serverError.detail : null;
  const codeError = at("code");
  const nameError = at("name");
  const colourError = at("colour");
  const inUseId = houseFieldId(index, "in-use");

  // The armed pair replaces the trigger, so focus has to be handed over both
  // ways or a keyboard remove drops it to the top of the document.
  useEffect(() => {
    if (armed) confirmRef.current?.focus();
    else if (wasArmed.current) removeRef.current?.focus();
    wasArmed.current = armed;
  }, [armed]);

  return (
    <li>
      <div className="house-row">
        <label className="field flush-bottom">
          <span>Code</span>
          <input
            id={houseFieldId(index, "code")}
            aria-label={`House ${index + 1} code`}
            value={row.code}
            maxLength={MAX_HOUSE_CODE}
            readOnly={row.inUse}
            aria-describedby={describedBy(
              row.inUse && inUseId,
              codeError && `${houseFieldId(index, "code")}-error`,
            )}
            aria-invalid={codeError ? true : undefined}
            ref={(el) => {
              if (el && focusRow.current === row.key) {
                focusRow.current = null;
                el.focus();
              }
            }}
            onChange={(e) => onChange({ code: e.target.value })}
          />
        </label>
        <label className="field flush-bottom">
          <span>Display name</span>
          <input
            id={houseFieldId(index, "name")}
            aria-label={`House ${index + 1} display name`}
            value={row.name}
            maxLength={MAX_HOUSE_NAME}
            placeholder={row.code.trim() || "Same as the code"}
            aria-describedby={describedBy(nameError && `${houseFieldId(index, "name")}-error`)}
            aria-invalid={nameError ? true : undefined}
            onChange={(e) => onChange({ name: e.target.value })}
          />
        </label>
        {/* The preview is a SIBLING of the label: `label.field > span` would
            otherwise flatten the house mark and swallow its swatch. */}
        <div>
          <ColourPickerField
            className="flush-bottom"
            id={houseFieldId(index, "colour")}
            label={`House ${index + 1} colour`}
            caption="Colour"
            clearLabel="No colour — text only"
            value={row.colour}
            colour={colour}
            pickerColour={colour ?? PICKER_FALLBACK}
            describedBy={describedBy(colourError && `${houseFieldId(index, "colour")}-error`)}
            invalid={colourError !== null}
            onChange={(next) => onChange({ colour: next })}
          />
          <span className="house-mark row-sub">
            {colour && (
              <span
                className="house-swatch"
                style={{ "--swatch": colour } as CSSProperties}
                aria-hidden="true"
              />
            )}
            {row.name.trim() || row.code.trim() || "Unnamed house"}
          </span>
        </div>
        <div className="house-remove">
          {!armed && (
            <button
              type="button"
              className="danger"
              aria-label={`Remove ${label}`}
              ref={removeRef}
              disabled={row.inUse}
              aria-describedby={describedBy(row.inUse && inUseId)}
              onClick={() => setArmed(true)}
            >
              Remove…
            </button>
          )}
          {row.inUse && (
            <p className="row-sub" id={inUseId}>
              {IN_USE_NOTE}
            </p>
          )}
        </div>
      </div>
      {armed && (
        <div className="row-armed" role="alert">
          <span>
            Remove {label}? The server refuses this if any roster or carnival data still
            references it.
          </span>
          <button
            type="button"
            className="danger armed"
            aria-label={`Confirm removing ${label}`}
            ref={confirmRef}
            onClick={onRemove}
          >
            Confirm remove
          </button>
          <button type="button" onClick={() => setArmed(false)}>
            Cancel
          </button>
        </div>
      )}
      {hint && <p className="field-error">{hint}</p>}
      {codeError && (
        <p className="field-error" id={`${houseFieldId(index, "code")}-error`} role="alert">
          {codeError}
        </p>
      )}
      {nameError && (
        <p className="field-error" id={`${houseFieldId(index, "name")}-error`} role="alert">
          {nameError}
        </p>
      )}
      {colourError && (
        <p className="field-error" id={`${houseFieldId(index, "colour")}-error`} role="alert">
          {colourError}
        </p>
      )}
    </li>
  );
}

/** Client-side preview of the server's house rules — never a gate on saving. */
function houseHint(
  row: HouseRow,
  index: number,
  rows: HouseRow[],
  colour: string | null,
): string | null {
  const code = row.code.trim();
  if (code !== "") {
    const twin = rows.findIndex((other) => other.code.trim().toLowerCase() === code.toLowerCase());
    if (twin >= 0 && twin !== index)
      return `“${code}” is listed twice; house codes must be unique.`;
  }
  if (row.colour.trim() !== "" && !colour) return "Not a colour. Write it as #rrggbb or #rgb.";
  if (colour) {
    const twin = rows.findIndex((other) => normaliseBrandColour(other.colour) === colour);
    if (twin >= 0 && twin !== index) return "Another house already uses this colour.";
  }
  return null;
}
