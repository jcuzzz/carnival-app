using Microsoft.Extensions.Configuration;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Imports;

namespace CarnivalApp.Api.Tests;

public static class Fixtures
{
    public static readonly AuditActor Importer = new("importer", "SystemAdministrator");
    public static readonly AuditActor Reviewer = new("reviewer", "SystemAdministrator");

    public static IConfiguration Config(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?> { ["School:Timezone"] = "Australia/Melbourne" };
        foreach (var (key, value) in extra) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static AuditService Audit(CarnivalAppDbContext db) => new(db, Config());

    /// <summary>Import service wired with a real AuditService on the same context.</summary>
    public static RosterImportService ImportService(CarnivalAppDbContext db) => new(db, Audit(db));

    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Discovery-confirmed house configuration (4 houses); codes as they appear in the CASES21 export.</summary>
    public static readonly string[] Houses = ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"];

    /// <summary>The year the fixture rosters' expected ages are computed for.</summary>
    public const int CompetitionYear = 2026;

    public static ImportMapping Mapping() =>
        ImportMapping.LoadFromFile(Path("import-mapping.example.json"));

    public static CsvTable.Parsed ParseFixture(string name)
    {
        using var stream = File.OpenRead(Path(name));
        return CsvTable.Parse(stream);
    }
}
