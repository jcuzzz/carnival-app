namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Records who swam, not a per-leg time; the team's result lives on the parent Entry.
/// </summary>
public class RelayLeg
{
    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry Entry { get; set; } = null!;

    /// <summary>1-based; also the medley stroke order.</summary>
    public int LegOrder { get; set; }

    /// <summary>Null for a freestyle relay.</summary>
    public string? Stroke { get; set; }

    public Guid StudentId { get; set; }
    public Student Student { get; set; } = null!;

    public Guid RosterSnapshotId { get; set; }
    public RosterSnapshot RosterSnapshot { get; set; } = null!;
    public required string SnapshotDisplayName { get; set; }
    public int SnapshotYearLevel { get; set; }
    public required string SnapshotHouseCode { get; set; }
    public required string SnapshotAgeGroup { get; set; }
    public CompetitionCategory SnapshotCategory { get; set; }
}
