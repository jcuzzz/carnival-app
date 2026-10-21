namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// The source file is never stored; only its name, hash and derived counts survive intake.
/// </summary>
public class ImportBatch
{
    public Guid Id { get; set; }

    public required string SourceFileName { get; set; }

    public required string SourceFileHash { get; set; }

    public required string MappingVersion { get; set; }

    public required string ImportedBySubject { get; set; }
    public DateTimeOffset ImportedAtUtc { get; set; }

    /// <summary>Names STKEYs, so it is discarded on promotion with the staged rows.</summary>
    public string? ValidationResultsJson { get; set; }

    /// <summary>
    /// The exact rows promotion applies; derived fields only, never the source birthdate. Discarded
    /// on promotion, since it would be a second roster copy.
    /// </summary>
    public string? StagedRowsJson { get; set; }

    public int NewCount { get; set; }
    public int ChangedCount { get; set; }
    public int InactiveCount { get; set; }
    public int UnmatchedCount { get; set; }
    public int RejectedCount { get; set; }

    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Staged;

    public string? ReviewedBySubject { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReviewNote { get; set; }

    public DateTimeOffset? PromotedAtUtc { get; set; }

    public ICollection<RosterSnapshot> RosterSnapshots { get; set; } = [];
}
