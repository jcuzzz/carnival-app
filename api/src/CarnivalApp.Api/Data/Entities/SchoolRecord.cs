namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Minted only by carnival finalisation, so a row is Ratified from
/// birth; superseding is a new row. The current record is <c>Ratified
/// &amp;&amp; EffectiveTo == null &amp;&amp; VoidedAtUtc == null</c>.
/// </summary>
public class SchoolRecord
{
    public Guid Id { get; set; }

    public Guid EventDefinitionId { get; set; }
    public EventDefinition EventDefinition { get; set; } = null!;

    public required string AgeGroup { get; set; }
    public CompetitionCategory Category { get; set; }

    public Guid ResultId { get; set; }
    public Result Result { get; set; } = null!;

    /// <summary>Denormalised for reporting stability.</summary>
    public decimal RecordValue { get; set; }

    /// <summary>Captured at mint; a relay's is the team entry's snapshot name.</summary>
    public required string HolderDisplayName { get; set; }

    public Guid? PreviousRecordId { get; set; }
    public SchoolRecord? PreviousRecord { get; set; }

    // Candidate and RejectedCandidate are unreachable and kept dormant, not repurposed.
    public SchoolRecordStatus Status { get; set; } = SchoolRecordStatus.Ratified;

    public string? ApprovedBySubject { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public string? ApprovalNote { get; set; }

    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    // Voiding never deletes the row or rewrites Status;
    // every "current record" read adds VoidedAtUtc == null.
    public DateTimeOffset? VoidedAtUtc { get; set; }
    public string? VoidedBySubject { get; set; }
    public string? VoidReason { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
