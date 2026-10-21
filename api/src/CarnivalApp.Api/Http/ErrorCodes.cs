namespace CarnivalApp.Api.Http;

/// <summary>
/// The closed vocabulary of <c>error</c> codes. The offline outbox branches on the value, so a code
/// may be added but never reworded or repurposed; the <c>detail</c> sentence is free copy.
/// </summary>
public static class ErrorCodes
{
    // ---- Request shape / validation ---------------------------------------

    public const string ValidationFailed = "VALIDATION_FAILED";

    public const string ReasonRequired = "REASON_REQUIRED";

    public const string UnknownStatus = "UNKNOWN_STATUS";

    /// <summary>
    /// Model binding could not read the body. Terminal: the same bytes
    /// never parse, so a queued write is refused once; a slow or
    /// truncated body wears the same exception and stays InternalError.
    /// </summary>
    public const string RequestInvalid = "REQUEST_INVALID";

    /// <summary>
    /// Kestrel's 413. Parked, not retried: a retry halts the drain behind a body that never
    /// shrinks, and raising the deployment limit makes the queued item deliverable.
    /// </summary>
    public const string RequestBodyTooLarge = "REQUEST_BODY_TOO_LARGE";

    /// <summary>Routing's own refusals: no endpoint, wrong method, unaccepted content type.</summary>
    public const string RouteNotFound = "ROUTE_NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";

    /// <summary>
    /// The <c>X-Captured-Actor</c> header is over-long or carries control characters.
    /// </summary>
    public const string CapturedActorInvalid = "CAPTURED_ACTOR_INVALID";

    /// <summary>
    /// Bulk-data rate limit (roster upload, CSV export); no capture path is rate-limited.
    /// </summary>
    public const string TooManyRequests = "TOO_MANY_REQUESTS";

    // ---- Idempotency ------------------------------------------------------

    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";

    /// <summary>
    /// The key was already used with a different payload — a client bug, never a replay.
    /// </summary>
    public const string IdempotencyPayloadMismatch = "IDEMPOTENCY_PAYLOAD_MISMATCH";

    /// <summary>Server misconfiguration: request-body buffering is not in the pipeline.</summary>
    public const string IdempotencyBufferingUnavailable = "IDEMPOTENCY_BUFFERING_UNAVAILABLE";

    public const string SubmissionIdConflict = "SUBMISSION_ID_CONFLICT";

    // ---- Authentication / authorisation -----------------------------------

    /// <summary>The outbox pauses (never drops) on this.</summary>
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";

    public const string Forbidden = "FORBIDDEN";

    /// <summary>
    /// No longer emitted. Kept because a device may still hold an item parked against it, which the
    /// client drains as retryable; never reuse the value.
    /// </summary>
    public const string NotAssignedOfficial = "NOT_ASSIGNED_OFFICIAL";

    // ---- Carnivals --------------------------------------------------------

    public const string CarnivalNotFound = "CARNIVAL_NOT_FOUND";

    public const string CarnivalNotEditable = "CARNIVAL_NOT_EDITABLE";

    /// <summary>A capture-path write needs the carnival Open. Transient before it opens.</summary>
    public const string CarnivalNotOpen = "CARNIVAL_NOT_OPEN";

    public const string IllegalCarnivalTransition = "ILLEGAL_CARNIVAL_TRANSITION";

    public const string CarnivalFinalisationBlocked = "CARNIVAL_FINALISATION_BLOCKED";

    /// <summary>
    /// The district selection export needs a Finalised carnival; the other exports do not.
    /// </summary>
    public const string CarnivalNotFinalised = "CARNIVAL_NOT_FINALISED";

    // ---- Devices ----------------------------------------------------------

    public const string DeviceIdInvalid = "DEVICE_ID_INVALID";

    /// <summary>
    /// The route id is not the caller's own <c>X-Device-Id</c>, or the header is absent.
    /// </summary>
    public const string DeviceIdMismatch = "DEVICE_ID_MISMATCH";

    // ---- Event-definition catalogue ---------------------------------------

    public const string EventDefinitionNotFound = "EVENT_DEFINITION_NOT_FOUND";
    public const string EventDefinitionCodeExists = "EVENT_DEFINITION_CODE_EXISTS";
    public const string EventDefinitionInactive = "EVENT_DEFINITION_INACTIVE";

    // ---- Event templates --------------------------------------------------

    /// <summary>The template key is missing, malformed or unknown (client-correctable).</summary>
    public const string TemplateInvalid = "TEMPLATE_INVALID";

    /// <summary>
    /// The template store is unconfigured or the document unreadable (server-side).
    /// </summary>
    public const string TemplateUnavailable = "TEMPLATE_UNAVAILABLE";

    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";

    public const string TemplateReconcileStale = "TEMPLATE_RECONCILE_STALE";

    /// <summary>
    /// A guarded field would move under results, team entries, standings or records.
    /// </summary>
    public const string TemplateReconcileUnsafe = "TEMPLATE_RECONCILE_UNSAFE";

    // ---- Carnival events --------------------------------------------------

    public const string EventNotFound = "EVENT_NOT_FOUND";

    /// <summary>One instance per definition + age group + category + tier.</summary>
    public const string EventAlreadyScheduled = "EVENT_ALREADY_SCHEDULED";

    /// <summary>
    /// The event's status does not accept captures. Transient: it may be (re)opened.
    /// </summary>
    public const string EventNotOpen = "EVENT_NOT_OPEN";

    // No longer emitted; kept for parked items and history — never reuse the values.
    public const string EventFinalised = "EVENT_FINALISED";

    public const string EventNotClosed = "EVENT_NOT_CLOSED";

    /// <summary>The division cannot be re-based while entries carry its eligibility.</summary>
    public const string EventDivisionLocked = "EVENT_DIVISION_LOCKED";

    /// <summary>
    /// The division has entries; close it instead. Carries entryCount and resultCount.
    /// </summary>
    public const string EventHasEntries = "EVENT_HAS_ENTRIES";

    public const string AgeScopeInvalid = "AGE_SCOPE_INVALID";

    public const string IllegalEventTransition = "ILLEGAL_EVENT_TRANSITION";

    // No longer emitted; kept for parked items and history — never reuse the values.
    public const string EventFinaliseViaGate = "EVENT_FINALISE_VIA_GATE";

    public const string FinalisationBlocked = "FINALISATION_BLOCKED";

    // ---- Schedule: streams and slots --------------------------------------
    // Administrative only: the schedule is advisory, so no capture is refused because of it.

    public const string ScheduleStreamNotFound = "SCHEDULE_STREAM_NOT_FOUND";
    public const string ScheduleSlotNotFound = "SCHEDULE_SLOT_NOT_FOUND";

    public const string ScheduleStreamNameExists = "SCHEDULE_STREAM_NAME_EXISTS";

    public const string ScheduleTimesInvalid = "SCHEDULE_TIMES_INVALID";

    public const string ScheduleSlotKindMismatch = "SCHEDULE_SLOT_KIND_MISMATCH";

    // ---- Races ------------------------------------------------------------

    public const string RaceNotFound = "RACE_NOT_FOUND";

    /// <summary>Parks rather than retries: a double-booked lane never frees itself.</summary>
    public const string LaneTaken = "LANE_TAKEN";

    /// <summary>
    /// This event's entries in the race carry different heats; supply the heat explicitly.
    /// </summary>
    public const string HeatAmbiguous = "HEAT_AMBIGUOUS";

    public const string RaceNotInEvent = "RACE_NOT_IN_EVENT";

    // ---- Entries ----------------------------------------------------------

    public const string EntryNotFound = "ENTRY_NOT_FOUND";

    public const string CategoryOverrideForbidden = "CATEGORY_OVERRIDE_FORBIDDEN";
    public const string EntryExists = "ENTRY_EXISTS";

    /// <summary>
    /// The student holds an entry in a sibling division (same definition and tier). Carries the
    /// standing entry's id and division.
    /// </summary>
    public const string EntryInOtherDivision = "ENTRY_IN_OTHER_DIVISION";

    public const string EntryHasResult = "ENTRY_HAS_RESULT";

    /// <summary>
    /// The heat is the placing scope, so a nonsense heat would mint its own 1st place.
    /// </summary>
    public const string EntryHeatInvalid = "ENTRY_HEAT_INVALID";

    public const string EntryLaneInvalid = "ENTRY_LANE_INVALID";

    public const string StudentNotFound = "STUDENT_NOT_FOUND";
    public const string StudentInactive = "STUDENT_INACTIVE";

    /// <summary>Transient: promoting a roster import resolves it.</summary>
    public const string RosterSnapshotMissing = "ROSTER_SNAPSHOT_MISSING";

    public const string AgeGroupIneligible = "AGE_GROUP_INELIGIBLE";
    public const string CategoryIneligible = "CATEGORY_INELIGIBLE";

    /// <summary>
    /// A team entry's category is synthesised from the division; nothing to override.
    /// </summary>
    public const string CategoryOverrideNotApplicable = "CATEGORY_OVERRIDE_NOT_APPLICABLE";

    public const string NotATeamEvent = "NOT_A_TEAM_EVENT";

    public const string NotAnIndividualEvent = "NOT_AN_INDIVIDUAL_EVENT";

    public const string HouseUnknown = "HOUSE_UNKNOWN";

    public const string TeamSizeMismatch = "TEAM_SIZE_MISMATCH";
    public const string DuplicateTeamMember = "DUPLICATE_TEAM_MEMBER";
    public const string TeamExists = "TEAM_EXISTS";

    public const string HouseMismatch = "HOUSE_MISMATCH";

    // ---- Results ----------------------------------------------------------

    public const string ResultNotFound = "RESULT_NOT_FOUND";

    public const string ResultExists = "RESULT_EXISTS";

    public const string ResultValueRequired = "RESULT_VALUE_REQUIRED";

    /// <summary>A DNS/DNF/DQ result cannot carry a measured value.</summary>
    public const string ResultValueNotAllowed = "RESULT_VALUE_NOT_ALLOWED";

    public const string ResultValueInvalid = "RESULT_VALUE_INVALID";

    public const string UnknownResultStatus = "UNKNOWN_RESULT_STATUS";

    /// <summary>NM on a Time event; a runner who starts and does not finish is DNF.</summary>
    public const string NmRequiresFieldMeasure = "NM_REQUIRES_FIELD_MEASURE";

    /// <summary>
    /// Correcting a Final result — the only state that refuses one. 409, not retryable.
    /// </summary>
    public const string ResultFinalised = "RESULT_FINALISED";

    /// <summary>
    /// Concurrency collision, never last-write-wins. Carries currentRevision where known.
    /// </summary>
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    /// <summary>
    /// The database refused the save (a constraint or column limit, not
    /// concurrency). 409 rather than 500 because a retryable 500 stalls
    /// the device's drain behind a payload that can never succeed.
    /// </summary>
    public const string WriteRefused = "WRITE_REFUSED";

    // ---- Field attempts ---------------------------------------------------
    // All 409: a 409 parks a queued item for a person on any client build, where a 400 would put a
    // real captured mark on the one-way rejected pile.

    /// <summary>
    /// An attempt on a single-measure definition, which is captured as a single mark.
    /// </summary>
    public const string AttemptsNotConfigured = "ATTEMPTS_NOT_CONFIGURED";

    public const string AttemptOrdinalOutOfRange = "ATTEMPT_ORDINAL_OUT_OF_RANGE";

    /// <summary>Re-entering a recorded ordinal is a correction, which carries a reason.</summary>
    public const string AttemptAlreadyRecorded = "ATTEMPT_ALREADY_RECORDED";

    /// <summary>The result carries a typed DNS/DNF/DQ, which clears the series.</summary>
    public const string ResultStatusBlocksAttempts = "RESULT_STATUS_BLOCKS_ATTEMPTS";

    /// <summary>
    /// A best mark (or typed NM) on an attempts event, where both derive from the series.
    /// </summary>
    public const string AttemptsRequired = "ATTEMPTS_REQUIRED";

    /// <summary>
    /// A corrected series is shorter than the stored one, so a recorded ordinal would vanish.
    /// </summary>
    public const string AttemptsSeriesTruncated = "ATTEMPTS_SERIES_TRUNCATED";

    /// <summary>
    /// The attempt count cannot change once results exist; re-sending the same value is fine.
    /// </summary>
    public const string AttemptCountFrozen = "ATTEMPT_COUNT_FROZEN";

    /// <summary>
    /// The measure type or units cannot change once results exist; re-sending the same is fine.
    /// </summary>
    public const string MeasureFrozen = "MEASURE_FROZEN";

    /// <summary>
    /// A definition's record categories cannot change once a school record cites it.
    /// </summary>
    public const string RecordCategoriesFrozen = "RECORD_CATEGORIES_FROZEN";

    // ---- High jump: the heights ladder ------------------------------------
    // Capture-path codes are 409 so a queued mark parks for a person.

    /// <summary>
    /// A Height event takes jumps at a bar height, not a series or typed best; also answers a jump
    /// or raise on an event not measured as Height.
    /// </summary>
    public const string LadderRequired = "LADDER_REQUIRED";

    /// <summary>
    /// Transient in substance — raising the bar lands the same mark — but a person raises it.
    /// </summary>
    public const string HeightNotOnLadder = "HEIGHT_NOT_ON_LADDER";

    /// <summary>
    /// Below a height the athlete has jumped at, or below the division's last bar.
    /// </summary>
    public const string BarNeverLowered = "BAR_NEVER_LOWERED";

    public const string AthleteEliminated = "ATHLETE_ELIMINATED";

    public const string HeightAlreadyCleared = "HEIGHT_ALREADY_CLEARED";

    /// <summary>A pass forfeits the rest of that height.</summary>
    public const string HeightPassed = "HEIGHT_PASSED";

    public const string HeightAttemptsExhausted = "HEIGHT_ATTEMPTS_EXHAUSTED";

    /// <summary>
    /// A mark has landed in the division, so its attempts per height no longer change.
    /// </summary>
    public const string AttemptsPerHeightFrozen = "ATTEMPTS_PER_HEIGHT_FROZEN";

    public const string LadderRequiresHeightMeasure = "LADDER_REQUIRES_HEIGHT_MEASURE";

    public const string LadderExcludesAttempts = "LADDER_EXCLUDES_ATTEMPTS";

    // ---- School records ---------------------------------------------------

    public const string SchoolRecordNotFound = "SCHOOL_RECORD_NOT_FOUND";

    /// <summary>Voiding is one-way; a second void means the caller holds a stale list.</summary>
    public const string SchoolRecordAlreadyVoided = "SCHOOL_RECORD_ALREADY_VOIDED";

    // ---- Scoring settings + house adjustments -----------------------------

    public const string ScoringRulesInvalid = "SCORING_RULES_INVALID";
    public const string AdjustmentNotFound = "ADJUSTMENT_NOT_FOUND";
    public const string AdjustmentPointsInvalid = "ADJUSTMENT_POINTS_INVALID";
    public const string AdjustmentCategoryUnknown = "ADJUSTMENT_CATEGORY_UNKNOWN";

    public const string LaneCountInvalid = "LANE_COUNT_INVALID";

    /// <summary>"Full" is the lane count and none is assumed.</summary>
    public const string AutoFillLanesRequiresLaneCount = "AUTO_FILL_LANES_REQUIRES_LANE_COUNT";

    // ---- Roster import pipeline -------------------------------------------

    public const string ImportFileRequired = "IMPORT_FILE_REQUIRED";

    /// <summary>The file name or content type is outside the CSV allowlist.</summary>
    public const string ImportFileTypeUnsupported = "IMPORT_FILE_TYPE_UNSUPPORTED";

    /// <summary>Not a year, or more than one year from the school's current year.</summary>
    public const string ImportCompetitionYearInvalid = "IMPORT_COMPETITION_YEAR_INVALID";

    public const string ImportMappingUnavailable = "IMPORT_MAPPING_UNAVAILABLE";
    public const string ImportBatchNotFound = "IMPORT_BATCH_NOT_FOUND";

    public const string ImportBatchWrongState = "IMPORT_BATCH_WRONG_STATE";

    public const string ImportBatchUnreadable = "IMPORT_BATCH_UNREADABLE";

    /// <summary>The batch was promoted or rejected and its staged rows were discarded.</summary>
    public const string ImportRowsPurged = "IMPORT_ROWS_PURGED";

    /// <summary>The batch was never promoted and its staged rows aged out; upload the file again.</summary>
    public const string ImportBatchExpired = "IMPORT_BATCH_EXPIRED";

    // ---- School settings --------------------------------------------------
    // A person is in front of the settings screen, so these refuse the save where startup defaults.

    public const string NameRequired = "NAME_REQUIRED";
    public const string NameTooLong = "NAME_TOO_LONG";

    public const string BrandColourInvalid = "BRAND_COLOUR_INVALID";

    /// <summary>Neither black nor white reaches 4.5:1 on it.</summary>
    public const string BrandColourUnreadable = "BRAND_COLOUR_UNREADABLE";

    public const string HousesRequired = "HOUSES_REQUIRED";
    public const string HouseCodeRequired = "HOUSE_CODE_REQUIRED";
    public const string HouseCodeTooLong = "HOUSE_CODE_TOO_LONG";
    public const string HouseCodeDuplicate = "HOUSE_CODE_DUPLICATE";
    public const string HouseNameTooLong = "HOUSE_NAME_TOO_LONG";
    public const string HouseColourInvalid = "HOUSE_COLOUR_INVALID";
    public const string HouseColourDuplicate = "HOUSE_COLOUR_DUPLICATE";

    /// <summary>
    /// Removing or renaming the house would orphan roster, entry, leg or adjustment rows.
    /// </summary>
    public const string HouseInUse = "HOUSE_IN_USE";

    public const string SettingsStale = "SETTINGS_STALE";

    // ---- In-app role assignment -------------------------------------------

    public const string RoleAssignmentNotFound = "ROLE_ASSIGNMENT_NOT_FOUND";

    public const string RoleAssignmentDuplicate = "ROLE_ASSIGNMENT_DUPLICATE";

    public const string RoleSelfRevoke = "ROLE_SELF_REVOKE";

    public const string RoleUpnInvalid = "ROLE_UPN_INVALID";
    public const string RoleUnknown = "ROLE_UNKNOWN";
    public const string RoleReasonRequired = "ROLE_REASON_REQUIRED";

    // ---- Public demo (ADR-0061) -------------------------------------------

    public const string DemoAtCapacity = "DEMO_AT_CAPACITY";
    public const string DemoSessionRequired = "DEMO_SESSION_REQUIRED";
    public const string DemoImportsDisabled = "DEMO_IMPORTS_DISABLED";

    // ---- Fallback ---------------------------------------------------------

    /// <summary>Unhandled server fault; the detail is deliberately non-leaking.</summary>
    public const string InternalError = "INTERNAL_ERROR";
}
