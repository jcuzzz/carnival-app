namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Carries an immutable eligibility snapshot so historical
/// results stay reproducible after roster changes.
/// </summary>
public class Entry
{
    public Guid Id { get; set; }

    public Guid CarnivalEventId { get; set; }
    public CarnivalEvent CarnivalEvent { get; set; } = null!;

    // Exactly one of StudentId (individual) or HouseCode (team) is set — a DB check constraint.
    public Guid? StudentId { get; set; }
    public Student? Student { get; set; }

    public string? HouseCode { get; set; }

    public ICollection<RelayLeg> RelayLegs { get; set; } = [];

    /// <summary>
    /// Scoped to this entry's event and the placing unit: placings are computed within the heat, or
    /// across the whole event when null. Never compare heat numbers across events.
    /// </summary>
    public int? Heat { get; set; }

    /// <summary>Scoped to the race, not the heat or event; unique per race.</summary>
    public int? Lane { get; set; }

    /// <summary>
    /// Never gating: no entry, result, eligibility or lifecycle rule may require one.
    /// </summary>
    public Guid? RaceId { get; set; }
    public Race? Race { get; set; }

    // Copied from the roster snapshot at creation and never rewritten; a team entry has no
    // RosterSnapshotId and carries team-level values (per-swimmer snapshots live on each leg).
    public Guid? RosterSnapshotId { get; set; }
    public RosterSnapshot? RosterSnapshot { get; set; }
    public required string SnapshotDisplayName { get; set; }
    public int SnapshotYearLevel { get; set; }
    public required string SnapshotHouseCode { get; set; }
    public required string SnapshotAgeGroup { get; set; }
    public CompetitionCategory SnapshotCategory { get; set; }

    // The override never rewrites SnapshotCategory; the effective
    // category is CategoryOverride ?? SnapshotCategory.
    public CompetitionCategory? CategoryOverride { get; set; }
    public string? CategoryOverrideActorSubject { get; set; }
    public DateTimeOffset? CategoryOverrideAtUtc { get; set; }
    public string? CategoryOverrideReason { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string CreatedBySubject { get; set; }

    public ICollection<Result> Results { get; set; } = [];
}
