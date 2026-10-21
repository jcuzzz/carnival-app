namespace CarnivalApp.Api.Data.Entities;

public class Carnival
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public CarnivalType Type { get; set; }
    public DateOnly Date { get; set; }
    public string? Venue { get; set; }
    public CarnivalStatus Status { get; set; } = CarnivalStatus.Draft;

    public required string RuleVersion { get; set; }

    /// <summary>
    /// Scoring overrides, adjustment categories and lane count; null means the defaults. A legacy
    /// <c>divisions</c> key may linger and is never read.
    /// </summary>
    public string? SettingsJson { get; set; }

    public required string OwnerSubject { get; set; }

    /// <summary>
    /// Concurrency token bumped by every transition and settings write. A plain int rather than
    /// xmin/rowversion keeps the schema provider-clean for SQLite.
    /// </summary>
    public int Version { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public ICollection<CarnivalEvent> Events { get; set; } = [];

    /// <summary>Empty means "not configured", never an empty matrix.</summary>
    public ICollection<CarnivalDivision> Divisions { get; set; } = [];

    public ICollection<ScheduleStream> ScheduleStreams { get; set; } = [];

    /// <summary>
    /// Owned by the carnival, not the program: a race must work with no schedule built.
    /// </summary>
    public ICollection<Race> Races { get; set; } = [];
}
