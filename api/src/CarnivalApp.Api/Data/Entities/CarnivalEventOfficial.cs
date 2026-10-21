namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Advisory, not access control: a race may span two events, so
/// gating capture on assignment would refuse correct captures.
/// </summary>
public class CarnivalEventOfficial
{
    public Guid Id { get; set; }

    public Guid CarnivalEventId { get; set; }
    public CarnivalEvent CarnivalEvent { get; set; } = null!;

    public required string SubjectId { get; set; }

    public string? SubjectDisplayName { get; set; }

    public DateTimeOffset AssignedAtUtc { get; set; }
    public required string AssignedBySubject { get; set; }
}
