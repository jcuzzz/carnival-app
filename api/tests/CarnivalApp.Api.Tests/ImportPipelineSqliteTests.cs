using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Full pipeline (stage → approve → promote) against a relational provider
/// that runs everywhere (SQLite in-memory). The schema is provider-clean by
/// ADR-0001, so this exercises the same model; PostgreSQL-specific behaviour
/// (migrations, transaction isolation) is covered by the Testcontainers tests.
/// </summary>
public sealed class ImportPipelineSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SportsDbContext> _options;

    public ImportPipelineSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SportsDbContext>().UseSqlite(_connection).Options;
        using var db = new SportsDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private SportsDbContext NewDb() => new(_options);

    private static MemoryStream Csv(params string[] dataLines)
    {
        var text = "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
                   + string.Join("\n", dataLines) + "\n";
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private async Task<ImportBatch> StageApprove(params string[] lines)
    {
        await using var db = NewDb();
        var service = Fixtures.ImportService(db);
        var outcome = await service.StageAsync(Csv(lines), "test.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
        Assert.Empty(outcome.StructuralErrors);
        return await service.ApproveAsync(outcome.Batch.Id, true, null, Fixtures.Reviewer);
    }

    [Fact]
    public async Task Promotion_creates_students_and_current_snapshots_for_actv_rows_only()
    {
        var batch = await StageApprove(
            "A1,Smith,Ann,,Annie,Miss,F,WATTLE,ACTV,2013-01-20,07A,07",
            "B2,Jones,Bo,,Bo,Mr,M,BANKSIA,ACTV,2011-06-01,09B,09",
            "L3,Left,Lee,,Lee,Mx,,,LEFT,,,",
            "D4,Deleted,Dee,,Dee,,,S12,DEL,,,");

        await using var db = NewDb();
        await Fixtures.ImportService(db).PromoteAsync(batch.Id, Fixtures.Importer);

        var students = await db.Students.Include(s => s.RosterSnapshots).ToListAsync();
        Assert.Equal(4, students.Count);

        var ann = students.Single(s => s.ExternalStudentId == "A1");
        Assert.True(ann.IsActive);
        var snap = Assert.Single(ann.RosterSnapshots);
        Assert.True(snap.IsCurrent);
        Assert.Equal("13", snap.CompetitionAgeGroup);
        Assert.Equal(CompetitionCategory.F, snap.CompetitionCategory);
        Assert.Equal("WATTLE", snap.HouseCode);
        Assert.Equal(2026, snap.SchoolYear);

        // Departed students: inactive identity references, no snapshot.
        var lee = students.Single(s => s.ExternalStudentId == "L3");
        Assert.False(lee.IsActive);
        Assert.Empty(lee.RosterSnapshots);
        Assert.Empty(students.Single(s => s.ExternalStudentId == "D4").RosterSnapshots);

        var promoted = await db.ImportBatches.SingleAsync(b => b.Id == batch.Id);
        Assert.Equal(ImportBatchStatus.Promoted, promoted.Status);
        Assert.NotNull(promoted.PromotedAtUtc);
    }

    [Fact]
    public async Task Second_import_supersedes_current_snapshot_without_rewriting_history()
    {
        var first = await StageApprove("A1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07");
        await using (var db = NewDb())
            await Fixtures.ImportService(db).PromoteAsync(first.Id, Fixtures.Importer);

        // Student moved house and year level.
        var second = await StageApprove("A1,Smith,Ann,,Ann,Miss,F,BANKSIA,ACTV,2013-01-20,08A,08");
        await using (var db2 = NewDb())
        {
            var doc = RosterImportService.GetDocument(
                await db2.ImportBatches.SingleAsync(b => b.Id == second.Id));
            Assert.Equal(RowDisposition.Changed, doc.Rows.Single().Disposition);
            await Fixtures.ImportService(db2).PromoteAsync(second.Id, Fixtures.Importer);
        }

        await using var verify = NewDb();
        var snapshots = await verify.RosterSnapshots.OrderBy(s => s.YearLevel).ToListAsync();
        Assert.Equal(2, snapshots.Count);                       // history preserved
        Assert.False(snapshots[0].IsCurrent);
        Assert.True(snapshots[1].IsCurrent);
        Assert.Equal("BANKSIA", snapshots[1].HouseCode);
        Assert.Single(await verify.Students.ToListAsync());     // same identity
    }

    [Fact]
    public async Task Identical_reimport_reconciles_as_unchanged()
    {
        const string line = "A1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07";
        var first = await StageApprove(line);
        await using (var db = NewDb())
            await Fixtures.ImportService(db).PromoteAsync(first.Id, Fixtures.Importer);

        await using var db2 = NewDb();
        var outcome = await Fixtures.ImportService(db2)
            .StageAsync(Csv(line), "again.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
        Assert.Equal(RowDisposition.Unchanged, outcome.Document.Rows.Single().Disposition);
    }

    [Fact]
    public async Task Active_students_missing_from_the_file_are_reported_unmatched()
    {
        var first = await StageApprove("A1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07");
        await using (var db = NewDb())
            await Fixtures.ImportService(db).PromoteAsync(first.Id, Fixtures.Importer);

        await using var db2 = NewDb();
        var outcome = await Fixtures.ImportService(db2).StageAsync(
            Csv("B2,Jones,Bo,,Bo,Mr,M,BANKSIA,ACTV,2011-06-01,09B,09"),
            "next.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
        var unmatched = Assert.Single(outcome.Document.UnmatchedStudents);
        Assert.Equal("A1", unmatched.ExternalStudentId);
        Assert.Equal(1, outcome.Batch.UnmatchedCount);
    }

    [Fact]
    public async Task Rejected_rows_are_excluded_from_promotion_but_do_not_fail_the_batch()
    {
        // Blank house on an ACTV student — the known real-data edge case.
        var batch = await StageApprove(
            "A1,Smith,Ann,,Ann,Miss,F,,ACTV,2013-01-20,07A,07",
            "B2,Jones,Bo,,Bo,Mr,M,BANKSIA,ACTV,2011-06-01,09B,09");
        Assert.Equal(1, batch.RejectedCount);

        await using var db = NewDb();
        await Fixtures.ImportService(db).PromoteAsync(batch.Id, Fixtures.Importer);
        Assert.Single(await db.Students.ToListAsync()); // only B2 promoted
        Assert.Equal("B2", (await db.Students.SingleAsync()).ExternalStudentId);
    }

    [Fact]
    public async Task Promote_requires_approval_and_approve_requires_validated()
    {
        await using var db = NewDb();
        var service = Fixtures.ImportService(db);
        var outcome = await service.StageAsync(
            Csv("A1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07"),
            "test.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);

        var ex = await Assert.ThrowsAsync<ImportPipelineException>(() => service.PromoteAsync(outcome.Batch.Id, Fixtures.Importer));
        Assert.Equal(409, ex.StatusCode);

        var rejected = await service.ApproveAsync(outcome.Batch.Id, false, "not this one", Fixtures.Reviewer);
        Assert.Equal(ImportBatchStatus.Rejected, rejected.Status);
        await Assert.ThrowsAsync<ImportPipelineException>(
            () => service.ApproveAsync(outcome.Batch.Id, true, null, Fixtures.Reviewer));
        await Assert.ThrowsAsync<ImportPipelineException>(() => service.PromoteAsync(outcome.Batch.Id, Fixtures.Importer));
    }

    [Fact]
    public async Task Missing_required_columns_fail_validation_at_intake()
    {
        await using var db = NewDb();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("FOO,BAR\n1,2\n"));
        var outcome = await Fixtures.ImportService(db)
            .StageAsync(stream, "bad.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
        Assert.Equal(ImportBatchStatus.ValidationFailed, outcome.Batch.Status);
        Assert.NotEmpty(outcome.StructuralErrors);
        await Assert.ThrowsAsync<ImportPipelineException>(
            () => Fixtures.ImportService(db).ApproveAsync(outcome.Batch.Id, true, null, Fixtures.Reviewer));
    }

    [Fact]
    public async Task Full_clean_fixture_stages_and_promotes_end_to_end()
    {
        await using (var db = NewDb())
        {
            var service = Fixtures.ImportService(db);
            await using var stream = File.OpenRead(Fixtures.Path("ST_CLEAN.csv"));
            var outcome = await service.StageAsync(stream, "ST_CLEAN.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
            Assert.Empty(outcome.StructuralErrors);
            Assert.Equal(883, outcome.Batch.NewCount);
            Assert.Equal(4314, outcome.Batch.InactiveCount);
            Assert.Equal(1, outcome.Batch.RejectedCount);
            await service.ApproveAsync(outcome.Batch.Id, true, null, Fixtures.Reviewer);
            await service.PromoteAsync(outcome.Batch.Id, Fixtures.Importer);
        }

        await using var verify = NewDb();
        Assert.Equal(883, await verify.RosterSnapshots.CountAsync(s => s.IsCurrent));
        Assert.Equal(883, await verify.Students.CountAsync(s => s.IsActive));
        Assert.Equal(4314, await verify.Students.CountAsync(s => !s.IsActive));
        // The unresolved blank-house student is not created at all yet.
        Assert.Equal(0, await verify.Students.CountAsync(s => s.ExternalStudentId == "SYN4899572274"));
    }
}

/// <summary>
/// GET /imports/{id}/validation serves a TYPED document (ADR-0012): the client
/// generates its shape from OpenAPI, so every key is camelCase — including the
/// row issues, which the untyped relay used to emit PascalCase.
/// </summary>
public sealed class ImportValidationDocumentTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("validation-admin", "SystemAdministrator");

    private static MultipartFormDataContent Roster(params string[] dataLines)
    {
        var csv = "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
                  + string.Join("\n", dataLines) + "\n";
        return new MultipartFormDataContent
        {
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "roster.csv" },
        };
    }

    private async Task<Guid> StageAsync(HttpClient admin, params string[] dataLines)
    {
        using var form = Roster(dataLines);
        var staged = await admin.PostAsync("/api/v1/imports", form);
        Assert.Equal(System.Net.HttpStatusCode.Created, staged.StatusCode);
        return System.Text.Json.JsonDocument.Parse(await staged.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<System.Text.Json.JsonElement> ValidationAsync(HttpClient admin, Guid id)
    {
        var response = await admin.GetAsync($"/api/v1/imports/{id}/validation");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.Clone();
    }

    [Fact]
    public async Task Row_issues_are_served_with_camel_case_property_names()
    {
        var admin = Admin;
        var key = $"D{Guid.NewGuid():N}"[..8];
        var id = await StageAsync(admin,
            $"{key},Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07",
            $"{key},Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07");

        var report = await ValidationAsync(admin, id);
        Assert.True(report.TryGetProperty("validation", out var validation));
        Assert.True(validation.TryGetProperty("structuralErrors", out _));
        Assert.True(validation.TryGetProperty("rowIssues", out var rowIssues));

        var row = rowIssues.EnumerateArray().First();
        Assert.True(row.TryGetProperty("rowNumber", out var rowNumber));
        Assert.Equal(2, rowNumber.GetInt32());
        Assert.True(row.TryGetProperty("externalStudentId", out _));
        Assert.True(row.TryGetProperty("issues", out var issues));

        var issue = issues.EnumerateArray().First();
        Assert.True(issue.TryGetProperty("code", out var code));
        Assert.Equal("DUPLICATE_STKEY", code.GetString());
        Assert.True(issue.TryGetProperty("severity", out var severity));
        Assert.Equal("Error", severity.GetString());
        Assert.True(issue.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task A_blob_stored_in_the_old_mixed_case_form_still_reads_back()
    {
        // Every batch stored before the document was typed is mixed-case: the
        // outer keys camelCase, the rows PascalCase. The read is case-insensitive
        // so those batches keep opening.
        var admin = Admin;
        var id = await StageAsync(admin, $"M{Guid.NewGuid():N}"[..8] + ",Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07");
        await CarnivalSeed.MutateAsync(factory, db =>
            db.ImportBatches.Single(b => b.Id == id).ValidationResultsJson =
                """
                {"structuralErrors":[],"rowIssues":[{"RowNumber":2,"ExternalStudentId":"X",
                "Issues":[{"Code":"MISSING_HOUSE","Severity":"Exception","Message":"m"}]}]}
                """);

        var validation = (await ValidationAsync(admin, id)).GetProperty("validation");
        var row = validation.GetProperty("rowIssues").EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("rowNumber").GetInt32());
        Assert.Equal("X", row.GetProperty("externalStudentId").GetString());
        var issue = row.GetProperty("issues").EnumerateArray().Single();
        Assert.Equal("MISSING_HOUSE", issue.GetProperty("code").GetString());
        Assert.Equal("Exception", issue.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task An_unreadable_blob_reads_as_no_document_rather_than_a_fault()
    {
        var admin = Admin;
        var id = await StageAsync(admin, $"U{Guid.NewGuid():N}"[..8] + ",Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07");
        await CarnivalSeed.MutateAsync(factory, db =>
            db.ImportBatches.Single(b => b.Id == id).ValidationResultsJson = "{not json");

        var report = await ValidationAsync(admin, id);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, report.GetProperty("validation").ValueKind);
    }
}
