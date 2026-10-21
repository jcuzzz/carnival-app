namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Minimal identity: no date of birth, gender, contact or
/// family data. Eligibility context lives on RosterSnapshot.
/// </summary>
public class Student
{
    /// <summary>Never exposed as the source-system id.</summary>
    public Guid Id { get; set; }

    /// <summary>CASES21 STKEY. Unique per deployment.</summary>
    public required string ExternalStudentId { get; set; }

    public required string Surname { get; set; }
    public required string FirstName { get; set; }
    public string? PreferredName { get; set; }

    /// <summary>False when the latest promoted import marks the student LEFT/LVNG.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public ICollection<RosterSnapshot> RosterSnapshots { get; set; } = [];
}
