import { useState } from "react";
import type { Attempt, MeasureType } from "../../api/types";
import type { ResultAttemptPayload } from "../../api/outboxTransport";
import type { OutboxItem } from "../../db/db";
import { measureNoun, readMeasureField } from "../../results/markMask";
import { MeasureInput } from "../../results/MeasureInput";
import { measureRaw } from "../../results/measureFormat";
import { outboxHoldsDisplay } from "./resultDisplay";
import StatusBadge from "../../sync/StatusBadge";

/** The best mark and NM are derived by the server; nothing here computes either. */

/** A foul reads as X wherever a mark would be — the athletics card's own mark. */
const FOUL = "X";

export function AttemptStrip({
  entry,
  measureType,
  attemptCount,
  attempts,
  queued,
  disabled,
  onCapture,
  onEdit,
}: {
  entry: { id: string; displayName: string };
  measureType: MeasureType;
  attemptCount: number;
  /** The series as stored, in ordinal order; absent until the first attempt. */
  attempts: readonly (Attempt | null)[] | undefined;
  /** This device's un-drained captures for this entry, by ordinal. */
  queued: ReadonlyMap<number, OutboxItem> | undefined;
  disabled: boolean;
  onCapture: (ordinal: number, value: string | null, foul: boolean) => void;
  /** Null where the result cannot be corrected (finalised, or not yet recorded). */
  onEdit: ((ordinal: number, initialRaw: string) => void) | null;
}) {
  const [raw, setRaw] = useState<Readonly<Record<number, string>>>({});
  const ordinals = Array.from({ length: attemptCount }, (_, i) => i + 1);

  return (
    <div className="attempt-strip" role="group" aria-label={`Attempts for ${entry.displayName}`}>
      {ordinals.map((ordinal) => {
        const stored = attempts?.[ordinal - 1] ?? null;
        const storedTaken = stored != null && (stored.foul || stored.value != null);
        const item = queued?.get(ordinal);
        // Mirrors `outboxHoldsDisplay`: an ACCEPTED capture is the server's now,
        // so the stored series wins the cell back — and with it the cell's Edit.
        if (item && (outboxHoldsDisplay(item) || !storedTaken)) {
          const payload = item.payload as ResultAttemptPayload;
          return (
            <span className="attempt-cell taken" key={ordinal}>
              <span className="attempt-ordinal" aria-hidden="true">
                {ordinal}
              </span>
              <span className="mono attempt-mark" aria-label={`Attempt ${ordinal}`}>
                {payload.foul ? FOUL : (payload.value ?? "?")}
              </span>
              <StatusBadge status={item.status} />
            </span>
          );
        }
        if (stored != null && storedTaken) {
          const mark = stored.foul ? FOUL : measureRaw(measureType, Number(stored.value));
          return (
            <span className="attempt-cell taken" key={ordinal}>
              <span className="attempt-ordinal" aria-hidden="true">
                {ordinal}
              </span>
              <span className="mono attempt-mark" aria-label={`Attempt ${ordinal}`}>
                {mark}
              </span>
              {onEdit && (
                <button
                  type="button"
                  className="edit-result"
                  aria-label={`Edit attempt ${ordinal} for ${entry.displayName}`}
                  onClick={() => onEdit(ordinal, stored.foul ? "" : mark)}
                >
                  Edit
                </button>
              )}
            </span>
          );
        }
        const typed = raw[ordinal] ?? "";
        const field = readMeasureField(measureType, typed);
        // Letters are the row menu's business: a cell takes a mark or a foul, and NM is derived.
        const savable = !disabled && field.mode === "value" && field.valid;
        const save = () => {
          if (!savable) return;
          setRaw({ ...raw, [ordinal]: "" });
          onCapture(ordinal, field.value, false);
        };
        return (
          <span className="attempt-cell" key={ordinal}>
            <span className="attempt-ordinal" aria-hidden="true">
              {ordinal}
            </span>
            <MeasureInput
              id={`attempt-${entry.id}-${ordinal}`}
              measureType={measureType}
              raw={typed}
              onRawChange={(next) => setRaw({ ...raw, [ordinal]: next })}
              onEnter={save}
              disabled={disabled}
              ariaLabel={`Attempt ${ordinal} ${measureNoun(measureType)} for ${entry.displayName}`}
            />
            <button type="button" className="primary" disabled={!savable} onClick={save}>
              Save
            </button>
            <button
              type="button"
              disabled={disabled}
              aria-label={`Foul on attempt ${ordinal} for ${entry.displayName}`}
              // A foul is what happened, so it wins over anything half-typed in
              // the cell; the field is cleared rather than left to contradict it.
              onClick={() => {
                setRaw({ ...raw, [ordinal]: "" });
                onCapture(ordinal, null, true);
              }}
            >
              Foul
            </button>
          </span>
        );
      })}
    </div>
  );
}
