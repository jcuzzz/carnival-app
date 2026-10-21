using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0032 §1: a promoted batch does not keep a second copy of the roster. The
/// staged rows and the row-issue payload both name students and STKEYs —
/// including rejected and departed rows — and once promotion has written the
/// snapshots they are duplicate personal data with no owner and no retention
/// decision. What survives is the batch counts and the promotion audit row.
/// </summary>
public sealed class StagedRosterPurgeTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private HttpClient Admin => _factory.CreateClientAs("purge-admin", "SystemAdministrator");

    private sealed record Staged(Guid Id, string Status);

    /// <summary>One promotable row, one row that never promotes (unknown status).</summary>
    private static MultipartFormDataContent Roster(string stkey)
    {
        var csv = new StringBuilder(
            "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n")
            .Append($"{stkey},Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n")
            .Append($"{stkey}-X,Nguyen,Bo,,Bo,Mr,M,WATTLE,WEIRD,2013-01-20,07A,07\n");
        return new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv.ToString())), "file", "roster.csv" },
        };
    }

    private async Task<Guid> StageAsync(HttpClient admin, string stkey)
    {
        using var form = Roster(stkey);
        var staged = await admin.PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.Created, staged.StatusCode);
        return (await staged.Content.ReadFromJsonAsync<Staged>())!.Id;
    }

    private Task<ImportBatch> BatchAsync(Guid id) => CarnivalSeed.QueryAsync(
        _factory, db => db.ImportBatches.AsNoTracking().SingleAsync(b => b.Id == id));

    [Fact]
    public async Task An_unpromoted_batch_keeps_its_staged_rows()
    {
        // Reconciliation is what the reviewer is looking at when they decide, so
        // the rows have to be there right up to the moment promotion succeeds.
        var admin = Admin;
        var id = await StageAsync(admin, $"K{Guid.NewGuid():N}"[..8]);

        Assert.NotNull((await BatchAsync(id)).StagedRowsJson);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/v1/imports/{id}/reconciliation")).StatusCode);

        // Approval is not promotion: still kept.
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/v1/imports/{id}/approve", new { approve = true })).StatusCode);
        Assert.NotNull((await BatchAsync(id)).StagedRowsJson);
    }

    [Fact]
    public async Task Promotion_discards_the_staged_rows_and_the_row_issues()
    {
        var admin = Admin;
        var id = await StageAsync(admin, $"P{Guid.NewGuid():N}"[..8]);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync($"/api/v1/imports/{id}/approve", new { approve = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync($"/api/v1/imports/{id}/promote", null)).StatusCode);

        var batch = await BatchAsync(id);
        Assert.Null(batch.StagedRowsJson);
        Assert.Null(batch.ValidationResultsJson);

        // The counts are the retained evidence, and they are untouched.
        Assert.Equal(ImportBatchStatus.Promoted, batch.Status);
        Assert.Equal(1, batch.NewCount);
        Assert.Equal(1, batch.RejectedCount);
        Assert.NotNull(batch.PromotedAtUtc);
    }

    [Fact]
    public async Task The_promotion_audit_row_still_says_what_happened()
    {
        // The other half of "the counts and the audit row are the evidence" — if
        // this went with the rows, the purge would be a deletion, not minimisation.
        var admin = Admin;
        var id = await StageAsync(admin, $"A{Guid.NewGuid():N}"[..8]);
        await admin.PostAsJsonAsync($"/api/v1/imports/{id}/approve", new { approve = true });
        await admin.PostAsync($"/api/v1/imports/{id}/promote", null);

        var audited = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.ImportPromote && a.ImportBatchId == id)
            .ToListAsync());

        var row = Assert.Single(audited);
        Assert.Contains("\"promoted\"", row.NewValuesJson!, StringComparison.Ordinal);
        Assert.Equal("purge-admin", row.ActorSubject);
    }

    [Fact]
    public async Task Reading_a_promoted_batchs_reconciliation_says_the_rows_were_discarded()
    {
        // The import worked and the copy was deliberately let go.
        var admin = Admin;
        var id = await StageAsync(admin, $"R{Guid.NewGuid():N}"[..8]);
        await admin.PostAsJsonAsync($"/api/v1/imports/{id}/approve", new { approve = true });
        await admin.PostAsync($"/api/v1/imports/{id}/promote", null);

        var response = await admin.GetAsync($"/api/v1/imports/{id}/reconciliation");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ImportRowsPurged);

        // Validation reads back as an empty document rather than a refusal — the
        // batch metadata (name, hash, mapping version) is still evidence.
        var validation = await admin.GetAsync($"/api/v1/imports/{id}/validation");
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        Assert.DoesNotContain("STKEY", await validation.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_batch_list_is_unchanged_by_the_purge()
    {
        // The list is the screen a system administrator actually reads; it was
        // always metadata-only, and that is now the whole retained record.
        var admin = Admin;
        var id = await StageAsync(admin, $"L{Guid.NewGuid():N}"[..8]);
        await admin.PostAsJsonAsync($"/api/v1/imports/{id}/approve", new { approve = true });
        await admin.PostAsync($"/api/v1/imports/{id}/promote", null);

        var listed = (await (await admin.GetAsync("/api/v1/imports"))
                .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == id);

        Assert.Equal("Promoted", listed.GetProperty("status").GetString());
        Assert.Equal("roster.csv", listed.GetProperty("sourceFileName").GetString());
        Assert.Equal(1, listed.GetProperty("newCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("rejectedCount").GetInt32());
    }

    [Fact]
    public async Task Rejection_discards_the_staged_rows_and_keeps_the_batch_record()
    {
        var admin = Admin;
        var id = await StageAsync(admin, $"J{Guid.NewGuid():N}"[..8]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/imports/{id}/approve", new { approve = false, note = "wrong file" })).StatusCode);

        var batch = await BatchAsync(id);
        Assert.Null(batch.StagedRowsJson);
        Assert.Null(batch.ValidationResultsJson);
        Assert.Equal(ImportBatchStatus.Rejected, batch.Status);
        Assert.Equal("roster.csv", batch.SourceFileName);
        Assert.Equal(1, batch.NewCount);
        Assert.Equal("wrong file", batch.ReviewNote);

        var reconciliation = await admin.GetAsync($"/api/v1/imports/{id}/reconciliation");
        Assert.Equal(HttpStatusCode.Conflict, reconciliation.StatusCode);
        await reconciliation.AssertErrorAsync(ErrorCodes.ImportRowsPurged);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/v1/imports/{id}/validation")).StatusCode);
    }

    [Fact]
    public async Task A_batch_purged_by_age_answers_expired_on_review_approve_and_promote()
    {
        var admin = Admin;
        var validated = await StageAsync(admin, $"E{Guid.NewGuid():N}"[..8]);
        var approved = await StageAsync(admin, $"F{Guid.NewGuid():N}"[..8]);
        await admin.PostAsJsonAsync($"/api/v1/imports/{approved}/approve", new { approve = true });

        var later = new ManualTime { Now = DateTimeOffset.UtcNow + TimeSpan.FromDays(8) };
        await CarnivalSeed.QueryAsync(_factory, db => new RetentionSweep(db, later).RunAsync());

        var reconciliation = await admin.GetAsync($"/api/v1/imports/{validated}/reconciliation");
        Assert.Equal(HttpStatusCode.Conflict, reconciliation.StatusCode);
        await reconciliation.AssertErrorAsync(ErrorCodes.ImportBatchExpired);

        var approve = await admin.PostAsJsonAsync(
            $"/api/v1/imports/{validated}/approve", new { approve = true });
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        await approve.AssertErrorAsync(ErrorCodes.ImportBatchExpired);
        Assert.Equal(ImportBatchStatus.Validated, (await BatchAsync(validated)).Status);

        var promote = await admin.PostAsync($"/api/v1/imports/{approved}/promote", null);
        Assert.Equal(HttpStatusCode.Conflict, promote.StatusCode);
        await promote.AssertErrorAsync(ErrorCodes.ImportBatchExpired);
        Assert.Equal(ImportBatchStatus.Approved, (await BatchAsync(approved)).Status);
    }

    [Fact]
    public async Task A_batch_purged_by_age_can_still_be_rejected()
    {
        var admin = Admin;
        var id = await StageAsync(admin, $"X{Guid.NewGuid():N}"[..8]);
        var later = new ManualTime { Now = DateTimeOffset.UtcNow + TimeSpan.FromDays(8) };
        await CarnivalSeed.QueryAsync(_factory, db => new RetentionSweep(db, later).RunAsync());

        var reject = await admin.PostAsJsonAsync(
            $"/api/v1/imports/{id}/approve", new { approve = false, note = "expired" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var batch = await BatchAsync(id);
        Assert.Equal(ImportBatchStatus.Rejected, batch.Status);
        Assert.Null(batch.StagedRowsJson);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.ImportReject && a.ImportBatchId == id)));
    }
}

public sealed class ImportMappingUnavailableTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"mapping-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"columns":{"STKEY":{"required":true}},"ageGroupRule":{"cutOff":"31-12"}}""")]
    [InlineData("""{"columns":"STKEY"}""")]
    [InlineData("""{"columns":{"STKEY":null},"ageGroupRule":{"cutOff":"12-31"}}""")]
    [InlineData("""{"columns":{"STKEY":{},"stkey":{}},"ageGroupRule":{"cutOff":"12-31"}}""")]
    public async Task A_malformed_mapping_file_answers_mapping_unavailable(string content)
    {
        await File.WriteAllTextAsync(_path, content);
        using var factory = new ApiTestFactory().With("Imports:MappingPath", _path);
        var admin = factory.CreateClientAs("mapping-admin", "SystemAdministrator");

        var defaults = await admin.GetAsync("/api/v1/imports/defaults");
        Assert.Equal(HttpStatusCode.InternalServerError, defaults.StatusCode);
        await defaults.AssertErrorAsync(ErrorCodes.ImportMappingUnavailable);

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("STKEY\nA1\n")), "file", "roster.csv" },
        };
        var staged = await admin.PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.InternalServerError, staged.StatusCode);
        await staged.AssertErrorAsync(ErrorCodes.ImportMappingUnavailable);
    }
}
