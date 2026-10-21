using System.Net;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The district selection export (ADR-0028 §4). What is pinned: the ONE export
/// with a lifecycle gate, the Selection/Place split ADR-0015 demands, the tie
/// the platform refuses to break, and who is on the list at all.
/// </summary>
public sealed class DistrictExportTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private HttpClient Admin => _factory.CreateClientAs("district-admin", "CarnivalAdministrator");

    private static string Path(Guid carnivalId) =>
        $"/api/v1/carnivals/{carnivalId}/reports/district.csv";

    /// <summary>
    /// One Competitive division with a tie at second, its Social sibling, an Open
    /// (S) division and a relay — every inclusion rule in one file.
    /// </summary>
    private Task<SeededRecords> SeedAsync() => new RecordsSeed()
        .Definition("SW-FREE", recordCategories: null)
        .Definition("SW-RELAY", recordCategories: null, format: EventFormat.Team)
        .Division("SW-FREE", "13", CompetitionCategory.M,
            new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m, Heat: 1, YearLevel: 7),
            new RecordsSeed.Swim("Blake Chen", "WATTLE", 31.8m, Heat: 1, YearLevel: 8),
            new RecordsSeed.Swim("Cora Diaz", "MOUNTAIN ASH", 31.8m, Heat: 2, YearLevel: 8),
            new RecordsSeed.Swim("Dale Egan", "GREVILLEA", 35.0m, Heat: 2, YearLevel: 7))
        .Division("SW-FREE", "13", CompetitionCategory.S,
            new RecordsSeed.Swim("Emery Frost", "GREVILLEA", 33.0m, YearLevel: 9))
        .Division("SW-FREE", "13", CompetitionCategory.M, EventTier.Social,
            new RecordsSeed.Swim("Finn Gray", "WATTLE", 20.0m))
        .Division("SW-RELAY", "13", CompetitionCategory.M,
            new RecordsSeed.Swim("BANKSIA team", "BANKSIA", 120.2m, Team: true))
        .BuildAsync(_factory);

    private async Task<SeededRecords> FinalisedAsync()
    {
        var seeded = await SeedAsync();
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        return seeded;
    }

    // ---- The finalisation gate ---------------------------------------------

    [Fact]
    public async Task An_unfinalised_carnival_refuses_the_district_list()
    {
        // This file leaves the school and drives a selection process that cannot
        // see a later correction (§4).
        var seeded = await SeedAsync();
        var response = await Admin.GetAsync(Path(seeded.CarnivalId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotFinalised);
        Assert.Contains("Finalise the carnival", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // A refusal is not an export.
        Assert.Empty(await ExportAuditAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task The_other_two_exports_are_unaffected_by_the_gate()
    {
        var seeded = await SeedAsync();
        foreach (var report in new[] { "results.csv", "standings.csv" })
            Assert.Equal(HttpStatusCode.OK,
                (await Admin.GetAsync($"/api/v1/carnivals/{seeded.CarnivalId}/reports/{report}")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_carnival_is_a_404_not_a_gate_refusal()
    {
        var response = await Admin.GetAsync(Path(Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotFound);
    }

    // ---- The file -----------------------------------------------------------

    [Fact]
    public async Task The_list_is_the_top_two_of_each_competitive_division_and_a_tie_yields_three_rows()
    {
        var seeded = await FinalisedAsync();
        var lines = await (await Admin.GetAsync(Path(seeded.CarnivalId))).CsvLinesAsync();
        var free = seeded.DefinitionCodes["SW-FREE"];

        Assert.Equal(
            "Event code,Event,Division,Selection,Name,Year level,Result",
            lines[0]);
        // Selection, NOT Place: this is the event ladder, so heat 2's Cora Diaz
        // ties heat 1's Blake Chen for second and both are listed. The platform
        // does not break a tie the school has to break itself.
        Assert.Equal(
            [
                $"{free},SW-FREE,13 Boys,1,Aiden Brook,7,31.20",
                $"{free},SW-FREE,13 Boys,2,Blake Chen,8,31.80",
                $"{free},SW-FREE,13 Boys,2,Cora Diaz,8,31.80",
                // Open (S) divisions are IN: ADR-0023 §1 means these students
                // cannot also be in the Boys division.
                $"{free},SW-FREE,13 Open,1,Emery Frost,9,33.00",
            ],
            lines[1..]);
    }

    [Fact]
    public async Task Relays_and_social_divisions_are_absent()
    {
        var seeded = await FinalisedAsync();
        var lines = await (await Admin.GetAsync(Path(seeded.CarnivalId))).CsvLinesAsync();

        // A relay is a school team, not district material.
        Assert.DoesNotContain(lines, l => l.Contains("BANKSIA team", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(seeded.DefinitionCodes["SW-RELAY"], StringComparison.Ordinal));
        // A Social swim is not selection evidence, however fast.
        Assert.DoesNotContain(lines, l => l.Contains("Finn Gray", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_column_carries_a_students_category_identity_or_house()
    {
        // Data minimisation (CLAUDE.md), ADR-0026 §2 and ADR-0032 §2: `Division`
        // is the only category-shaped value on the row — no DOB, no student id,
        // no category, and no house (an internal grouping the convener cannot use).
        var seeded = await FinalisedAsync();
        var lines = await (await Admin.GetAsync(Path(seeded.CarnivalId))).CsvLinesAsync();

        var header = CsvAssertions.CsvFields(lines[0]);
        Assert.DoesNotContain(header, h => h.Contains("categ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(header, h => h.Contains("birth", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(header, h => h.Contains("id", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(header, h => h.Contains("house", StringComparison.OrdinalIgnoreCase));
        foreach (var line in lines[1..])
        {
            Assert.DoesNotContain(CsvAssertions.CsvFields(line), f => f is "M" or "F" or "S");
            Assert.DoesNotContain(CsvAssertions.CsvFields(line),
                f => f is "BANKSIA" or "WATTLE" or "MOUNTAIN ASH" or "GREVILLEA");
        }
    }

    // ---- Audit --------------------------------------------------------------

    [Fact]
    public async Task The_export_writes_one_audit_row_naming_the_district_report()
    {
        var seeded = await FinalisedAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await _factory.CreateClientAs("district-exporter", "CarnivalAdministrator")
                .GetAsync(Path(seeded.CarnivalId))).StatusCode);

        var row = Assert.Single(await ExportAuditAsync(seeded.CarnivalId));
        Assert.Equal("district-exporter", row.ActorSubject);
        Assert.Contains("\"report\":\"district\"", row.NewValuesJson!, StringComparison.Ordinal);
        Assert.Contains("\"rows\":4", row.NewValuesJson!, StringComparison.Ordinal);
    }

    private Task<List<AuditEvent>> ExportAuditAsync(Guid carnivalId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.ReportExport
                        && a.EntityId == carnivalId.ToString()
                        && a.NewValuesJson!.Contains("district"))
            .ToListAsync());
}
