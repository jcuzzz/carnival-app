using System.Text.RegularExpressions;
using Npgsql;

namespace CarnivalApp.Api.Demo;

/// <summary>The server-side half of a visitor sandbox: its database (ADR-0061 §1, §3).</summary>
public interface IVisitorDatabases
{
    Task CreateAsync(string name, CancellationToken ct);

    Task DropAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Runs as the provisioner role (CREATEDB, owner of the control database); names are validated
/// before they reach SQL, which cannot parameterise identifiers.
/// </summary>
public sealed partial class PostgresVisitorDatabases(IConfiguration configuration) : IVisitorDatabases
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    private string ControlConnection => DemoMode.ControlConnectionString(configuration);

    private string Template
    {
        get
        {
            var template = configuration[DemoMode.TemplateDatabaseKey] ?? DemoMode.DefaultTemplateDatabase;
            return Identifier().IsMatch(template)
                ? template
                : throw new InvalidOperationException($"{DemoMode.TemplateDatabaseKey} is not a plain identifier.");
        }
    }

    public async Task CreateAsync(string name, CancellationToken ct) =>
        await ExecuteAsync($"CREATE DATABASE \"{Checked(name)}\" TEMPLATE \"{Template}\"", ct);

    public async Task DropAsync(string name, CancellationToken ct)
    {
        await ExecuteAsync($"DROP DATABASE IF EXISTS \"{Checked(name)}\" WITH (FORCE)", ct);
        // FORCE killed its connections; the pool would hand out dead ones.
        await using var pooled = new NpgsqlConnection(DemoMode.VisitorConnectionString(configuration, name));
        NpgsqlConnection.ClearPool(pooled);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ControlConnection);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            @"SELECT datname FROM pg_database WHERE datname LIKE 'demo\_v\_%'", connection);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (VisitorDatabase.IsValidName(name)) names.Add(name);
        }
        return names;
    }

    private static string Checked(string name) => VisitorDatabase.IsValidName(name)
        ? name
        : throw new ArgumentException($"'{name}' is not a visitor database name.", nameof(name));

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ControlConnection);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
