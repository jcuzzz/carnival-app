namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Never rewritten; promotion creates a new current snapshot. Holds derived competition fields
/// only — never a birth date or a gender attribute.
/// </summary>
public class RosterSnapshot
{
    public Guid Id { get; set; }

    public Guid StudentId { get; set; }
    public Student Student { get; set; } = null!;

    public int SchoolYear { get; set; }

    public int YearLevel { get; set; }

    public required string HouseCode { get; set; }

    /// <summary>
    /// The age at the cut-off ("12") or the configured senior band. Never "OPEN", which is reserved
    /// for an event open across ages — a snapshot carrying it would match every open event.
    /// </summary>
    public required string CompetitionAgeGroup { get; set; }

    public CompetitionCategory CompetitionCategory { get; set; }

    public string? HomeGroup { get; set; }

    public Guid ImportBatchId { get; set; }
    public ImportBatch ImportBatch { get; set; } = null!;

    public DateOnly EffectiveDate { get; set; }

    public bool IsCurrent { get; set; }
}
