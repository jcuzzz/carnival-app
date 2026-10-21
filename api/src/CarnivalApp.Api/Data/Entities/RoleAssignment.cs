namespace CarnivalApp.Api.Data.Entities;

public static class RoleAssignmentLimits
{
    public const int MaxUpnLength = 256;
    public const int MaxReasonLength = 500;
}

/// <summary>
/// Rows are never deleted: a revocation is a write, so the table is its own history.
/// </summary>
public class RoleAssignment
{
    public Guid Id { get; set; }

    /// <summary>Null until the UPN binds on first sign-in.</summary>
    public string? SubjectId { get; set; }

    /// <summary>Lower-case; never re-matched once bound.</summary>
    public required string SubjectUpn { get; set; }

    public RoleSubjectType SubjectType { get; set; }

    public string? SubjectDisplayName { get; set; }

    public PlatformRole Role { get; set; }

    /// <summary>Unwritten: assignments are deployment-wide.</summary>
    public string? Scope { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBySubject { get; set; }
    public string? RevocationReason { get; set; }

    /// <summary>Unused: the list plus the audit trail is the review evidence.</summary>
    public string? ReviewedBySubject { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string CreatedBySubject { get; set; }
}
