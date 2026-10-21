import { useState, type ReactNode } from "react";
import type { AttemptsPerHeight, LadderRequest, MeasureType } from "../../api/types";
import { measureRaw } from "../../results/measureFormat";
import { EVENT_TYPES } from "./eventLabels";
import { TagEditor } from "./TagEditor";

const UNITS: Readonly<Record<MeasureType, string>> = {
  Time: "seconds",
  Distance: "metres",
  Height: "metres",
  Count: "count",
};

/** What AT-HIGH-JUMP ships with, and a sane bar for a new one. */
const DEFAULT_LADDER: LadderRequest = { start: 1.2, step: 0.05 };

/** Whole centimetres, in metres. */
const LADDER_LIMITS = { start: { min: 0.01, max: 5 }, step: { min: 0.01, max: 1 } };

export interface MeasureFields {
  attempts: string;
  barStart: string;
  barStep: string;
  /** "3" or "2" on a Height event; blank reads as 3. */
  perHeight: string;
}

/** The catalogue fields as typed, whether by this hook or an editor's own state. */
export interface DefinitionValues extends MeasureFields {
  name: string;
  code: string;
  measureType: MeasureType;
  units: string;
}

export interface DefinitionReading {
  /** null = a single typed mark. */
  attemptCount: number | null;
  /** null = no deck suggestions; the type, not this, is what says "bar ladder". */
  ladder: LadderRequest | null;
  /** A Height event's default for its divisions; null on every other type. */
  attemptsPerHeight: AttemptsPerHeight | null;
  /** Everything but the sport, which each caller chooses its own way. */
  incompleteWithoutSport: boolean;
}

export interface DefinitionFieldsState extends DefinitionReading {
  name: string;
  setName: (value: string) => void;
  code: string;
  setCode: (value: string) => void;
  measureType: MeasureType;
  changeMeasure: (next: MeasureType) => void;
  units: string;
  setUnits: (value: string) => void;
  attempts: string;
  setAttempts: (value: string) => void;
  barStart: string;
  setBarStart: (value: string) => void;
  barStep: string;
  setBarStep: (value: string) => void;
  perHeight: string;
  setPerHeight: (value: string) => void;
  tags: string[];
  setTags: (value: string[]) => void;
}

/** The types the server lets carry a series of attempts. */
export function ownsAttempts(measureType: MeasureType): boolean {
  return measureType === "Distance" || measureType === "Count";
}

/** Switching type seeds the fields it owns and clears the other's. */
export function nextFieldsForMeasure(prev: MeasureFields, next: MeasureType): MeasureFields {
  if (next === "Height")
    return {
      attempts: "",
      barStart: measureRaw("Height", DEFAULT_LADDER.start),
      barStep: measureRaw("Height", DEFAULT_LADDER.step),
      perHeight: "3",
    };
  return {
    attempts: ownsAttempts(next) ? prev.attempts : "",
    barStart: "",
    barStep: "",
    perHeight: "",
  };
}

/** What the typed fields mean, and whether they are ready to send. */
export function readDefinitionFields(values: DefinitionValues): DefinitionReading {
  const ladderState = readLadder(values.measureType, values.barStart, values.barStep);
  const series = ownsAttempts(values.measureType);
  const attempts = readAttempts(values.attempts);
  return {
    attemptCount: series ? attempts.attemptCount : null,
    ladder: ladderState.ladder,
    attemptsPerHeight: values.measureType === "Height" ? (values.perHeight === "2" ? 2 : 3) : null,
    incompleteWithoutSport:
      values.name.trim() === "" ||
      values.code.trim() === "" ||
      values.units.trim() === "" ||
      (series && attempts.invalid) ||
      ladderState.invalid,
  };
}

/** Blank is a single typed mark, anything else 1-6 whole attempts. */
function readAttempts(attempts: string): { attemptCount: number | null; invalid: boolean } {
  if (attempts.trim() === "") return { attemptCount: null, invalid: false };
  const count = Number(attempts);
  const ok = Number.isInteger(count) && count >= 1 && count <= 6;
  // An out-of-range count is still the count TYPED: `invalid` blocks the save,
  // and an editor comparing against what is stored must see the difference.
  return { attemptCount: Number.isFinite(count) ? count : null, invalid: !ok };
}

export function useDefinitionFields(): DefinitionFieldsState {
  const [name, setName] = useState("");
  const [code, setCode] = useState("");
  const [measureType, setMeasureType] = useState<MeasureType>("Time");
  const [units, setUnits] = useState(UNITS.Time);
  // Blank is a single typed mark; choosing Distance seeds a series.
  const [attempts, setAttempts] = useState("");
  const [barStart, setBarStart] = useState("");
  const [barStep, setBarStep] = useState("");
  const [perHeight, setPerHeight] = useState("");
  const [tags, setTags] = useState<string[]>([]);

  function changeMeasure(next: MeasureType): void {
    if (units === UNITS[measureType]) setUnits(UNITS[next]);
    const fields = nextFieldsForMeasure({ attempts, barStart, barStep, perHeight }, next);
    // The Distance seed is this form's default for a new event, not a server rule.
    setAttempts(next === "Distance" ? "3" : fields.attempts);
    setBarStart(fields.barStart);
    setBarStep(fields.barStep);
    setPerHeight(fields.perHeight);
    setMeasureType(next);
  }

  return {
    name,
    setName,
    code,
    setCode,
    measureType,
    changeMeasure,
    units,
    setUnits,
    attempts,
    setAttempts,
    barStart,
    setBarStart,
    barStep,
    setBarStep,
    perHeight,
    setPerHeight,
    tags,
    setTags,
    ...readDefinitionFields({
      name,
      code,
      measureType,
      units,
      attempts,
      barStart,
      barStep,
      perHeight,
    }),
  };
}

/** Both blank is none; half-filled or off the centimetre grid is refused here and by the server. */
export function readLadder(
  measureType: MeasureType,
  barStart: string,
  barStep: string,
): { ladder: LadderRequest | null; invalid: boolean } {
  if (measureType !== "Height") return { ladder: null, invalid: false };
  const start = barStart.trim();
  const step = barStep.trim();
  if (start === "" && step === "") return { ladder: null, invalid: false };
  const values = { start: Number(start), step: Number(step) };
  const ok =
    inRange(values.start, LADDER_LIMITS.start) && inRange(values.step, LADDER_LIMITS.step);
  return ok ? { ladder: values, invalid: false } : { ladder: null, invalid: true };
}

function inRange(value: number, limit: { min: number; max: number }): boolean {
  if (!Number.isFinite(value) || value < limit.min || value > limit.max) return false;
  return Math.abs(value * 100 - Math.round(value * 100)) < 1e-6;
}

const FROZEN_NOTE = "Frozen: results are recorded under this type.";

export function EventTypeSelect({
  measureType,
  changeMeasure,
  writable,
  frozen = false,
}: {
  measureType: MeasureType;
  changeMeasure: (next: MeasureType) => void;
  writable: boolean;
  /** The server refuses a changed type once results exist (MEASURE_FROZEN). */
  frozen?: boolean;
}) {
  return (
    <>
      {/* Full row: the type names run past a single grid track and would clip. */}
      <label className="field full-row">
        <span>Event type</span>
        <select
          value={measureType}
          onChange={(e) => changeMeasure(e.target.value as MeasureType)}
          disabled={!writable || frozen}
        >
          {EVENT_TYPES.map((type) => (
            <option key={type.measure} value={type.measure}>
              {type.label}
            </option>
          ))}
          {/* Only so a definition already stored as `Count` still reads as itself:
              the three types above are what a school chooses between. */}
          {measureType === "Count" && <option value="Count">Count</option>}
        </select>
      </label>
      {frozen && (
        <p className="row-sub field-note">{FROZEN_NOTE}</p>
      )}
    </>
  );
}

/** What the chosen type owns: the series count, or the bar's suggestions and attempts. */
export function EventTypeFields({
  measureType,
  attempts,
  setAttempts,
  barStart,
  setBarStart,
  barStep,
  setBarStep,
  perHeight,
  setPerHeight,
  writable,
  frozen = false,
  legacyAttemptCount = null,
}: {
  measureType: MeasureType;
  attempts: string;
  setAttempts: (value: string) => void;
  barStart: string;
  setBarStart: (value: string) => void;
  barStep: string;
  setBarStep: (value: string) => void;
  perHeight: string;
  setPerHeight: (value: string) => void;
  writable: boolean;
  /** The server refuses a changed attempt count once results exist (ATTEMPT_COUNT_FROZEN). */
  frozen?: boolean;
  /** A count stored on a Height definition by an older build; re-sent unchanged. */
  legacyAttemptCount?: number | null;
}) {
  if (measureType === "Height")
    return (
      <>
        <label className="field">
          <span>Suggested opening height</span>
          <input
            type="number"
            inputMode="decimal"
            step="0.01"
            min={LADDER_LIMITS.start.min}
            max={LADDER_LIMITS.start.max}
            value={barStart}
            placeholder="1.20"
            onChange={(e) => setBarStart(e.target.value)}
            disabled={!writable}
          />
        </label>
        <label className="field">
          <span>Usual rise</span>
          <input
            type="number"
            inputMode="decimal"
            step="0.01"
            min={LADDER_LIMITS.step.min}
            max={LADDER_LIMITS.step.max}
            value={barStep}
            placeholder="0.05"
            onChange={(e) => setBarStep(e.target.value)}
            disabled={!writable}
          />
        </label>
        <label className="field">
          <span>Attempts per height</span>
          <select
            value={perHeight === "2" ? "2" : "3"}
            onChange={(e) => setPerHeight(e.target.value)}
            disabled={!writable}
          >
            <option value="3">3</option>
            <option value="2">2</option>
          </select>
        </label>
        <p className="row-sub field-note">
          Suggestions for the deck — the official sets the opening height and every raise there,
          and can switch a division between 3 and 2 attempts until its first mark.
        </p>
        {legacyAttemptCount !== null && (
          <p className="row-sub field-note">
            Stored attempt count: {legacyAttemptCount}, from an earlier version. It is kept as it
            is.
          </p>
        )}
      </>
    );
  if (!ownsAttempts(measureType)) return null;
  return (
    <>
      <label className="field">
        <span>Attempts</span>
        <input
          type="number"
          inputMode="numeric"
          min={1}
          max={6}
          value={attempts}
          placeholder="one mark"
          onChange={(e) => setAttempts(e.target.value)}
          disabled={!writable || frozen}
        />
      </label>
      {frozen && <p className="row-sub field-note">{FROZEN_NOTE}</p>}
    </>
  );
}

export function DefinitionFields({
  fields,
  sportField,
  writable,
}: {
  fields: DefinitionFieldsState;
  sportField: ReactNode;
  writable: boolean;
}) {
  return (
    <>
      <label className="field wide flush-top">
        <span>Name</span>
        <input
          value={fields.name}
          placeholder="Freestyle 50m"
          onChange={(e) => fields.setName(e.target.value)}
          disabled={!writable}
        />
      </label>
      <div className="form-grid">
        <label className="field">
          <span>Code</span>
          <input
            value={fields.code}
            placeholder="SW-50-FREE"
            onChange={(e) => fields.setCode(e.target.value)}
            disabled={!writable}
          />
        </label>
        {sportField}
        <EventTypeSelect
          measureType={fields.measureType}
          changeMeasure={fields.changeMeasure}
          writable={writable}
        />
        <label className="field">
          <span>Units</span>
          <input
            value={fields.units}
            onChange={(e) => fields.setUnits(e.target.value)}
            disabled={!writable}
          />
        </label>
        <EventTypeFields
          measureType={fields.measureType}
          attempts={fields.attempts}
          setAttempts={fields.setAttempts}
          barStart={fields.barStart}
          setBarStart={fields.setBarStart}
          barStep={fields.barStep}
          setBarStep={fields.setBarStep}
          perHeight={fields.perHeight}
          setPerHeight={fields.setPerHeight}
          writable={writable}
        />
      </div>
      <TagEditor tags={fields.tags} onChange={fields.setTags} writable={writable} />
    </>
  );
}
