import { ApiError } from "../api/client";
import { ErrorCodes } from "../api/errorCodes";
import { correctResult } from "../api/resources";
import {
  RESULT_CORRECT_KIND,
  type NonOkResultStatus,
  type ResultCorrectPayload,
} from "../api/outboxTransport";
import { syncWorker } from "../api/syncSetup";
import type {
  AttemptCorrection,
  CorrectResultRequest,
  JumpCorrection,
  MeasureType,
  ResultDto,
} from "../api/types";
import type { OutboxItem } from "../db/db";
import { enqueue } from "../sync/outbox";

/**
 * Online the correction is direct, so a stale-revision clash reaches the official at the deck;
 * only a transport failure falls through to the queue, since an ApiError means the server answered.
 */

export interface CorrectionInput {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  entryDisplayName: string;
  resultId: string;
  /** `currentRevision` as displayed: the concurrency token. */
  baseVersion: number;
  value: string | null;
  status: NonOkResultStatus | null;
  measureType: MeasureType;
  /** The whole series, edited cell replaced; the server re-derives the best mark from it. */
  attempts?: (AttemptCorrection | null)[];
  /** The whole card, heights ascending; the server replays it mark by mark. */
  jumps?: JumpCorrection[];
  reason: string;
  actorSubject: string;
  actorDisplayName?: string;
}

export type CorrectionOutcome =
  | { kind: "corrected"; result: ResultDto }
  | { kind: "queued"; item: OutboxItem }
  | { kind: "stale"; currentRevision: number | undefined };

export async function submitCorrection(input: CorrectionInput): Promise<CorrectionOutcome> {
  if (navigator.onLine) {
    try {
      const result = await correctResult(input.resultId, {
        expectedRevision: input.baseVersion,
        value: input.value,
        status: input.status === null ? null : input.status.toUpperCase(),
        // A null element is an untaken ordinal, which the generated type cannot say.
        ...(input.attempts
          ? { attempts: input.attempts as NonNullable<CorrectResultRequest["attempts"]> }
          : {}),
        ...(input.jumps ? { jumps: input.jumps } : {}),
        reason: input.reason,
      });
      return { kind: "corrected", result };
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.code === ErrorCodes.ConcurrencyConflict) {
          const revision = err.body?.currentRevision;
          return { kind: "stale", currentRevision: typeof revision === "number" ? revision : undefined };
        }
        throw err;
      }
      // Transport failure (the pool's usual "online but unreachable"): queue it.
    }
  }
  return { kind: "queued", item: await enqueueCorrection(input) };
}

async function enqueueCorrection(input: CorrectionInput): Promise<OutboxItem> {
  const payload: ResultCorrectPayload = {
    carnivalId: input.carnivalId,
    carnivalEventId: input.carnivalEventId,
    entryId: input.entryId,
    resultId: input.resultId,
    value: input.value,
    status: input.status,
    ...(input.attempts ? { attempts: input.attempts } : {}),
    ...(input.jumps ? { jumps: input.jumps } : {}),
    reason: input.reason,
    display: { entryDisplayName: input.entryDisplayName, measureType: input.measureType },
  };
  const item = await enqueue({
    kind: RESULT_CORRECT_KIND,
    payload,
    actorSubject: input.actorSubject,
    ...(input.actorDisplayName !== undefined ? { actorDisplayName: input.actorDisplayName } : {}),
    baseVersion: input.baseVersion,
  });
  void syncWorker.syncNow();
  return item;
}
