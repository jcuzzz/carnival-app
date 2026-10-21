namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Append-only. No navigation properties: audit rows must survive
/// entity deletion, so references are soft (type + id strings).
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }

    /// <summary>"system" for automated actions.</summary>
    public required string ActorSubject { get; set; }

    public string? ActorRole { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string? SchoolTimezone { get; set; }

    public required string Action { get; set; }

    public required string EntityType { get; set; }
    public required string EntityId { get; set; }

    public string? PriorValuesJson { get; set; }

    public string? NewValuesJson { get; set; }

    public string? Reason { get; set; }

    public Guid? ImportBatchId { get; set; }

    public string? ApplicationVersion { get; set; }

    /// <summary>
    /// For a result capture or correction, the validation rule set that passed the value (not the
    /// carnival's scoring version); elsewhere the scoring rule version.
    /// </summary>
    public string? RuleVersion { get; set; }
}
