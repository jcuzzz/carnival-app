/**
 * Only the codes the app branches on; `detail` is free copy and nothing branches on it. Not
 * generated: a new server code must hit the fallback, not break the build.
 */

export const ErrorCodes = {
  AuthenticationRequired: "AUTHENTICATION_REQUIRED",
  ConcurrencyConflict: "CONCURRENCY_CONFLICT",
} as const;

/** The same payload succeeds once server state moves on, and the server keeps the key unburned. */
export const RETRYABLE_CODES: ReadonlySet<string> = new Set([
  "CARNIVAL_NOT_OPEN",
  "EVENT_NOT_OPEN",
  "ROSTER_SNAPSHOT_MISSING", // promoting a roster import resolves it
  "NOT_ASSIGNED_OFFICIAL", // the assignment can still be granted
  "INTERNAL_ERROR",
  "TOO_MANY_REQUESTS", // the rate limiter refuses before any idempotency key is stored
  // Ops-fixable misconfiguration: the same payload lands once it is fixed.
  "IDEMPOTENCY_BUFFERING_UNAVAILABLE",
]);

/**
 * A real capture a person can still recover. `conflict` is the only non-accepted state with a way
 * back into the queue, so a capture is `rejected` only when nothing is left to save.
 */
export const CONFLICT_CODES: ReadonlySet<string> = new Set([
  ErrorCodes.ConcurrencyConflict,
  "RESULT_EXISTS",
  "RESULT_FINALISED",
  "ENTRY_EXISTS",
  "ENTRY_IN_OTHER_DIVISION",
  "RACE_NOT_IN_EVENT",
  "TEAM_EXISTS",
  "SUBMISSION_ID_CONFLICT",
  "IDEMPOTENCY_PAYLOAD_MISMATCH",
  // Validation family.
  "VALIDATION_FAILED",
  // Unreadable body: never retryable, since the same bytes never parse and a retry holds capture
  // order, so one item would block every later capture for the rest of the carnival.
  "REQUEST_INVALID",
  // The size limit is deployment config: raising it lands this exact item.
  "REQUEST_BODY_TOO_LARGE",
  "REASON_REQUIRED",
  "RESULT_VALUE_REQUIRED",
  "RESULT_VALUE_NOT_ALLOWED",
  "RESULT_VALUE_INVALID",
  "UNKNOWN_RESULT_STATUS",
  "NM_REQUIRES_FIELD_MEASURE",
  "ATTEMPTS_NOT_CONFIGURED",
  "ATTEMPT_ORDINAL_OUT_OF_RANGE",
  "ATTEMPT_ALREADY_RECORDED",
  "RESULT_STATUS_BLOCKS_ATTEMPTS",
  // The device has not learned the definition's series yet; the mark belongs in a cell.
  "ATTEMPTS_REQUIRED",
  "ATTEMPTS_SERIES_TRUNCATED",
  // No outbox kind reaches the routes that emit the next two today.
  "ATTEMPT_COUNT_FROZEN",
  "MEASURE_FROZEN",
  "LADDER_REQUIRED",
  "HEIGHT_NOT_ON_LADDER",
  "BAR_NEVER_LOWERED",
  "ATHLETE_ELIMINATED",
  "HEIGHT_ALREADY_CLEARED",
  "HEIGHT_PASSED",
  "HEIGHT_ATTEMPTS_EXHAUSTED",
  // A direct call today; listed to pre-decide the classification.
  "ATTEMPTS_PER_HEIGHT_FROZEN",
  // No outbox kind reaches the routes that emit this today; listed to pre-decide it.
  "UNKNOWN_STATUS",
  "ADJUSTMENT_POINTS_INVALID",
  "ADJUSTMENT_CATEGORY_UNKNOWN",
  "HOUSE_UNKNOWN", // the house was renamed or removed while a team or adjustment sat queued
  "AGE_GROUP_INELIGIBLE",
  "CATEGORY_INELIGIBLE",
  "STUDENT_INACTIVE",
  // The roster moved under a queued add; the capture names a real child, so a person picks who.
  "STUDENT_NOT_FOUND",
  "CAPTURED_ACTOR_INVALID",
  "WRITE_REFUSED", // a non-concurrency database refusal the server could not name more precisely
  "TEAM_SIZE_MISMATCH",
  "DUPLICATE_TEAM_MEMBER",
  "HOUSE_MISMATCH",
  // Marshalling: none of these resolves itself, and a retry would head-of-line block the device.
  "LANE_TAKEN",
  "HEAT_AMBIGUOUS",
  "ENTRY_HEAT_INVALID",
  "ENTRY_LANE_INVALID",
  // A 404: the status fallback alone would reject a real marshalling capture.
  "RACE_NOT_FOUND",
  // Deploy skew only: an older server 404s a race.create whose slot vanished, and rejecting it
  // would destroy the race that queued assignments depend on.
  "SCHEDULE_SLOT_NOT_FOUND",
]);

/** Undefined means "no code", never a decision; a pre-contract body reads as undefined. */
export function errorCodeOf(body: unknown): string | undefined {
  if (typeof body !== "object" || body === null) return undefined;
  const code = (body as { error?: unknown }).error;
  // A pre-contract body put a whole sentence in `error`; a code never has spaces.
  return typeof code === "string" && code !== "" && !code.includes(" ") ? code : undefined;
}
