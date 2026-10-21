import type { ReactNode } from "react";
import type { Attempt, AttemptCorrection, MeasureType, ResultDto } from "../../api/types";
import { codeText } from "../../api/errorText";
import {
  RESULT_CORRECT_KIND,
  RESULT_JUMP_KIND,
  RESULT_SUBMIT_KIND,
  type ResultCapturePayload,
  type ResultCorrectPayload,
} from "../../api/outboxTransport";
import type { OutboxItem } from "../../db/db";
import { formatCapturedValue, formatMeasure, measureRaw } from "../../results/measureFormat";
import StatusBadge from "../../sync/StatusBadge";
import { statusLabel } from "./statusLabels";

/** The server's human `detail`, unless the client owns better deck wording for the code. */
export function refusalDetail(item: OutboxItem): string | undefined {
  const body = item.serverResponse;
  return codeText(body?.error) ?? body?.detail ?? body?.error ?? item.lastError;
}

export function conflictDetail(item: OutboxItem): string {
  return refusalDetail(item) ?? "see outbox.";
}

/** Refusals no requeue clears: the block is server-side and holds all day. */
const DEAD_END_CODES: ReadonlySet<string> = new Set(["RESULT_FINALISED"]);

/** Can this parked capture be got moving from the deck row it sits on? */
export function recoverableHere(parked: OutboxItem, serverResult: ResultDto | undefined): boolean {
  if (serverResult?.lifecycleState === "Final") return false;
  const code = parked.serverResponse?.error;
  return code === undefined || !DEAD_END_CODES.has(code);
}

/**
 * Validation is automatic, so "Verified" would read as "a person checked this"; the badge says
 * only whether the result is still amendable.
 */
function LifecycleBadge({ state }: { state: ResultDto["lifecycleState"] }): ReactNode {
  const [label, tone] =
    state === "Final"
      ? (["Final", "ok"] as const)
      : state === "Draft"
        ? (["Draft", "warn"] as const)
        : // Provisional (legacy rows) and Verified (every new capture) are the
          // same thing to a recorder: recorded, and still amendable today.
          (["Recorded", "ok"] as const);
  return <span className={`badge ${tone}`}>{label}</span>;
}

/** What an outbox capture would put back in the mask ("1:23.45" / "DNS"). */
export function capturedRaw(item: OutboxItem): string {
  const payload = item.payload as ResultCapturePayload;
  return payload.status ? statusLabel(payload.status) : payload.value ?? "";
}

export interface CorrectionTarget {
  resultId: string;
  /** `currentRevision` — the optimistic-concurrency token being amended. */
  baseVersion: number;
  /** The recorded value, prefilled into the mask ("1:23.45" / "DNS"). */
  initialRaw: string;
  /** The stored series a per-cell correction restates; absent where the base is an ack. */
  attempts?: readonly (Attempt | null)[];
}

/** Kinds whose ack names a result; a first mark mints it, so its ack is a typed DNS's only base. */
function isResultCapture(kind: string): boolean {
  return kind === RESULT_SUBMIT_KIND || kind === RESULT_CORRECT_KIND || kind === RESULT_JUMP_KIND;
}

/**
 * The server result is the preferred base, else an accepted capture's ack. A queued or syncing
 * capture is not correctable: undo or the drain settles it first.
 */
export function correctionTarget(
  serverResult: ResultDto | undefined,
  outboxItem: OutboxItem | undefined,
  /** Unknown reads as a time. */
  measureType: MeasureType = "Time",
): CorrectionTarget | null {
  if (serverResult) {
    // Only carnival finalisation closes a result; the server enforces it (RESULT_FINALISED).
    if (serverResult.lifecycleState === "Final") return null;
    return {
      resultId: serverResult.id,
      baseVersion: serverResult.currentRevision,
      ...(serverResult.attempts ? { attempts: serverResult.attempts } : {}),
      initialRaw:
        serverResult.status === "Ok"
          ? serverResult.measuredValue != null
            ? measureRaw(measureType, Number(serverResult.measuredValue))
            : ""
          : statusLabel(serverResult.status),
    };
  }
  if (outboxItem?.status === "accepted" && isResultCapture(outboxItem.kind)) {
    const ack = outboxItem.serverResponse;
    if (typeof ack?.id !== "string" || typeof ack.currentRevision !== "number") return null;
    const initialRaw = capturedRaw(outboxItem);
    return { resultId: ack.id, baseVersion: ack.currentRevision, initialRaw };
  }
  return null;
}

/** Exported so nothing server-derived (the place) renders beside a value the server never saw. */
export function outboxHoldsDisplay(outboxItem: OutboxItem | undefined): boolean {
  return outboxItem !== undefined && outboxItem.status !== "accepted";
}

export function resultDisplay(
  serverResult: ResultDto | undefined,
  outboxItem: OutboxItem | undefined,
  measureType: MeasureType = "Time",
): { text: string; badge: ReactNode } | null {
  if (outboxItem && outboxHoldsDisplay(outboxItem)) {
    return {
      text: capturedText(outboxItem, measureType),
      badge: <StatusBadge status={outboxItem.status} />,
    };
  }
  if (serverResult) {
    const text =
      serverResult.status === "Ok"
        ? serverResult.measuredValue != null
          ? formatMeasure(measureType, Number(serverResult.measuredValue))
          : "?"
        : statusLabel(serverResult.status);
    return { text, badge: <LifecycleBadge state={serverResult.lifecycleState} /> };
  }
  if (outboxItem) {
    // Accepted but the authoritative list has not refreshed yet.
    return {
      text: capturedText(outboxItem, measureType),
      badge: <StatusBadge status={outboxItem.status} />,
    };
  }
  return null;
}

/** What this device last said about the entry, in the row's own clothes. */
function capturedText(item: OutboxItem, measureType: MeasureType): string {
  const payload = item.payload as ResultCorrectPayload;
  if (payload.status) return statusLabel(payload.status);
  if (payload.value != null) return formatCapturedValue(measureType, payload.value);
  if (payload.attempts) return seriesText(payload.attempts, measureType);
  return "?";
}

/** A queued series read as the server will derive it: the best valid mark, else NM. */
function seriesText(attempts: readonly (AttemptCorrection | null)[], measureType: MeasureType) {
  const marks = attempts
    .map((a) => (a && !a.foul && a.value ? Number(a.value) : NaN))
    .filter((n) => Number.isFinite(n));
  if (marks.length === 0) return attempts.some((a) => a?.foul) ? statusLabel("Nm") : "?";
  const best = measureType === "Time" ? Math.min(...marks) : Math.max(...marks);
  return formatMeasure(measureType, best);
}

/** A queued correction's series, which the strip and the next cell edit read over the server's. */
export function queuedSeries(item: OutboxItem | undefined): (Attempt | null)[] | undefined {
  if (!item || !outboxHoldsDisplay(item) || item.kind !== RESULT_CORRECT_KIND) return undefined;
  return (item.payload as ResultCorrectPayload).attempts?.map(
    (a) => a && { foul: a.foul, value: a.foul || !a.value ? null : Number(a.value) },
  );
}
