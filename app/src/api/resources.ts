import { api, apiFile, type ApiFile, type Query } from "./client";
import type {
  ApplyTemplateResult,
  AttemptsPerHeight,
  Branding,
  Carnival,
  CarnivalEvent,
  CarnivalEventStatus,
  CarnivalProgress,
  CarnivalRequest,
  CarnivalSettings,
  CarnivalStatus,
  CorrectResultRequest,
  DefinitionRequest,
  DeviceFleet,
  DeviceStatus,
  DeviceStatusReport,
  EligibleStudent,
  EligibleStudentPage,
  Entry,
  EventDefinition,
  GenerateDivisionsRequest,
  GenerateDivisionsResponse,
  HouseMarkDto,
  HousePointsAdjustment,
  ImportBatchSummary,
  ImportDefaults,
  OfficialsResponse,
  OpenScheduledResult,
  PlaceEventRequest,
  Race,
  ReconciliationReport,
  ResultDto,
  ResultsReport,
  Schedule,
  ScheduleEventRequest,
  ScheduleSlot,
  ScheduleStream,
  SchoolRecord,
  GrantRoleRequest,
  RoleAssignment,
  SchoolSettings,
  SchoolSettingsRequest,
  TemplateDiff,
  TemplateReconcileOutcome,
  TemplateSummary,
  SettingsRequest,
  SlotRequest,
  StreamRequest,
  StageResponse,
  Standings,
  ValidationReport,
} from "./types";

// ---- Imports ----------------------------------------------------------

export function listImportBatches(): Promise<ImportBatchSummary[]> {
  return api<ImportBatchSummary[]>("/imports");
}

/** Without a year the server chooses the default the defaults read reports. */
export function stageImport(file: File, competitionYear?: number): Promise<StageResponse> {
  const formData = new FormData();
  formData.append("file", file);
  if (competitionYear !== undefined) formData.append("competitionYear", String(competitionYear));
  return api<StageResponse>("/imports", { method: "POST", formData });
}

export function getImportDefaults(): Promise<ImportDefaults> {
  return api<ImportDefaults>("/imports/defaults");
}

export function getImportValidation(id: string): Promise<ValidationReport> {
  return api(`/imports/${id}/validation`);
}

export function getImportReconciliation(id: string): Promise<ReconciliationReport> {
  return api(`/imports/${id}/reconciliation`);
}

export function approveImport(
  id: string,
  approve: boolean,
  note?: string,
): Promise<{ id: string; status: string }> {
  return api(`/imports/${id}/approve`, { method: "POST", body: { approve, note: note ?? null } });
}

export function promoteImport(
  id: string,
): Promise<{ id: string; status: string; promotedAtUtc: string }> {
  return api(`/imports/${id}/promote`, { method: "POST", body: {} });
}

// ---- Event-definition catalogue ---------------------------------------

export function listEventDefinitions(includeInactive = false): Promise<EventDefinition[]> {
  return api(`/event-definitions?includeInactive=${includeInactive}`);
}

export function createEventDefinition(request: DefinitionRequest): Promise<EventDefinition> {
  return api<EventDefinition>("/event-definitions", { method: "POST", body: request });
}

export function updateEventDefinition(
  id: string,
  request: DefinitionRequest,
): Promise<EventDefinition> {
  return api(`/event-definitions/${id}`, { method: "PUT", body: request });
}

/** Soft deactivate: definitions are referenced by events and records forever. */
export function deactivateEventDefinition(id: string): Promise<void> {
  return api(`/event-definitions/${id}`, { method: "DELETE" });
}

// ---- Carnivals --------------------------------------------------------

export function listCarnivals(): Promise<Carnival[]> {
  return api<Carnival[]>("/carnivals");
}

export function getCarnival(id: string): Promise<Carnival> {
  return api(`/carnivals/${id}`);
}

export function createCarnival(request: CarnivalRequest): Promise<Carnival> {
  return api<Carnival>("/carnivals", { method: "POST", body: request });
}

export function changeCarnivalStatus(
  id: string,
  status: CarnivalStatus,
  reason?: string,
): Promise<Carnival> {
  return api(`/carnivals/${id}/status`, {
    method: "POST",
    body: { status, reason: reason ?? null },
  });
}

// ---- Devices ----------------------------------------------------------

/** Not in the service worker's cache allowlist: a cached list would hide a device going quiet. */
export function listCarnivalDevices(carnivalId: string): Promise<DeviceFleet> {
  return api(`/carnivals/${carnivalId}/devices`);
}

/**
 * Failures are the caller's to swallow. The server refuses a
 * route id `X-Device-Id` does not match, so no caller can zero another device's row.
 */
export function reportDeviceSyncStatus(
  carnivalId: string,
  deviceId: string,
  report: DeviceStatusReport,
): Promise<DeviceStatus> {
  return api(
    `/carnivals/${carnivalId}/devices/${encodeURIComponent(deviceId)}/sync-status`,
    {
      method: "PUT",
      body: report,
      headers: { "X-Device-Id": deviceId },
      signal: AbortSignal.timeout(DEVICE_REPORT_TIMEOUT_MS),
    },
  );
}

/** Short enough that a hung socket cannot keep a report alive across several worker passes. */
export const DEVICE_REPORT_TIMEOUT_MS = 10_000;

// ---- Carnival events --------------------------------------------------

export function listCarnivalEvents(carnivalId: string): Promise<CarnivalEvent[]> {
  return api(`/carnivals/${carnivalId}/events`);
}

export function getCarnivalEvent(carnivalId: string, eventId: string): Promise<CarnivalEvent> {
  return api(`/carnivals/${carnivalId}/events/${eventId}`);
}

export function scheduleEvent(
  carnivalId: string,
  request: ScheduleEventRequest,
): Promise<CarnivalEvent> {
  return api(`/carnivals/${carnivalId}/events`, { method: "POST", body: request });
}

/** Idempotent: a re-press creates nothing and reports the repeats as skipped. */
export function generateDivisions(
  carnivalId: string,
  request: GenerateDivisionsRequest,
): Promise<GenerateDivisionsResponse> {
  return api(`/carnivals/${carnivalId}/events/generate`, {
    method: "POST",
    body: request,
  });
}

/** Refused once it has entries or results: a contested division is closed, never deleted. */
export function unscheduleDivision(carnivalId: string, eventId: string): Promise<void> {
  return api(`/carnivals/${carnivalId}/events/${eventId}`, { method: "DELETE" });
}

export function changeEventStatus(
  carnivalId: string,
  eventId: string,
  status: CarnivalEventStatus,
  reason?: string,
): Promise<CarnivalEvent> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/status`, {
    method: "POST",
    body: { status, reason: reason ?? null },
  });
}

export function openScheduledEvents(carnivalId: string): Promise<OpenScheduledResult> {
  return api(`/carnivals/${carnivalId}/events/open-scheduled`, {
    method: "POST",
  });
}

/**
 * Direct, never the outbox: the ladder is the division's shared state, and a queued raise would
 * let two decks disagree about which bar is up.
 */
export function raiseBar(
  carnivalId: string,
  eventId: string,
  height: number,
): Promise<CarnivalEvent> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/heights`, {
    method: "POST",
    body: { height },
  });
}

export function setAttemptsPerHeight(
  carnivalId: string,
  eventId: string,
  attemptsPerHeight: AttemptsPerHeight,
): Promise<CarnivalEvent> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/attempts-per-height`, {
    method: "PUT",
    body: { attemptsPerHeight },
  });
}

export function applyCarnivalTemplate(
  carnivalId: string,
  template: string,
): Promise<ApplyTemplateResult> {
  return api(`/carnivals/${carnivalId}/apply-template`, {
    method: "POST",
    body: { template },
  });
}

/** One small request, so the program caches whole and stays usable with no uplink. */
export function getCarnivalSchedule(carnivalId: string): Promise<Schedule> {
  return api(`/carnivals/${carnivalId}/schedule`);
}

/** Direct writes: the program is built at school before the carnival, never offline on the day. */
export function createScheduleStream(
  carnivalId: string,
  request: StreamRequest,
): Promise<ScheduleStream> {
  return api(`/carnivals/${carnivalId}/schedule/streams`, {
    method: "POST",
    body: request,
  });
}

export function updateScheduleStream(
  carnivalId: string,
  streamId: string,
  request: StreamRequest,
): Promise<ScheduleStream> {
  return api(`/carnivals/${carnivalId}/schedule/streams/${streamId}`, {
    method: "PUT",
    body: request,
  });
}

export function deleteScheduleStream(carnivalId: string, streamId: string): Promise<void> {
  return api(`/carnivals/${carnivalId}/schedule/streams/${streamId}`, { method: "DELETE" });
}

export function createScheduleSlot(
  carnivalId: string,
  streamId: string,
  request: SlotRequest,
): Promise<ScheduleSlot> {
  return api(`/carnivals/${carnivalId}/schedule/streams/${streamId}/slots`, {
    method: "POST",
    body: request,
  });
}

/** PUT replaces the slot: send it whole, never the one field being edited. */
export function updateScheduleSlot(
  carnivalId: string,
  slotId: string,
  request: SlotRequest,
): Promise<ScheduleSlot> {
  return api(`/carnivals/${carnivalId}/schedule/slots/${slotId}`, {
    method: "PUT",
    body: request,
  });
}

export function deleteScheduleSlot(carnivalId: string, slotId: string): Promise<void> {
  return api(`/carnivals/${carnivalId}/schedule/slots/${slotId}`, { method: "DELETE" });
}

export function placeScheduleEvent(
  carnivalId: string,
  slotId: string,
  request: PlaceEventRequest,
): Promise<ScheduleSlot> {
  return api(`/carnivals/${carnivalId}/schedule/slots/${slotId}/events`, {
    method: "POST",
    body: request,
  });
}

export function removeScheduleEvent(
  carnivalId: string,
  slotId: string,
  eventId: string,
): Promise<ScheduleSlot> {
  return api(`/carnivals/${carnivalId}/schedule/slots/${slotId}/events/${eventId}`, {
    method: "DELETE",
  });
}

// ---- Races ------------------------------------------------------------

/** The only way the client learns which race an entry sits in. */
export function listRaces(carnivalId: string): Promise<Race[]> {
  return api(`/carnivals/${carnivalId}/races`);
}

export function assignOfficials(
  carnivalId: string,
  eventId: string,
  officials: { subjectId: string; displayName?: string }[],
): Promise<OfficialsResponse> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/officials`, {
    method: "PUT",
    body: { officials },
  });
}

// ---- Entries ----------------------------------------------------------

export function searchEligibleStudentsPage(
  carnivalId: string,
  eventId: string,
  search: string,
  house?: string,
): Promise<EligibleStudentPage> {
  const params = new URLSearchParams();
  if (search.trim() !== "") params.set("search", search.trim());
  if (house) params.set("house", house);
  const query: Query = params.toString() === "" ? "" : `?${params.toString()}`;
  return api(
    `/carnivals/${carnivalId}/events/${eventId}/eligible-students${query}`,
  );
}

/** The students alone, for callers that do not surface the truncation notice. */
export async function searchEligibleStudents(
  carnivalId: string,
  eventId: string,
  search: string,
  house?: string,
): Promise<EligibleStudent[]> {
  return (await searchEligibleStudentsPage(carnivalId, eventId, search, house)).students;
}

export function listEntries(carnivalId: string, eventId: string): Promise<Entry[]> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/entries`);
}

export function createEntry(
  carnivalId: string,
  eventId: string,
  studentId: string,
  raceId: string | null = null,
): Promise<Entry> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/entries`, {
    method: "POST",
    body: { studentId, raceId },
  });
}

export function createTeamEntry(
  carnivalId: string,
  eventId: string,
  houseCode: string,
  studentIds: string[],
  raceId: string | null = null,
): Promise<Entry> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/team-entries`, {
    method: "POST",
    body: { houseCode, studentIds, raceId },
  });
}

/** Anonymous: read before sign-in, to paint the chrome. */
export function getBranding(): Promise<Branding> {
  return api<Branding>("/branding");
}

export function listHouses(): Promise<string[]> {
  return api<{ houses: string[] }>("/school/houses").then((r) => r.houses);
}

/** Separate from `listHouses` so neither call changes shape when the other does. */
export function getHouseMarks(): Promise<HouseMarkDto[]> {
  return api<{ houseMarks: HouseMarkDto[] }>("/school/houses").then((r) => r.houseMarks);
}

// ---- School settings --------------------------------------------------

export function getSchoolSettings(): Promise<SchoolSettings> {
  return api<SchoolSettings>("/school/settings");
}

/** `updatedAtUtc` is the stamp the form rendered from; the server refuses a stale one. */
export function putSchoolSettings(request: SchoolSettingsRequest): Promise<SchoolSettings> {
  return api<SchoolSettings>("/school/settings", { method: "PUT", body: request });
}

// ---- In-app role assignment -------------------------------------------

export function listRoleAssignments(): Promise<RoleAssignment[]> {
  return api<RoleAssignment[]>("/admin/role-assignments");
}

export function grantRole(request: GrantRoleRequest): Promise<RoleAssignment> {
  return api<RoleAssignment>("/admin/role-assignments", { method: "POST", body: request });
}

export function revokeRole(id: string, reason: string): Promise<RoleAssignment> {
  return api(`/admin/role-assignments/${id}`, {
    method: "DELETE",
    body: { reason },
  });
}

// ---- Template reconciliation ------------------------------------------

export function listEventTemplates(): Promise<TemplateSummary[]> {
  return api<TemplateSummary[]>("/admin/event-templates");
}

export function previewTemplateReconcile(templateId: string): Promise<TemplateDiff> {
  return api(`/admin/event-templates/${templateId}/reconcile`);
}

/** `diffHash` comes from the preview, never invented: the server refuses a stale one. */
export function applyTemplateReconcile(
  templateId: string,
  diffHash: string,
  reason: string,
  excludeCodes: string[],
): Promise<TemplateReconcileOutcome> {
  return api(`/admin/event-templates/${templateId}/reconcile`, {
    method: "POST",
    body: { diffHash, reason, excludeCodes },
  });
}

// ---- Results (reads + the online correction; captures go through the outbox) ----

export function listResults(carnivalId: string, eventId: string): Promise<ResultDto[]> {
  return api(`/carnivals/${carnivalId}/events/${eventId}/results`);
}

/** Direct while online, so a stale-revision 409 shows immediately; offline it is queued instead. */
export function correctResult(
  resultId: string,
  request: CorrectResultRequest,
): Promise<ResultDto> {
  return api(`/results/${resultId}/correct`, { method: "POST", body: request });
}

// ---- House standings (scoring engine) ---------------------------------

export function getStandings(carnivalId: string): Promise<Standings> {
  return api(`/carnivals/${carnivalId}/standings`);
}

export function getProgress(carnivalId: string): Promise<CarnivalProgress> {
  return api(`/carnivals/${carnivalId}/progress`);
}

// ---- Reports and controlled exports -----------------------------------

/** Already ordered and placed by the scoring engine: the client never ranks anything itself. */
export function getResultsReport(carnivalId: string): Promise<ResultsReport> {
  return api(`/carnivals/${carnivalId}/reports/results`);
}

export type ReportName = "standings" | "results" | "district";

/** Audited server-side, which is why it is a real request and never assembled client-side. */
export function downloadReportCsv(carnivalId: string, report: ReportName): Promise<ApiFile> {
  return apiFile(`/carnivals/${carnivalId}/reports/${report}.csv`, `${report}.csv`);
}

// ---- School records ----------------------------------------------------

/** Outside `/reports/`, so the service worker caches it for the deck badge on a cold start. */
export function getRecords(sport?: string): Promise<SchoolRecord[]> {
  const query: Query = sport ? `?sport=${encodeURIComponent(sport)}` : "";
  return api(`/records${query}`);
}

/** The service-worker predicate does not match this path: online-only, never cached. */
export function getRecordsForAdministration(sport?: string): Promise<SchoolRecord[]> {
  const query: Query = sport ? `?sport=${encodeURIComponent(sport)}` : "";
  return api(`/records/administration${query}`);
}

/** Direct, never the outbox: record custody is a staff-desk act with no venue path. */
export function voidRecord(recordId: string, reason: string): Promise<SchoolRecord> {
  return api(`/records/${recordId}/void`, {
    method: "POST",
    body: { reason },
  });
}

// ---- Scoring settings + novelty/bonus adjustments ---------------------

export function getCarnivalSettings(carnivalId: string): Promise<CarnivalSettings> {
  return api(`/carnivals/${carnivalId}/settings`);
}

export function updateCarnivalSettings(
  carnivalId: string,
  request: SettingsRequest,
): Promise<CarnivalSettings> {
  return api(`/carnivals/${carnivalId}/settings`, { method: "PUT", body: request });
}

export function listAdjustments(carnivalId: string): Promise<HousePointsAdjustment[]> {
  return api(`/carnivals/${carnivalId}/adjustments`);
}

/** Create goes through the outbox. */
export function voidAdjustment(
  carnivalId: string,
  adjustmentId: string,
  reason: string,
): Promise<HousePointsAdjustment> {
  return api(`/carnivals/${carnivalId}/adjustments/${adjustmentId}/void`, {
    method: "POST",
    body: { reason },
  });
}
