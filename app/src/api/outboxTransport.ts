import type { OutboxItem } from "../db/db";
import { outcomeFromStatus, type SyncOutcome, type SyncResponse, type Transport } from "../sync/syncWorker";
import { getDeviceId } from "../auth/deviceId";
import { apiUrl, devAuthHeaders } from "./client";
import { CONFLICT_CODES, RETRYABLE_CODES, errorCodeOf } from "./errorCodes";
import { narrowAck } from "../sync/ackShape";
import type { AttemptCorrection, JumpCorrection, MeasureType, ResultStatus } from "./types";

// Every kind but `result.correct` sends the item id as the Idempotency-Key and the id it mints;
// a correction's replay safety is `expectedRevision` instead.

export const RESULT_SUBMIT_KIND = "result.submit";

export const RESULT_CORRECT_KIND = "result.correct";

export const RESULT_ATTEMPT_KIND = "result.attempt";

export const RESULT_JUMP_KIND = "result.jump";

export const ENTRY_CREATE_KIND = "entry.create";

export const TEAM_CREATE_KIND = "team.create";

export const ADJUSTMENT_CREATE_KIND = "adjustment.create";

export const RACE_CREATE_KIND = "race.create";

export const ENTRY_ASSIGN_KIND = "entry.assign";

/** One server operation and one audit record, never a client batch of N assignments. */
export const RACE_MATERIALISE_KIND = "race.materialise";

/** Capture-time actor: a drain after a handover is never attributed to the session user. */
export const CAPTURED_ACTOR_HEADER = "X-Captured-Actor";
export const CAPTURED_ACTOR_NAME_HEADER = "X-Captured-Actor-Name";

/** Same casing as `ResultStatus` on reads; the request body wants capitals (`toWireStatus`). */
export type NonOkResultStatus = Exclude<ResultStatus, "Ok">;

export interface ResultCapturePayload {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  /** Raw official input ("mm:ss.ff" / "4.32"); null for a status. */
  value: string | null;
  status: NonOkResultStatus | null;
  /**
   * Never sent, and dropped once accepted. An item queued by an older build has no
   * `measureType` and reads as `Time`.
   */
  display?: { entryDisplayName: string; measureType?: MeasureType };
}

export type ResultSubmitPayload = ResultCapturePayload;

export type JumpMark = "O" | "X" | "-";

export interface ResultCorrectPayload extends ResultCapturePayload {
  resultId: string;
  /**
   * The whole series in ordinal order on an attempts event, the edited cell replaced; the server
   * re-derives the best mark and status from it.
   */
  attempts?: (AttemptCorrection | null)[];
  /** The whole card, heights ascending, on a Height event. */
  jumps?: JumpCorrection[];
  reason: string;
}

export interface ResultAttemptPayload {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  /** 1-based, within the definition's attempt count. */
  ordinal: number;
  value: string | null;
  foul: boolean;
  /** Absent only on an item queued before the outbox read it. */
  display?: { entryDisplayName: string; measureType: MeasureType; attemptCount?: number };
}

export interface ResultJumpPayload {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  height: number;
  mark: JumpMark;
  display?: { entryDisplayName: string; measureType: MeasureType };
}

export interface EntryCreatePayload {
  carnivalId: string;
  carnivalEventId: string;
  studentId: string;
  /** The heat the marshal was standing at; the app always names one. */
  raceId?: string | null;
  display?: {
    displayName: string;
    yearLevel: number;
    houseCode: string;
    ageGroup: string;
  };
}

export interface TeamCreatePayload {
  carnivalId: string;
  carnivalEventId: string;
  houseCode: string;
  /** The relay's swimmers in LEG ORDER — the server reads the order, not a map. */
  studentIds: string[];
  raceId?: string | null;
  /** `ageGroup` is the event's division, which is what the server snapshots on a team entry. */
  display?: {
    ageGroup: string;
    legs: { displayName: string; yearLevel: number; stroke: string | null }[];
  };
}

export interface AdjustmentCreatePayload {
  carnivalId: string;
  houseCode: string;
  points: number;
  category: string;
  reason: string;
}

export interface RaceCreatePayload {
  carnivalId: string;
  /** null/omitted = not on the program. */
  scheduleSlotId?: string | null;
  sortOrder?: number | null;
  label?: string | null;
}

export interface RaceMaterialisePayload {
  carnivalId: string;
  eventId: string;
  scheduleSlotId?: string | null;
  label?: string | null;
  display?: { title: string };
}

export interface EntryAssignPayload {
  carnivalId: string;
  carnivalEventId: string;
  entryId: string;
  /** null clears the placement (with lane; heat too unless explicit). */
  raceId: string | null;
  lane: number | null;
  /** Explicit heat for the genuinely odd case; null = derive from the race. */
  heat: number | null;
  /** `raceTitle` is absent on items queued by an older build. */
  display?: { entryDisplayName: string; raceTitle?: string };
}

/** Items queued by an older build stored the capitals directly; they pass through untouched. */
function toWireStatus(status: NonOkResultStatus | null): string | null {
  return status === null ? null : status.toUpperCase();
}

/**
 * A status alone cannot tell a retryable 409 (event not open yet) from a conflict for a person
 * (two officials recorded one swimmer); the machine code can. No code: the status rule decides.
 */
export function classifyResponse(status: number, body: unknown): SyncOutcome {
  const byStatus = outcomeFromStatus(status);
  if (byStatus === "accepted" || byStatus === "pause") return byStatus;
  const code = errorCodeOf(body);
  if (code === undefined) return byStatus;
  if (RETRYABLE_CODES.has(code)) return "retry";
  if (CONFLICT_CODES.has(code)) return "conflict";
  return byStatus;
}

/**
 * ENTRY_NOT_FOUND at drain time is dependency ordering: the `entry.create` it names may be parked
 * on this device and still recoverable, and `rejected` is terminal.
 */
function keepEntryRecoverable(classified: SyncResponse): SyncResponse {
  if (classified.outcome === "rejected" && errorCodeOf(classified.body) === "ENTRY_NOT_FOUND")
    return { ...classified, outcome: "conflict" };
  return classified;
}

async function toSyncResponse(kind: string, response: Response): Promise<SyncResponse> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }
  // The verdict is read off the FULL body; only what the row KEEPS is narrowed.
  const outcome = classifyResponse(response.status, body);
  const retryAfterMs = response.status === 429 ? retryAfterMsOf(response) : undefined;
  return {
    status: response.status,
    body: narrowAck(kind, response.status, body),
    outcome,
    ...(retryAfterMs !== undefined ? { retryAfterMs } : {}),
  };
}

function retryAfterMsOf(response: Response): number | undefined {
  const seconds = Number(response.headers?.get("Retry-After"));
  return Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : undefined;
}

export function createOutboxTransport(fetchImpl: typeof fetch = fetch): Transport {
  return async (item: OutboxItem): Promise<SyncResponse> => {
    if (item.kind === RESULT_SUBMIT_KIND) return submitResult(item, fetchImpl);
    if (item.kind === RESULT_CORRECT_KIND) return correctResult(item, fetchImpl);
    if (item.kind === RESULT_ATTEMPT_KIND) return submitAttempt(item, fetchImpl);
    if (item.kind === RESULT_JUMP_KIND) return submitJump(item, fetchImpl);
    if (item.kind === ENTRY_CREATE_KIND) return createEntry(item, fetchImpl);
    if (item.kind === TEAM_CREATE_KIND) return createTeam(item, fetchImpl);
    if (item.kind === ADJUSTMENT_CREATE_KIND) return createAdjustment(item, fetchImpl);
    if (item.kind === RACE_CREATE_KIND) return createRace(item, fetchImpl);
    if (item.kind === RACE_MATERIALISE_KIND) return materialiseRace(item, fetchImpl);
    if (item.kind === ENTRY_ASSIGN_KIND) return assignEntry(item, fetchImpl);
    // An unknown kind is a client bug: refuse locally (kept for audit), never retry forever.
    return {
      status: 400,
      body: { error: "UNKNOWN_OUTBOX_KIND", detail: `Unknown outbox kind '${item.kind}'.` },
      outcome: "rejected",
    };
  };
}

function eventRoute(payload: { carnivalId: string; carnivalEventId: string }) {
  return `/carnivals/${payload.carnivalId}/events/${payload.carnivalEventId}` as const;
}

async function submitResult(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as ResultSubmitPayload;
  const url = apiUrl(`${eventRoute(payload)}/entries/${payload.entryId}/results`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      // URI-encoded: header values must stay ASCII-safe.
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      value: payload.value,
      status: toWireStatus(payload.status),
      recordedAtUtc: item.capturedAt,
    }),
  });
  return keepEntryRecoverable(await toSyncResponse(item.kind, response));
}

async function correctResult(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as ResultCorrectPayload;
  const url = apiUrl(`/results/${payload.resultId}/correct`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      // The revision the official saw when typing, never a drain-time re-read: a replay lands on a
      // bumped revision and comes back 409 instead of applying twice.
      expectedRevision: item.baseVersion ?? 0,
      value: payload.value,
      status: toWireStatus(payload.status),
      // A bare value is refused on an attempts event, so the series rides the correction.
      ...(payload.attempts ? { attempts: payload.attempts } : {}),
      ...(payload.jumps ? { jumps: payload.jumps } : {}),
      reason: payload.reason,
    }),
  });
  return toSyncResponse(item.kind, response);
}

async function submitAttempt(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as ResultAttemptPayload;
  const url = apiUrl(
    `${eventRoute(payload)}/entries/${payload.entryId}/attempts/${payload.ordinal}`,
  );
  const response = await fetchImpl(url, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      value: payload.value,
      foul: payload.foul,
      recordedAtUtc: item.capturedAt,
    }),
  });
  return keepEntryRecoverable(await toSyncResponse(item.kind, response));
}

async function submitJump(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  // The server compares ladder heights by decimal value, so 1.2 and 1.20 name one height.
  const payload = item.payload as ResultJumpPayload;
  const url = apiUrl(
    `${eventRoute(payload)}/entries/${payload.entryId}/jumps/${String(payload.height)}`,
  );
  const response = await fetchImpl(url, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({ mark: payload.mark, recordedAtUtc: item.capturedAt }),
  });
  return keepEntryRecoverable(await toSyncResponse(item.kind, response));
}

async function createEntry(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as EntryCreatePayload;
  const url = apiUrl(`${eventRoute(payload)}/entries`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({ studentId: payload.studentId, raceId: payload.raceId ?? null }),
  });
  return toSyncResponse(item.kind, response);
}

async function createTeam(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as TeamCreatePayload;
  const url = apiUrl(`${eventRoute(payload)}/team-entries`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      houseCode: payload.houseCode,
      studentIds: payload.studentIds,
      raceId: payload.raceId ?? null,
    }),
  });
  return toSyncResponse(item.kind, response);
}

async function createRace(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as RaceCreatePayload;
  const url = apiUrl(`/carnivals/${payload.carnivalId}/races`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      scheduleSlotId: payload.scheduleSlotId ?? null,
      sortOrder: payload.sortOrder ?? null,
      label: payload.label ?? null,
    }),
  });
  return toSyncResponse(item.kind, response);
}

/**
 * Promoted to `conflict` so the marshal's intent waits for a person. Scoped to this kind: a
 * result.submit against a deleted event is a different judgement.
 */
const MATERIALISE_RECOVERABLE_CODES: ReadonlySet<string> = new Set([
  "CARNIVAL_NOT_FOUND",
  "EVENT_NOT_FOUND",
]);

async function materialiseRace(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as RaceMaterialisePayload;
  const url = apiUrl(`/carnivals/${payload.carnivalId}/races/materialise`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      eventId: payload.eventId,
      scheduleSlotId: payload.scheduleSlotId ?? null,
      label: payload.label ?? null,
    }),
  });
  const classified = await toSyncResponse(item.kind, response);
  const code = errorCodeOf(classified.body);
  if (
    classified.outcome === "rejected" &&
    code !== undefined &&
    MATERIALISE_RECOVERABLE_CODES.has(code)
  )
    return { ...classified, outcome: "conflict" };
  return classified;
}

async function assignEntry(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as EntryAssignPayload;
  const url = apiUrl(`${eventRoute(payload)}/entries/${payload.entryId}/placement`);
  const response = await fetchImpl(url, {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      raceId: payload.raceId,
      lane: payload.lane,
      heat: payload.heat,
    }),
  });
  return keepEntryRecoverable(await toSyncResponse(item.kind, response));
}

async function createAdjustment(item: OutboxItem, fetchImpl: typeof fetch): Promise<SyncResponse> {
  const payload = item.payload as AdjustmentCreatePayload;
  const url = apiUrl(`/carnivals/${payload.carnivalId}/adjustments`);
  const response = await fetchImpl(url, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": item.id,
      "X-Device-Id": getDeviceId(),
      [CAPTURED_ACTOR_HEADER]: item.actorSubject,
      ...(item.actorDisplayName
        ? { [CAPTURED_ACTOR_NAME_HEADER]: encodeURIComponent(item.actorDisplayName) }
        : {}),
      ...devAuthHeaders(),
    },
    credentials: "include",
    body: JSON.stringify({
      houseCode: payload.houseCode,
      points: payload.points,
      category: payload.category,
      reason: payload.reason,
    }),
  });
  return toSyncResponse(item.kind, response);
}
