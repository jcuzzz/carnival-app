import { useEffect, useRef, useState, type FormEvent, type RefObject } from "react";
import { keys } from "../../api/queryKeys";
import { useInvalidate } from "../../api/useApiQuery";
import type { Attempt, AttemptCorrection, CarnivalEvent, Entry } from "../../api/types";
import type { NonOkResultStatus, ResultCapturePayload } from "../../api/outboxTransport";
import type { OutboxItem } from "../../db/db";
import { syncWorker } from "../../api/syncSetup";
import { editAndRequeue, InvalidTransitionError } from "../../sync/outbox";
import { measureFieldLabel, readMeasureField } from "../../results/markMask";
import { measureRaw } from "../../results/measureFormat";
import { MeasureInput } from "../../results/MeasureInput";
import { submitCorrection } from "../../results/correctResult";
import { submitResultViaOutbox } from "../../results/submitResult";
import { STATUS_FROM_TYPED } from "./statusLabels";

/**
 * Every first capture is enqueued, never fetched directly; a correction reuses this form. The row
 * menu's DNS/DNF/DQ fire the same doSubmit, so one state machine drives both.
 */
export function ResultForm({
  carnivalId,
  event,
  entry,
  actorSubject,
  inputRef,
  statusRef,
  correcting,
  attemptEdit,
  statusOnly,
  parked,
  rebase,
  initialRaw = "",
  onSubmitted,
  onCancel,
}: {
  carnivalId: string;
  event: CarnivalEvent | undefined;
  entry: Entry;
  actorSubject: string | null;
  inputRef: RefObject<HTMLInputElement | null>;
  /** Filled with this form's status submit, for the row's actions menu. */
  statusRef?: RefObject<((code: NonOkResultStatus) => void) | null>;
  /** Set to correct an existing result: its id + the revision being amended. */
  correcting?: { resultId: string; baseVersion: number };
  /** A per-cell correction restates the whole series: an attempts event refuses a best mark. */
  attemptEdit?: {
    ordinal: number;
    attemptCount: number;
    attempts: readonly (Attempt | null)[] | undefined;
    /** The cell arrives fouled, so Save sends the X rather than a typed mark. */
    foul?: boolean;
  };
  /** The strip captures the marks, so this form carries only the row menu's status write. */
  statusOnly?: boolean;
  /** Retype a parked capture: re-sent under its own id, so a re-send is idempotent. */
  parked?: OutboxItem;
  /** The revision the parked correction is re-based on before it goes back —
   *  its own `baseVersion` is the one the server already refused. */
  rebase?: number;
  /** Prefill for a correction ("1:23.45" or "DNS"). */
  initialRaw?: string;
  /** Fired once a capture is enqueued/accepted; `itemId` is null when the
   *  correction went straight to the server (nothing to undo locally). */
  onSubmitted: (itemId: string | null) => void;
  onCancel?: () => void;
}) {
  const [raw, setRaw] = useState(initialRaw);
  const [reason, setReason] = useState("");
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Read by the menu path, which fires outside this render's closure.
  const busyRef = useRef(false);
  const invalidate = useInvalidate();
  const measureType = event?.measureType ?? "Time";
  const field = readMeasureField(measureType, raw);
  const eventOpen = event === undefined || event.status === "Open";
  // Every control that could fire a second submission is dead while one enqueues.
  const disabled = !eventOpen || busy;

  // A fouled cell has no mark to type, so Save is live on an empty field; a mark typed over it
  // wins, because the last thing the official said is what they mean.
  const foulCell = attemptEdit !== undefined && (attemptEdit.foul ?? false) && raw.trim() === "";

  async function doSubmit(explicitStatus?: NonOkResultStatus, asFoul = false) {
    // One enqueue per tap, whichever control fired it.
    if (busyRef.current || !eventOpen) return;
    if (!actorSubject) {
      setSubmitError("Sign in first.");
      return;
    }
    let status: NonOkResultStatus | null = explicitStatus ?? null;
    let value: string | null = null;
    let attempts: (AttemptCorrection | null)[] | undefined;
    if (!explicitStatus) {
      if (attemptEdit && (asFoul || foulCell)) {
        attempts = seriesWith(attemptEdit, measureType, null);
      } else if (field.mode === "status") {
        if (!field.statusCode) return; // incomplete code — the field shows guidance
        status = STATUS_FROM_TYPED[field.statusCode];
      } else {
        if (!field.valid || !field.value) return; // Save is disabled anyway
        if (attemptEdit) attempts = seriesWith(attemptEdit, measureType, field.value);
        else value = field.value;
      }
    }
    setSubmitError(null);
    busyRef.current = true;
    setBusy(true);
    try {
      if (parked) {
        await editAndRequeue(
          parked.id,
          { ...(parked.payload as ResultCapturePayload), value, status },
          rebase !== undefined ? { baseVersion: rebase } : undefined,
        );
        void syncWorker.syncNow();
        // No undo window: the deck row must not offer a one-tap way to abandon captured work.
        onSubmitted(null);
      } else if (correcting) {
        const outcome = await submitCorrection({
          carnivalId,
          carnivalEventId: entry.carnivalEventId,
          entryId: entry.id,
          entryDisplayName: entry.displayName,
          resultId: correcting.resultId,
          baseVersion: correcting.baseVersion,
          value,
          status,
          measureType,
          ...(attempts ? { attempts } : {}),
          // A reason is mandatory; the default keeps the audit trail honest without a keyboard.
          reason: reason.trim() === "" ? "Corrected at the deck" : reason.trim(),
          actorSubject,
        });
        if (outcome.kind === "stale") {
          setSubmitError(
            "This result changed elsewhere — reload before correcting it again.",
          );
          invalidate(keys.results(carnivalId, entry.carnivalEventId));
          return;
        }
        if (outcome.kind === "corrected") {
          invalidate(keys.results(carnivalId, entry.carnivalEventId));
          onSubmitted(null);
        } else {
          onSubmitted(outcome.item.id);
        }
      } else {
        const item = await submitResultViaOutbox({
          carnivalId,
          carnivalEventId: entry.carnivalEventId,
          entryId: entry.id,
          entryDisplayName: entry.displayName,
          value,
          status,
          measureType,
          actorSubject,
        });
        onSubmitted(item.id);
      }
      setRaw("");
      setReason("");
    } catch (err) {
      setSubmitError(
        err instanceof InvalidTransitionError
          ? "That capture has already been dealt with somewhere else."
          : err instanceof Error
            ? err.message
            : String(err),
      );
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }

  // Re-published on every render so the menu always fires the current closure;
  // cleared on unmount so a menu can never submit through a form that is gone.
  useEffect(() => {
    if (!statusRef) return;
    statusRef.current = (code: NonOkResultStatus) => void doSubmit(code);
    return () => {
      statusRef.current = null;
    };
  });

  return (
    <form
      className={`entry-form${statusOnly ? " status-only" : ""}`}
      onSubmit={(e: FormEvent) => {
        e.preventDefault();
        void doSubmit();
      }}
    >
      {!statusOnly && (
        <>
          <MeasureInput
            id={`time-${entry.id}`}
            measureType={measureType}
            raw={raw}
            onRawChange={setRaw}
            onEnter={() => void doSubmit()}
            disabled={disabled}
            ariaLabel={
              attemptEdit
                ? `Attempt ${attemptEdit.ordinal} ${measureFieldLabel(measureType, entry.displayName)}`
                : measureFieldLabel(measureType, entry.displayName)
            }
            inputRef={inputRef}
          />
          <button
            className="primary"
            type="submit"
            disabled={disabled || (!field.valid && !foulCell)}
          >
            {busy ? "Saving…" : parked ? "Save and send" : correcting ? "Save correction" : "Save"}
          </button>
          {attemptEdit && (
            // The cell may be a foul, which the mask cannot type.
            <button
              type="button"
              disabled={disabled}
              aria-label={`Mark attempt ${attemptEdit.ordinal} for ${entry.displayName} as a foul`}
              onClick={() => void doSubmit(undefined, true)}
            >
              Mark as foul
            </button>
          )}
        </>
      )}
      {correcting && !statusOnly && (
        <>
          <label className="visually-hidden" htmlFor={`reason-${entry.id}`}>
            Reason for correcting {entry.displayName}
          </label>
          <input
            id={`reason-${entry.id}`}
            className="correction-reason"
            type="text"
            placeholder="Reason (optional)"
            autoCorrect="off"
            autoCapitalize="sentences"
            value={reason}
            disabled={disabled}
            onChange={(e) => setReason(e.target.value)}
          />
        </>
      )}
      {onCancel && (
        <button type="button" disabled={busy} onClick={onCancel}>
          Cancel
        </button>
      )}
      {submitError && (
        <span className="field-error" role="alert">
          {submitError}
        </span>
      )}
    </form>
  );
}

/**
 * The whole series with one cell replaced (null value = foul), untouched cells as raw mask strings.
 * Covers the stored length too: the server refuses a correction that truncates the series.
 */
function seriesWith(
  edit: {
    ordinal: number;
    attemptCount: number;
    attempts: readonly (Attempt | null)[] | undefined;
  },
  measureType: CarnivalEvent["measureType"],
  value: string | null,
): (AttemptCorrection | null)[] {
  const length = Math.max(edit.attemptCount, edit.attempts?.length ?? 0, edit.ordinal);
  return Array.from({ length }, (_, i) => {
    if (i + 1 === edit.ordinal)
      return value === null ? { value: null, foul: true } : { value, foul: false };
    const stored = edit.attempts?.[i] ?? null;
    if (stored === null) return null;
    if (stored.foul) return { value: null, foul: true };
    return stored.value == null
      ? null
      : { value: measureRaw(measureType, Number(stored.value)), foul: false };
  });
}
