namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// References a house, never a student, entry or result, so it can never create a record candidate.
/// Corrected by voiding and recording anew, never edited in place.
/// </summary>
public class HousePointsAdjustment
{
    public Guid Id { get; set; }

    public Guid CarnivalId { get; set; }
    public Carnival Carnival { get; set; } = null!;

    public required string HouseCode { get; set; }

    /// <summary>May be negative (a deduction).</summary>
    public int Points { get; set; }

    public required string Category { get; set; }

    public required string Reason { get; set; }

    public required string RecordedBySubject { get; set; }

    public string? RecordedByRole { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    // A voided adjustment stays for the record but no longer counts.
    public bool Voided { get; set; }
    public string? VoidedBySubject { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset? VoidedAtUtc { get; set; }
}
