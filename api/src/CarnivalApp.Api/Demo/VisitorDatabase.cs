using System.Text.RegularExpressions;
using Npgsql;

namespace CarnivalApp.Api.Demo;

/// <summary>
/// Which database the per-database caches key on; one database outside demo mode (ADR-0061 §1).
/// </summary>
public interface IDatabaseKey
{
    string Current { get; }
}

public sealed class SingleDatabase : IDatabaseKey
{
    public string Current => "";
}

/// <summary>
/// The visitor's database, chosen per request by the demo session handler (ADR-0061 §1–2).
/// </summary>
public sealed partial class VisitorDatabase(IHttpContextAccessor accessor) : IDatabaseKey
{
    public const string ItemKey = "CarnivalApp.VisitorDatabase";
    public const string NamePrefix = "demo_v_";

    [GeneratedRegex("^demo_v_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static string NewName() => NamePrefix + Guid.NewGuid().ToString("N");

    public static void Select(HttpContext context, string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"'{name}' is not a visitor database name.", nameof(name));
        context.Items[ItemKey] = name;
    }

    public string? Selected => accessor.HttpContext?.Items[ItemKey] as string;

    public string Current => Selected ?? "";

    /// <summary>
    /// Throws rather than fall back: the configured database is never a visitor's.
    /// </summary>
    public string ConnectionString(string serverConnectionString, int poolSize)
    {
        var database = Selected ?? throw new InvalidOperationException(
            "Demo mode: no visitor database is selected for this request.");
        return ForDatabase(serverConnectionString, database, poolSize);
    }

    // Idle pools empty out, so idle visitors hold no server connections.
    public static string ForDatabase(string serverConnectionString, string database, int poolSize) =>
        new NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Database = database,
            MinPoolSize = 0,
            MaxPoolSize = poolSize,
            ConnectionIdleLifetime = 60,
        }.ConnectionString;
}
