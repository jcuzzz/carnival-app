namespace CarnivalApp.Api.Data.Entities;

// All enums are persisted as strings, so reordering members is safe.

/// <summary>
/// Competition grouping derived from the import's GENDER column (M / F / X → M / F / S) — not a
/// gender record; no other gender data is stored.
/// </summary>
public enum CompetitionCategory
{
    M,
    F,
    S,
}

public enum ImportBatchStatus
{
    Staged,
    Validated,
    ValidationFailed,
    Approved,
    Rejected,
    Promoted,
}

public enum CarnivalType
{
    Swimming,
    Athletics,
    CrossCountry,
    Other,
}

/// <summary>Forward-only: Draft → Open → Closed → Finalised.</summary>
public enum CarnivalStatus
{
    Draft,
    Open,
    Closed,
    Finalised,
}

public enum EventFormat
{
    Individual,
    Team,
}

/// <summary>Both tiers score house points; school records consider Competitive only.</summary>
public enum EventTier
{
    Competitive,
    Social,
}

/// <summary>
/// Social is a flat per-entry point to the entrant's house, needing no ranking or result. Null on
/// the event means follow the tier.
/// </summary>
public enum EventScoringMode
{
    Places,
    Social,
}

public enum MeasureType
{
    Time,      // lower is better (seconds)
    Distance,  // higher is better (metres)
    Height,    // higher is better (metres)
    Count,     // higher is better (e.g. laps)
}

public enum CarnivalEventStatus
{
    Scheduled,
    Open,
    Closed,
    Finalised,
}

/// <summary>Only Ok carries a measured value.</summary>
public enum ResultStatus
{
    Ok,
    Dns, // did not start
    Dnf, // did not finish
    Dq,  // disqualified
    Nm,  // no mark: attempted, nothing valid — field events only
}

/// <summary>
/// Draft exists only in the client outbox; server-side a result enters at Provisional.
/// </summary>
public enum ResultLifecycleState
{
    Draft,
    Provisional,
    Verified,
    Final,
}

public enum SchoolRecordStatus
{
    Candidate,
    Ratified,
    Superseded,
    RejectedCandidate,
}

/// <summary>
/// Retired role names are still accepted as aliases by
/// PlatformRoles.TryParse, and audit history keeps the old strings.
/// </summary>
public enum PlatformRole
{
    CarnivalAdministrator,

    Recorder,

    Viewer,

    /// <summary>
    /// Kept apart from CarnivalAdministrator because imports are where real student data lands;
    /// also holds the full carnival surface so a one-technician school is not locked out.
    /// </summary>
    SystemAdministrator,
}

public enum RoleSubjectType
{
    User,
    Group,
}
