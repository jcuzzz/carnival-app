using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Reports and controlled exports (ADR-0004 tier 1): who may take one, what the
/// file actually contains, and the audit row that says it left.
///
/// The seeded carnival is small and deliberate — two heats of one Competitive
/// division plus one Social division — because the three things worth pinning
/// are all shape: each heat mints its own 1st (ADR-0015 §1), Social rows are
/// marked and carry no place (ADR-0014 §4), and no student's CATEGORY appears
/// anywhere (ADR-0026 §2).
/// </summary>
public sealed class ReportEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private const string CommaName = "Brook, Aiden \"AJ\"";

    // One host for the class; every test seeds its OWN carnival, so the audit
    // and lifecycle assertions stay isolated without a host each.
    private readonly ApiTestFactory _factory = factory;
    private Guid _carnivalId;
    private string _freeCode = "";

    private HttpClient Admin => _factory.CreateClientAs("report-admin", "CarnivalAdministrator");

    // ---- Seed -------------------------------------------------------------

    /// <summary>One swim: heat, who, which house, what happened.</summary>
    private sealed record Swim(int? Heat, string Name, string House, ResultStatus Status, decimal? Value);

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var n = Guid.NewGuid().ToString("N")[..8];

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Preston Swimming",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 3, 6),
            Status = CarnivalStatus.Open,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = now,
        };
        db.Carnivals.Add(carnival);

        void AddEvent(string code, string name, EventTier tier, params Swim[] swims)
        {
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = code,
                Name = name,
                Sport = "Swimming",
                MeasureType = MeasureType.Time,
                Units = "s",
                CreatedAtUtc = now,
            };
            db.EventDefinitions.Add(definition);
            var carnivalEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnival.Id,
                EventDefinitionId = definition.Id,
                Category = CompetitionCategory.M,
                Tier = tier,
                Status = CarnivalEventStatus.Open,
            };
            carnivalEvent.AgeGroup = "13";
            db.CarnivalEvents.Add(carnivalEvent);

            foreach (var swim in swims)
            {
                var student = new Student
                {
                    Id = Guid.CreateVersion7(),
                    ExternalStudentId = $"S-{Guid.NewGuid():N}",
                    Surname = "X",
                    FirstName = "Y",
                    IsActive = true,
                    CreatedAtUtc = now,
                };
                db.Students.Add(student);
                var entry = new Entry
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalEventId = carnivalEvent.Id,
                    StudentId = student.Id,
                    Heat = swim.Heat,
                    SnapshotDisplayName = swim.Name,
                    SnapshotYearLevel = 7,
                    SnapshotHouseCode = swim.House,
                    SnapshotAgeGroup = "13",
                    SnapshotCategory = CompetitionCategory.M,
                    CreatedAtUtc = now,
                    CreatedBySubject = "seed",
                };
                db.Entries.Add(entry);
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    Status = swim.Status,
                    MeasuredValue = swim.Value,
                    LifecycleState = ResultLifecycleState.Verified,
                    CurrentRevision = 1,
                    RecordedBySubject = "seed",
                    RecordedAtUtc = now,
                    CreatedAtUtc = now,
                });
            }
        }

        _freeCode = $"SW-50-FREE-{n}";
        // Two heats of one division. Heat 2's winner is slower than heat 1's
        // runner-up and still takes 1st — the ADR-0015 §1 property.
        AddEvent(_freeCode, "Freestyle 50m", EventTier.Competitive,
            new Swim(1, CommaName, "BANKSIA", ResultStatus.Ok, 31.2m),
            new Swim(1, "Blake Chen", "WATTLE", ResultStatus.Ok, 31.8m),
            new Swim(2, "Cora Diaz", "MOUNTAIN ASH", ResultStatus.Ok, 35.0m),
            new Swim(2, "Dale Egan", "GREVILLEA", ResultStatus.Dns, null));
        AddEvent($"SW-NOVELTY-{n}", "Novelty splash", EventTier.Social,
            new Swim(null, "Emery Frost", "GREVILLEA", ResultStatus.Ok, 40.0m));

        await db.SaveChangesAsync();
        _carnivalId = carnival.Id;
    }

    // ---- Helpers ----------------------------------------------------------

    private string Path(string report) => $"/api/v1/carnivals/{_carnivalId}/reports/{report}";

    // ---- Authorisation ----------------------------------------------------

    public static TheoryData<string> Reports => new() { "results", "standings.csv", "results.csv" };

    [Theory]
    [MemberData(nameof(Reports))]
    public async Task Unauthenticated_requests_get_401(string report)
    {
        await SeedAsync();
        var response = await _factory.CreateClient().GetAsync(Path(report));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AuthenticationRequired);
    }

    [Theory]
    [InlineData("Viewer", "results")]
    [InlineData("Viewer", "standings.csv")]
    [InlineData("Viewer", "results.csv")]
    [InlineData("Recorder", "results")]
    [InlineData("Recorder", "standings.csv")]
    [InlineData("Recorder", "results.csv")]
    public async Task A_viewer_or_recorder_may_not_export(string role, string report)
    {
        // Watching the scoreboard (GET …/standings, ViewCarnivals) is not
        // permission to take a file of student names off the platform.
        await SeedAsync();
        var response = await _factory.CreateClientAs($"report-{role}", role).GetAsync(Path(report));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.Forbidden);

        // A refusal is not an export: no row may claim a file left the platform.
        Assert.Empty(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.ReportExport
                        && a.EntityId == _carnivalId.ToString())
            .ToListAsync()));
    }

    [Theory]
    [InlineData("CarnivalAdministrator", "results")]
    [InlineData("CarnivalAdministrator", "standings.csv")]
    [InlineData("CarnivalAdministrator", "results.csv")]
    [InlineData("SystemAdministrator", "results")]
    [InlineData("SystemAdministrator", "standings.csv")]
    [InlineData("SystemAdministrator", "results.csv")]
    public async Task An_administrator_may_export(string role, string report)
    {
        await SeedAsync();
        var response = await _factory.CreateClientAs($"report-{role}", role).GetAsync(Path(report));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_carnival_is_a_404_not_an_empty_book()
    {
        await SeedAsync();
        var response = await Admin.GetAsync($"/api/v1/carnivals/{Guid.CreateVersion7()}/reports/results.csv");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotFound);
    }

    // ---- The results book -------------------------------------------------

    [Fact]
    public async Task The_results_csv_prints_one_row_per_result_in_event_heat_place_order()
    {
        await SeedAsync();
        var lines = await (await Admin.GetAsync(Path("results.csv"))).CsvLinesAsync();

        // The three trailing columns are the ADR-0056 card and its countback
        // numbers; every row of a non-Height division leaves them empty.
        Assert.Equal(
            "Event code,Event,Division,Tier,Heat,Place,Name,House,Result,Status," +
            "Series,Fails at best,Total fails",
            lines[0]);
        // Heat 1, in place order — the name carrying a comma AND a quote is
        // quoted, and the quote doubled (RFC 4180).
        Assert.Equal(
            $"{_freeCode},Freestyle 50m,13 Boys,Competitive,1,1,\"Brook, Aiden \"\"AJ\"\"\",BANKSIA,31.20,OK,,,",
            lines[1]);
        Assert.Equal(
            $"{_freeCode},Freestyle 50m,13 Boys,Competitive,1,2,Blake Chen,WATTLE,31.80,OK,,,",
            lines[2]);
        // Heat 2's winner is SLOWER than heat 1's runner-up and still places 1st:
        // two heats, two firsts (ADR-0015 §1).
        Assert.Equal(
            $"{_freeCode},Freestyle 50m,13 Boys,Competitive,2,1,Cora Diaz,MOUNTAIN ASH,35.00,OK,,,",
            lines[3]);
        // A no-show has no time and no place, and still appears — the book is a
        // record of the division, not of the finishers.
        Assert.Equal(
            $"{_freeCode},Freestyle 50m,13 Boys,Competitive,2,,Dale Egan,GREVILLEA,,DNS,,,",
            lines[4]);
        // Social: marked by its tier, unheated, and NOT placed — a Social event
        // has no placings at all (ADR-0014 §4).
        Assert.EndsWith(
            ",Novelty splash,13 Boys,Social,,,Emery Frost,GREVILLEA,40.00,OK,,,", lines[5],
            StringComparison.Ordinal);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public async Task No_column_carries_a_students_category()
    {
        // ADR-0026 §2: the division's label ("13 Boys") is the only
        // category-shaped value in the report. Every entry above is category M,
        // so a leak would show up as a column of bare M's.
        await SeedAsync();
        var lines = await (await Admin.GetAsync(Path("results.csv"))).CsvLinesAsync();

        Assert.DoesNotContain(CsvAssertions.CsvFields(lines[0]), h => h.Contains("categ", StringComparison.OrdinalIgnoreCase));
        foreach (var line in lines[1..])
            Assert.DoesNotContain(CsvAssertions.CsvFields(line), f => f is "M" or "F" or "S");
    }

    [Fact]
    public async Task The_results_report_reads_the_same_rows_on_screen()
    {
        await SeedAsync();
        var response = await Admin.GetAsync(Path("results"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Preston Swimming", report.GetProperty("carnivalName").GetString());
        Assert.True(report.GetProperty("provisional").GetBoolean());
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(
            new int?[] { 1, 2, 1, null, null },
            rows.Select(r => r.GetProperty("place") is { ValueKind: JsonValueKind.Number } p
                ? p.GetInt32()
                : (int?)null));
        Assert.Equal("Social", rows[^1].GetProperty("tier").GetString());
        // The printed value is rounded; the badge compares the raw number beside
        // it (ADR-0028 §5), which a non-result does not have.
        Assert.Equal("31.20", rows[0].GetProperty("value").GetString());
        Assert.Equal(31.2m, rows[0].GetProperty("measuredValue").GetDecimal());
        Assert.Equal(JsonValueKind.Null, rows[3].GetProperty("measuredValue").ValueKind);
    }

    // ---- High jump in the book (ADR-0056 §7) ------------------------------

    [Fact]
    public async Task The_book_prints_the_card_and_the_two_countback_numbers()
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, category: null, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == seeded.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");
        var recorder = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        var eventPath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";
        foreach (var height in new[] { 1.20m, 1.25m })
            Assert.Equal(HttpStatusCode.OK,
                (await recorder.PostAsJsonAsync($"{eventPath}/heights", new { height })).StatusCode);

        async Task<Guid> EnterAsync(Guid studentId) =>
            (await (await recorder.PostAsJsonAsync($"{eventPath}/entries", new { studentId }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        async Task JumpAsync(Guid entryId, decimal height, string mark)
        {
            var request = new HttpRequestMessage(HttpMethod.Put,
                $"{eventPath}/entries/{entryId}/jumps/" +
                height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            {
                Content = JsonContent.Create(new { mark }),
                Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
            };
            Assert.True((await recorder.SendAsync(request)).IsSuccessStatusCode);
        }

        var clean = await EnterAsync(seeded.EligibleBoyId);
        var scrappy = await EnterAsync(seeded.GirlId);
        await JumpAsync(clean, 1.20m, "-");
        await JumpAsync(clean, 1.25m, "O");
        foreach (var height in new[] { 1.20m, 1.25m })
        {
            await JumpAsync(scrappy, height, "X");
            await JumpAsync(scrappy, height, "O");
        }

        var report = await (await Admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results")).Content
            .ReadFromJsonAsync<JsonElement>();
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([1, 2], rows.Select(r => r.GetProperty("place").GetInt32()));
        Assert.Equal(0, rows[0].GetProperty("failsAtBest").GetInt32());
        Assert.Equal(0, rows[0].GetProperty("totalFails").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("failsAtBest").GetInt32());
        Assert.Equal(2, rows[1].GetProperty("totalFails").GetInt32());
        // ADR-0056 amendment 2026-09-25: a division from before the setting reads 3.
        Assert.Equal(3, rows[0].GetProperty("attemptsPerHeight").GetInt32());

        var lines = await (await Admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results.csv")).CsvLinesAsync();
        Assert.EndsWith("1.25 m,OK,1.20 - · 1.25 O,0,0", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("1.25 m,OK,1.20 XO · 1.25 XO,1,2", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_books_total_column_counts_fails_only_to_the_last_cleared_height()
    {
        // ADR-0056 amendment 2026-09-20: the Total column is the countback figure,
        // so the printed number is the one the place was decided on.
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, category: null, measureType: MeasureType.Height);
        var recorder = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        var eventPath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";
        foreach (var height in new[] { 1.20m, 1.25m, 1.30m })
            Assert.Equal(HttpStatusCode.OK,
                (await recorder.PostAsJsonAsync($"{eventPath}/heights", new { height })).StatusCode);

        async Task<Guid> EnterAsync(Guid studentId) =>
            (await (await recorder.PostAsJsonAsync($"{eventPath}/entries", new { studentId }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        async Task JumpAsync(Guid entryId, decimal height, string mark)
        {
            var request = new HttpRequestMessage(HttpMethod.Put,
                $"{eventPath}/entries/{entryId}/jumps/" +
                height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            {
                Content = JsonContent.Create(new { mark }),
                Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
            };
            Assert.True((await recorder.SendAsync(request)).IsSuccessStatusCode);
        }

        var contestedTheNextBar = await EnterAsync(seeded.EligibleBoyId);
        var stoppedAtTheBest = await EnterAsync(seeded.GirlId);
        await JumpAsync(contestedTheNextBar, 1.20m, "O");
        await JumpAsync(contestedTheNextBar, 1.25m, "O");
        foreach (var _ in new[] { 1, 2, 3 }) await JumpAsync(contestedTheNextBar, 1.30m, "X");
        await JumpAsync(stoppedAtTheBest, 1.20m, "X");
        await JumpAsync(stoppedAtTheBest, 1.20m, "O");
        await JumpAsync(stoppedAtTheBest, 1.25m, "O");

        var report = await (await Admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results")).Content
            .ReadFromJsonAsync<JsonElement>();
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([1, 2], rows.Select(r => r.GetProperty("place").GetInt32()));
        Assert.Equal(0, rows[0].GetProperty("totalFails").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("totalFails").GetInt32());

        var lines = await (await Admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results.csv")).CsvLinesAsync();
        Assert.EndsWith("1.25 m,OK,1.20 O · 1.25 O · 1.30 XXX,0,0", lines[1],
            StringComparison.Ordinal);
        Assert.EndsWith("1.25 m,OK,1.20 XO · 1.25 O,0,1", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_height_division_holding_a_legacy_series_still_prints_it_by_shape()
    {
        // ADR-0056, amended 2026-09-17: the book reads the BLOB's shape, never the
        // event's mode, so a series captured on a Height event before the
        // amendment keeps printing beside the cards that follow it.
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        var legacy = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.EligibleBoyId);
        var card = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.SecondEligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.Results.Add(Recorded(legacy, 1.30m, """[{"value":1.30},{"foul":true}]"""));
            db.Results.Add(Recorded(card, 1.25m, """[{"height":1.25,"marks":"XO"}]"""));
        });

        var lines = await (await Admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results.csv")).CsvLinesAsync();
        var rows = lines[1..].Select(CsvAssertions.CsvFields).ToList();
        var legacyRow = Assert.Single(rows, f => f[8] == "1.30 m");
        var cardRow = Assert.Single(rows, f => f[8] == "1.25 m");

        // The typed height still ranks on its value, and prints no card columns.
        Assert.Equal("1", legacyRow[5]);
        Assert.Equal(["", "", ""], legacyRow[10..13]);
        Assert.Equal("2", cardRow[5]);
        Assert.Equal(["1.25 XO", "1", "1"], cardRow[10..13]);
    }

    private static Result Recorded(Guid entryId, decimal measured, string attemptsJson) => new()
    {
        Id = Guid.CreateVersion7(),
        EntryId = entryId,
        Status = ResultStatus.Ok,
        MeasuredValue = measured,
        AttemptsJson = attemptsJson,
        LifecycleState = ResultLifecycleState.Verified,
        CurrentRevision = 1,
        RecordedBySubject = "seed",
        RecordedAtUtc = DateTimeOffset.UtcNow,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    // ---- Formula injection ------------------------------------------------

    [Fact]
    public async Task A_formula_leading_event_name_or_student_name_is_neutralised()
    {
        // RFC 4180 quoting is not enough: Excel and Sheets EVALUATE a cell that
        // opens with =/+/-/@, so a name or event title is executable in a file
        // designed to leave the school. A leading apostrophe forces it to text.
        await SeedAsync();
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.EventDefinitions.Single(d => d.Code == _freeCode).Name = "=SUM(A1:A9)";
            db.Entries.Single(e => e.SnapshotDisplayName == "Blake Chen"
                                   && e.CarnivalEvent.CarnivalId == _carnivalId)
                .SnapshotDisplayName = "-Blake Chen";
        });

        var lines = await (await Admin.GetAsync(Path("results.csv"))).CsvLinesAsync();

        Assert.Equal(
            $"{_freeCode},'=SUM(A1:A9),13 Boys,Competitive,1,1,\"Brook, Aiden \"\"AJ\"\"\",BANKSIA,31.20,OK,,,",
            lines[1]);
        Assert.Equal(
            $"{_freeCode},'=SUM(A1:A9),13 Boys,Competitive,1,2,'-Blake Chen,WATTLE,31.80,OK,,,",
            lines[2]);
    }

    [Fact]
    public async Task A_negative_bonus_is_still_a_number()
    {
        // The other half of the rule: ADR-0005 allows a deduction, so a leading
        // '-' on a NUMERIC field must survive — quoting it as text would turn the
        // standings column a school sums into a column of strings.
        await SeedAsync();
        var admin = Admin;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{_carnivalId}/adjustments",
            new
            {
                houseCode = "BANKSIA",
                points = -3,
                category = "Best Dressed House",
                reason = "deduction seed",
            })).StatusCode);

        var lines = await (await admin.GetAsync(Path("standings.csv"))).CsvLinesAsync();
        var banksia = Assert.Single(
            lines[1..].Select(CsvAssertions.CsvFields), f => f[1] == "BANKSIA");

        Assert.Equal("-3", banksia[4]); // bonus points — NOT "'-3"
        Assert.Equal("1", banksia[5]);  // 4 placement points less the deduction
    }

    // ---- Standings --------------------------------------------------------

    [Fact]
    public async Task The_standings_csv_carries_the_components_the_total_is_made_of()
    {
        await SeedAsync();
        var admin = Admin;
        var awarded = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{_carnivalId}/adjustments",
            new
            {
                houseCode = "BANKSIA",
                points = 5,
                category = "Best Dressed House",
                reason = "report seed",
            });
        Assert.Equal(HttpStatusCode.Created, awarded.StatusCode);

        var lines = await (await admin.GetAsync(Path("standings.csv"))).CsvLinesAsync();

        Assert.Equal(
            [
                "Rank,House,Placement points,Participation points,Bonus points,Total",
                // 1st of heat 1 (4) + the 5-point award, kept as separate components.
                "1,BANKSIA,4,0,5,9",
                // Heat 2's winner: slower than WATTLE, ahead of it on the board.
                "2,MOUNTAIN ASH,4,0,0,4",
                "3,WATTLE,3,0,0,3",
                // A DNS scores nothing; the Social entry's flat point is participation.
                "4,GREVILLEA,0,1,0,1",
            ],
            lines);
    }

    [Fact]
    public async Task The_standings_csv_writes_the_shared_rank_the_screen_shows()
    {
        await SeedAsync();

        var lines = await (await Admin.GetAsync(Path("standings.csv"))).CsvLinesAsync();

        Assert.Equal(
            [
                "Rank,House,Placement points,Participation points,Bonus points,Total",
                "1,BANKSIA,4,0,0,4",
                "1,MOUNTAIN ASH,4,0,0,4",
                "3,WATTLE,3,0,0,3",
                "4,GREVILLEA,0,1,0,1",
            ],
            lines);
    }

    // ---- Audit ------------------------------------------------------------

    [Theory]
    [InlineData("standings.csv", "standings")]
    [InlineData("results.csv", "results")]
    public async Task Every_export_writes_one_audit_row_naming_the_report(string report, string named)
    {
        await SeedAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await _factory.CreateClientAs("exporter", "CarnivalAdministrator")
                .GetAsync(Path(report))).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.ReportExport
                        && a.EntityId == _carnivalId.ToString())
            .ToListAsync());

        var row = Assert.Single(rows);
        Assert.Equal("exporter", row.ActorSubject);
        Assert.Equal(nameof(Carnival), row.EntityType);
        Assert.Contains($"\"report\":\"{named}\"", row.NewValuesJson);
        Assert.Contains("\"format\":\"csv\"", row.NewValuesJson);
    }

    [Fact]
    public async Task Reading_the_book_on_screen_is_not_an_export()
    {
        // The audit row is evidence that a FILE left the platform. A screen read
        // is not that, and auditing it would bury the exports in noise.
        await SeedAsync();
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetAsync(Path("results"))).StatusCode);

        Assert.Empty(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.ReportExport
                        && a.EntityId == _carnivalId.ToString())
            .ToListAsync()));
    }

    // ---- Carnival status --------------------------------------------------

    [Fact]
    public async Task A_finalised_carnival_still_exports()
    {
        // Reports are read-only, so no lifecycle gate applies — and a finalised
        // carnival is precisely the one a school prints from.
        await SeedAsync();
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == _carnivalId).Status = CarnivalStatus.Finalised);

        var lines = await (await Admin.GetAsync(Path("results.csv"))).CsvLinesAsync();
        Assert.Equal(6, lines.Length);
    }
}
