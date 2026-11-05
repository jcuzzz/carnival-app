namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Operational, kept 90 days. Rows are written in time order, so <see cref="Id"/> orders them
/// where SQLite cannot order a <see cref="DateTimeOffset"/>.
/// </summary>
public class SignInAttempt
{
    public long Id { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    /// <summary>Null when the name matched no account; the typed name is never stored.</summary>
    public Guid? LocalAccountId { get; set; }

    public required string ClientAddress { get; set; }

    public SignInOutcome Outcome { get; set; }
}
