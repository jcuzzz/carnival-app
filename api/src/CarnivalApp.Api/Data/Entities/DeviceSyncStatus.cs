namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Telemetry, not a record: last-write-wins and never audited per write. No student data ever —
/// nothing may be added here that names a student.
/// </summary>
public class DeviceSyncStatus
{
    public Guid CarnivalId { get; set; }
    public Carnival? Carnival { get; set; }

    /// <summary>The same value that rides on every idempotent write as X-Device-Id.</summary>
    public required string DeviceId { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset? LastDrainUtc { get; set; }

    /// <summary>Queued + syncing.</summary>
    public int PendingCount { get; set; }

    /// <summary>Conflict + rejected.</summary>
    public int ParkedCount { get; set; }

    public DateTimeOffset? OldestPendingCapturedAtUtc { get; set; }

    /// <summary>The authenticated subject — never device-asserted.</summary>
    public required string LastSubject { get; set; }

    public string? LastDisplayName { get; set; }

    public string? AppVersion { get; set; }
}
