/**
 * App-facing names for the generated wire schemas (`npm run generate:types`); never re-describe a
 * field here. Hand-written types exist only where the OpenAPI document cannot say the truth.
 */

import type { components } from "./generated";

type Schemas = components["schemas"];

// ---- Enumerations -----------------------------------------------------

export type CompetitionCategory = Schemas["CompetitionCategory"];
export type CarnivalType = Schemas["CarnivalType"];
export type CarnivalStatus = Schemas["CarnivalStatus"];
export type CarnivalEventStatus = Schemas["CarnivalEventStatus"];
export type MeasureType = Schemas["MeasureType"];
export type ResultStatus = Schemas["ResultStatus"];
export type ResultLifecycleState = Schemas["ResultLifecycleState"];
export type EventFormat = Schemas["EventFormat"];
export type EventTier = Schemas["EventTier"];

/** The server resolves retired role aliases before anything is stored or returned. */
export type PlatformRole = Schemas["PlatformRole"];

// ---- Carnivals, events, entries, results ------------------------------

export type Carnival = Schemas["CarnivalDto"];
export type EventDefinition = Schemas["EventDefinitionDto"];
export type CarnivalEvent = Schemas["CarnivalEventDto"];
export type Official = Schemas["OfficialDto"];
export type RelayLegDto = Schemas["RelayLegDto"];
export type EligibleStudent = Schemas["EligibleStudentDto"];

export type EligibleStudentPage = Schemas["EligibleStudentPage"];

export type Entry = Schemas["EntryDto"];
export type ResultDto = Schemas["ResultDto"];

/**
 * A NULL element is an untaken ordinal, which the OpenAPI document cannot express, so both arrive
 * typed non-nullable.
 */
export type Attempt = Schemas["AttemptDto"];
export type AttemptCorrection = Schemas["AttemptCorrection"];

export type Jump = Schemas["JumpDto"];
export type JumpCorrection = Schemas["JumpCorrection"];
export type Ladder = Schemas["LadderDto"];
export type LadderRequest = Schemas["LadderRequest"];

export type AttemptsPerHeight = 2 | 3;

// ---- Reports ----------------------------------------------------------

export type ResultsReport = Schemas["ResultsReport"];
export type ResultRow = Schemas["ResultRow"];

// ---- School records ---------------------------------------------------

export type SchoolRecord = Schemas["SchoolRecordDto"];

// ---- Carnival program -------------------------------------------------

/** Advisory only: nothing may gate on it, and an event in no slot is fully usable. */
export type Schedule = Schemas["ScheduleDto"];
export type ScheduleStream = Schemas["ScheduleStreamDto"];
export type ScheduleSlot = Schemas["ScheduleSlotDto"];
export type ScheduleSlotKind = Schemas["ScheduleSlotKind"];

// ---- Races and lanes --------------------------------------------------

/** The only place the client learns an entry's race: `EntryDto` carries heat and lane only. */
export type Race = Schemas["RaceDto"];
export type RaceEntry = Schemas["RaceEntryDto"];

// ---- Imports ----------------------------------------------------------

export type ImportBatchStatus = Schemas["ImportBatchStatus"];
export type RowDisposition = Schemas["RowDisposition"];
export type IssueSeverity = Schemas["IssueSeverity"];
export type RowIssue = Schemas["RowIssue"];
export type ReconciliationSummary = Schemas["ReconciliationSummary"];
export type ImportBatchSummary = Schemas["ImportBatchSummary"];
export type StageResponse = Schemas["StageResponse"];
export type ImportDefaults = Schemas["ImportDefaults"];

/**
 * A staged row is a settable class, so the document marks every property optional although the
 * server always serialises them all.
 */
export type StagedRow = Required<Schemas["StagedRow"]>;

export type ReconciliationReport = Omit<Schemas["ReconciliationReport"], "rows"> & {
  rows: StagedRow[];
};

export type ValidationReport = Schemas["ValidationReport"];
export type ValidationDocument = Schemas["ValidationDocument"];
export type RowValidation = Schemas["RowValidation"];

// ---- Scoring: standings, settings, adjustments ------------------------

export type HouseStanding = Schemas["HouseStanding"];
export type Standings = Schemas["Standings"];
export type ScoringRules = Schemas["ScoringRulesDto"];
export type CarnivalSettings = Schemas["CarnivalSettingsDto"];

export type DivisionProfile = Schemas["DivisionProfileDto"];
export type HousePointsAdjustment = Schemas["AdjustmentDto"];

// ---- Request bodies ---------------------------------------------------

/**
 * Request records have no per-field defaults, so the document types optional fields as
 * required-but-nullable; a missing JSON member binds to null, so the app omits them.
 */
type Omittable<T> = { [K in keyof T as null extends T[K] ? K : never]?: T[K] } & {
  [K in keyof T as null extends T[K] ? never : K]: T[K];
};

export type CarnivalRequest = Omittable<Schemas["CarnivalRequest"]>;
export type DefinitionRequest = Omittable<Schemas["DefinitionRequest"]>;
export type ScheduleEventRequest = Omittable<Schemas["ScheduleEventRequest"]>;
export type CorrectResultRequest = Omittable<Schemas["CorrectResultRequest"]>;
export type ScoringSettingsRequest = Omittable<Schemas["ScoringSettingsRequest"]>;

export type GenerateDivisionsRequest = Omittable<Schemas["GenerateDivisionsRequest"]>;
export type GenerateDivisionRequest = Schemas["GenerateDivisionRequest"];
export type GenerateDivisionsResponse = Schemas["GenerateDivisionsResponse"];

/** `SlotRequest` is a replace, not a patch: an omitted time or category clears it. */
export type StreamRequest = Omittable<Schemas["StreamRequest"]>;
export type SlotRequest = Omittable<Schemas["SlotRequest"]>;
export type PlaceEventRequest = Omittable<Schemas["PlaceEventRequest"]>;

/** `scoring` is spelled out because Omittable does not recurse into members. */
export type SettingsRequest = Omittable<Omit<Schemas["SettingsRequest"], "scoring">> & {
  scoring?: ScoringSettingsRequest | null;
};

export type OfficialsResponse = Schemas["OfficialsResponse"];

export type DeviceStatus = Schemas["DeviceSyncStatusDto"];
export type DeviceStatusReport = Omittable<Schemas["DeviceSyncStatusRequest"]>;

/** Carries the server clock: every panel state is an age, which a skewed browser would warp. */
export type DeviceFleet = Schemas["DeviceFleetDto"];

// ---- School branding --------------------------------------------------

/** Anonymous-safe: the sign-in screen is painted before anyone has signed in. */
export type Branding = Schemas["BrandingResponse"];

/** `colour` is null whenever the house renders text-only. */
export type HouseMarkDto = Schemas["HouseMark"];

// ---- School settings --------------------------------------------------

export type SchoolSettings = Schemas["SchoolSettingsResponse"];

/** Not `Omittable`: a full replacement, so an omitted `houses` would read as "no houses". */
export type SchoolSettingsRequest = Schemas["SchoolSettingsRequest"];
export type HouseSettingRequest = Schemas["HouseSettingRequest"];

// ---- In-app role assignment -------------------------------------------

/** A bootstrap administrator (`bootstrap: true`) has no `id` and no row to revoke. */
export type RoleAssignment = Schemas["RoleAssignmentDto"];
export type GrantRoleRequest = Schemas["GrantRoleRequest"];

// ---- Template reconciliation ------------------------------------------

export type TemplateDiff = Schemas["TemplateDiff"];
export type TemplateDiffRow = Schemas["TemplateDiffRow"];
export type TemplateDiffKind = Schemas["TemplateDiffKind"];
export type TemplateFieldDiff = Schemas["TemplateFieldDiff"];
export type TemplateSummary = Schemas["TemplateSummary"];
export type TemplateReconcileOutcome = Schemas["TemplateReconcileOutcome"];

export type ApplyTemplateResult = Schemas["ApplicationResult"];

export type OpenScheduledResult = Schemas["OpenScheduledResponse"];

// ---- Dashboard progress -----------------------------------------------

export type CarnivalProgress = Schemas["CarnivalProgress"];
export type ProgressTotals = Schemas["ProgressTotals"];
export type EventProgress = Schemas["EventProgress"];
export type HouseParticipation = Schemas["HouseParticipation"];
export type RecordToday = Schemas["RecordToday"];

// ---- Errors -----------------------------------------------------------

/**
 * Widens the generated body: extension data (`currentRevision`, ...) has no schema, and a
 * response from before the contract may lack any field or carry RFC7807's `title`.
 */
export type ApiErrorBody = Partial<Schemas["ApiErrorBody"]> & {
  title?: string;
  [key: string]: unknown;
};
