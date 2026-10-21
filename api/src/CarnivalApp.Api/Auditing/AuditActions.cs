namespace CarnivalApp.Api.Auditing;

/// <summary>
/// Existing rows carry the values, so a value never changes.
/// Convention: "&lt;entity&gt;.&lt;verb&gt;", lower-kebab, ≤64 chars.
/// </summary>
public static class AuditActions
{
    // ---- Carnival ---------------------------------------------------------

    public const string CarnivalCreate = "carnival.create";
    public const string CarnivalUpdate = "carnival.update";

    public const string CarnivalStatus = "carnival.status";

    public const string CarnivalSettings = "carnival.settings";

    public const string CarnivalApplyTemplate = "carnival.apply-template";

    // ---- Event-definition catalogue --------------------------------------

    public const string EventDefinitionCreate = "event-definition.create";
    public const string EventDefinitionUpdate = "event-definition.update";
    public const string EventDefinitionDeactivate = "event-definition.deactivate";

    /// <summary>
    /// The summary row beside the per-definition rows: reason, diff hash and counts.
    /// </summary>
    public const string EventTemplateReconcile = "event-template.reconcile";

    // ---- Carnival events -------------------------------------------------

    public const string CarnivalEventSchedule = "carnival-event.schedule";
    public const string CarnivalEventUpdate = "carnival-event.update";

    /// <summary>Audited apart from a plain update because it re-bases eligibility.</summary>
    public const string CarnivalEventDivisionChange = "carnival-event.division-change";

    public const string CarnivalEventUnschedule = "carnival-event.unschedule";

    public const string CarnivalEventStatus = "carnival-event.status";

    public const string CarnivalEventRaiseBar = "carnival-event.raise-bar";

    public const string CarnivalEventAttemptsPerHeight = "carnival-event.attempts-per-height";

    /// <summary>One row per press, however many divisions opened.</summary>
    public const string CarnivalEventOpenAll = "carnival-event.open-all";

    public const string CarnivalEventAssignOfficials = "carnival-event.assign-officials";

    /// <inheritdoc cref="ResultVerify"/>
    public const string CarnivalEventFinalise = "carnival-event.finalise";

    // ---- Schedule streams and slots --------------------------------------

    public const string ScheduleStreamCreate = "schedule-stream.create";
    public const string ScheduleStreamUpdate = "schedule-stream.update";
    public const string ScheduleStreamDelete = "schedule-stream.delete";

    public const string ScheduleSlotCreate = "schedule-slot.create";
    public const string ScheduleSlotUpdate = "schedule-slot.update";
    public const string ScheduleSlotDelete = "schedule-slot.delete";

    public const string ScheduleEventPlace = "schedule-slot.place-event";

    public const string ScheduleEventRemove = "schedule-slot.remove-event";

    // ---- Races -----------------------------------------------------------

    public const string RaceCreate = "race.create";
    public const string RaceUpdate = "race.update";
    public const string RaceDelete = "race.delete";

    /// <summary>One record for one human act, never N <see cref="EntryMarshal"/> rows.</summary>
    public const string RaceMaterialise = "race.materialise";

    // ---- Entries ---------------------------------------------------------

    public const string EntryCreate = "entry.create";
    public const string EntryRemove = "entry.remove";

    /// <summary>
    /// Race, lane and heat assigned, corrected or cleared; the evidence in a points dispute.
    /// </summary>
    public const string EntryMarshal = "entry.marshal";

    public const string EntryCategoryOverride = "entry.category-override";

    public const string TeamEntryCreate = "team-entry.create";

    // ---- Result lifecycle ------------------------------------------------

    public const string ResultSubmit = "result.submit";
    public const string ResultCorrect = "result.correct";

    public const string ResultAttempt = "result.attempt";

    public const string ResultJump = "result.jump";

    /// <summary>
    /// No longer written, but existing audit rows carry it;
    /// kept so they stay greppable. Never reuse the value.
    /// </summary>
    public const string ResultVerify = "result.verify";

    /// <inheritdoc cref="ResultVerify"/>
    public const string ResultReturnToProvisional = "result.return-to-provisional";

    public const string ResultFinalise = "result.finalise";

    // ---- School records --------------------------------------------------

    public const string SchoolRecordMint = "school-record.mint";

    public const string SchoolRecordVoid = "school-record.void";

    // ---- House-point adjustments -----------------------------------------

    public const string HouseAdjustmentCreate = "house-adjustment.create";
    public const string HouseAdjustmentVoid = "house-adjustment.void";

    // ---- Reports and controlled exports ----------------------------------

    public const string ReportExport = "report.export";

    // ---- Roster import pipeline ------------------------------------------

    public const string ImportStage = "import.stage";
    public const string ImportApprove = "import.approve";
    public const string ImportReject = "import.reject";
    public const string ImportPromote = "import.promote";

    // ---- School configuration --------------------------------------------

    public const string SchoolSettings = "school.settings";

    // ---- In-app role assignment ------------------------------------------

    public const string RoleAssignmentGrant = "role-assignment.grant";

    /// <summary>Rows are never deleted, so this is the removal.</summary>
    public const string RoleAssignmentRevoke = "role-assignment.revoke";

    /// <summary>
    /// An unbound row took its subject id on first sign-in; the actor is "system".
    /// </summary>
    public const string RoleAssignmentBind = "role-assignment.bind";

    // ---- Development ------------------------------------------------------

    public const string DevSeed = "dev.seed";
}
