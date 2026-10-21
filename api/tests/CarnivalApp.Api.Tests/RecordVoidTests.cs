using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The records-administration surface (ADR-0035): the one audited fix path for
/// a record minted in error. Void, never delete — the row and its evidence stay,
/// and every read that resolves "the current record" stops seeing it.
/// </summary>
public sealed class RecordVoidTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private static string BoysAndGirls(params string[] bands) =>
        RecordsSeed.Categories(bands, CompetitionCategory.M, CompetitionCategory.F);

    private HttpClient Administrator => _factory.CreateClientAs("records-custodian", "CarnivalAdministrator");

    private async Task<List<JsonElement>> BookAsync()
    {
        var response = await _factory.CreateClientAs("records-viewer", "Viewer").GetAsync("/api/v1/records");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private async Task<List<JsonElement>> CustodyAsync()
    {
        var response = await Administrator.GetAsync("/api/v1/records/administration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static JsonElement Row(IEnumerable<JsonElement> rows, string code) =>
        Assert.Single(rows, r => r.GetProperty("eventCode").GetString() == code);

    private static Guid IdOf(JsonElement row) => Guid.Parse(row.GetProperty("id").GetString()!);

    private Task<HttpResponseMessage> VoidAsync(Guid id, string? reason) =>
        Administrator.PostAsJsonAsync($"/api/v1/records/{id}/void", new { reason });

    /// <summary>One carnival, one Boys 13 division, one recorded swim, finalised.</summary>
    private async Task<SeededRecords> MintAsync(
        string key, decimal value, string holder = "Aiden Brook", SeededRecords? reusing = null,
        DateOnly? date = null)
    {
        var seeded = await new RecordsSeed()
            .Definition(key, BoysAndGirls("13"))
            .Division(key, "13", CompetitionCategory.M, new RecordsSeed.Swim(holder, "BANKSIA", value))
            .BuildAsync(_factory, reusing: reusing, date: date);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        return seeded;
    }

    // ---- The fix path ------------------------------------------------------

    [Fact]
    public async Task A_voided_record_leaves_the_book_and_stays_visible_to_its_custodian()
    {
        var seeded = await MintAsync("SW-VOID", 20.01m, "Typo Swimmer");
        var code = seeded.DefinitionCodes["SW-VOID"];
        var id = IdOf(Row(await BookAsync(), code));

        var response = await VoidAsync(id, "Timekeeper's sheet read 20.01; the swim was 30.01.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var voided = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(voided.GetProperty("voided").GetBoolean());
        Assert.Equal("records-custodian", voided.GetProperty("voidedBySubject").GetString());
        Assert.Contains("30.01", voided.GetProperty("voidReason").GetString()!, StringComparison.Ordinal);

        // Gone from the wall poster…
        Assert.DoesNotContain(await BookAsync(), r => r.GetProperty("eventCode").GetString() == code);
        // …and still on the custody screen, which is the whole point of voiding
        // rather than deleting.
        var kept = Row(await CustodyAsync(), code);
        Assert.True(kept.GetProperty("voided").GetBoolean());
        Assert.Equal("Typo Swimmer", kept.GetProperty("holderDisplayName").GetString());
        Assert.NotNull(kept.GetProperty("voidedAtUtc").GetString());
    }

    [Fact]
    public async Task The_row_is_never_deleted()
    {
        var seeded = await MintAsync("SW-KEPT", 21.02m);
        var id = IdOf(Row(await BookAsync(), seeded.DefinitionCodes["SW-KEPT"]));
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(id, "Mistimed.")).StatusCode);

        var row = await CarnivalSeed.QueryAsync(_factory, db => db.SchoolRecords.AsNoTracking()
            .FirstAsync(r => r.Id == id));
        Assert.NotNull(row);
        Assert.NotNull(row.VoidedAtUtc);
        Assert.Equal("records-custodian", row.VoidedBySubject);
        // Status is NOT rewritten: voiding is a second axis over the lifecycle,
        // so the row still says what it was when it was minted.
        Assert.Equal(SchoolRecordStatus.Ratified, row.Status);
        Assert.Null(row.EffectiveTo);
    }

    [Fact]
    public async Task Voiding_the_standing_record_puts_the_one_it_superseded_back()
    {
        var first = await MintAsync("SW-CHAIN", 31.2m, "Aiden Brook");
        var second = await MintAsync("SW-CHAIN", 20.4m, "Typo Swimmer",
            reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.NotEqual(first.CarnivalId, second.CarnivalId);

        var code = first.DefinitionCodes["SW-CHAIN"];
        var typo = Row(await BookAsync(), code);
        Assert.Equal("Typo Swimmer", typo.GetProperty("holderDisplayName").GetString());

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(IdOf(typo), "Impossible time.")).StatusCode);

        // Without this the category empties and the next carnival seeds off a
        // slower time — a WRONG record, which is what voiding exists to prevent.
        var restored = Row(await BookAsync(), code);
        Assert.Equal("Aiden Brook", restored.GetProperty("holderDisplayName").GetString());
        Assert.Equal(31.2m, restored.GetProperty("value").GetDecimal());
        Assert.Equal("2027-03-06", restored.GetProperty("setOn").GetString());
    }

    [Fact]
    public async Task An_already_voided_predecessor_is_skipped_and_the_category_empties()
    {
        var first = await MintAsync("SW-BOTH", 31.2m, "Aiden Brook");
        var second = await MintAsync("SW-BOTH", 20.4m, "Typo Swimmer",
            reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.NotEqual(first.CarnivalId, second.CarnivalId);
        var code = first.DefinitionCodes["SW-BOTH"];
        var definitionId = first.DefinitionIds["SW-BOTH"];

        // The superseded row is not on any read — voiding it needs its id from
        // the database, which is the surface gap ADR-0035 records.
        var supersededId = await CarnivalSeed.QueryAsync(_factory, db => db.SchoolRecords.AsNoTracking()
            .Where(r => r.EventDefinitionId == definitionId
                        && r.Status == SchoolRecordStatus.Superseded)
            .Select(r => r.Id)
            .SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(supersededId, "Also mistimed.")).StatusCode);
        // It was already out of the book, so nothing moved.
        Assert.Equal("Typo Swimmer",
            Row(await BookAsync(), code).GetProperty("holderDisplayName").GetString());

        var typoId = IdOf(Row(await BookAsync(), code));
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(typoId, "Impossible time.")).StatusCode);

        // The walk skips the voided ancestor and finds no live one, so the
        // category is EMPTY until a finalisation re-mints it.
        Assert.DoesNotContain(await BookAsync(), r => r.GetProperty("eventCode").GetString() == code);
        Assert.Equal(2, (await CustodyAsync()).Count(r => r.GetProperty("eventCode").GetString() == code));
    }

    [Fact]
    public async Task A_voided_record_is_neither_beaten_nor_a_blocker_at_the_next_finalisation()
    {
        var first = await MintAsync("SW-SEED", 20.05m, "Typo Swimmer");
        var code = first.DefinitionCodes["SW-SEED"];
        Assert.Equal(HttpStatusCode.OK,
            (await VoidAsync(IdOf(Row(await BookAsync(), code)), "Impossible time.")).StatusCode);
        Assert.DoesNotContain(await BookAsync(), r => r.GetProperty("eventCode").GetString() == code);

        // A SLOWER but legitimate swim: it could never beat the voided 20.05, so
        // if detection still saw that row nothing would mint at all.
        var second = await MintAsync("SW-SEED", 31.7m, "Blake Chen",
            reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.NotEqual(first.CarnivalId, second.CarnivalId);

        var seededAgain = Row(await BookAsync(), code);
        Assert.Equal("Blake Chen", seededAgain.GetProperty("holderDisplayName").GetString());
        Assert.Equal(31.7m, seededAgain.GetProperty("value").GetDecimal());
    }

    // ---- Refusals (ADR-0011 shape) ----------------------------------------

    [Fact]
    public async Task A_second_void_is_a_conflict_not_a_silent_success()
    {
        var seeded = await MintAsync("SW-TWICE", 22.03m);
        var id = IdOf(Row(await BookAsync(), seeded.DefinitionCodes["SW-TWICE"]));
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(id, "Mistimed.")).StatusCode);

        var again = await VoidAsync(id, "Mistimed again.");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.SchoolRecordAlreadyVoided);
    }

    [Fact]
    public async Task An_unknown_record_is_a_404()
    {
        var response = await VoidAsync(Guid.NewGuid(), "Mistimed.");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.SchoolRecordNotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_void_without_a_reason_is_refused(string? reason)
    {
        var seeded = await MintAsync("SW-WHY", 23.04m);
        var id = IdOf(Row(await BookAsync(), seeded.DefinitionCodes["SW-WHY"]));

        var response = await VoidAsync(id, reason);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ReasonRequired);
        // Refused means unchanged: the record is still in the book.
        Assert.Contains(await BookAsync(),
            r => r.GetProperty("eventCode").GetString() == seeded.DefinitionCodes["SW-WHY"]);
    }

    [Fact]
    public async Task An_over_long_reason_is_refused_rather_than_truncated()
    {
        var seeded = await MintAsync("SW-LONG", 24.05m);
        var id = IdOf(Row(await BookAsync(), seeded.DefinitionCodes["SW-LONG"]));

        var response = await VoidAsync(id, new string('x', 1001));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    // ---- Audit -------------------------------------------------------------

    [Fact]
    public async Task Voiding_writes_one_audit_row_carrying_what_stood_and_why_it_went()
    {
        var seeded = await MintAsync("SW-AUDIT", 25.06m, "Typo Swimmer");
        var id = IdOf(Row(await BookAsync(), seeded.DefinitionCodes["SW-AUDIT"]));
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(id, "Sheet misread.")).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.SchoolRecordVoid && a.EntityId == id.ToString())
            .ToListAsync());
        var audit = Assert.Single(rows);
        Assert.Equal("records-custodian", audit.ActorSubject);
        Assert.Equal(nameof(SchoolRecord), audit.EntityType);
        Assert.Equal("Sheet misread.", audit.Reason);
        Assert.Contains("Typo Swimmer", audit.PriorValuesJson!, StringComparison.Ordinal);
        Assert.Contains("25.06", audit.PriorValuesJson!, StringComparison.Ordinal);
        Assert.Contains("\"voided\":true", audit.NewValuesJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reinstatement_is_named_in_the_void_audit_row()
    {
        var first = await MintAsync("SW-BACK", 31.2m, "Aiden Brook");
        var second = await MintAsync("SW-BACK", 20.4m, "Typo Swimmer",
            reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.NotEqual(first.CarnivalId, second.CarnivalId);
        var typo = Row(await BookAsync(), first.DefinitionCodes["SW-BACK"]);
        var id = IdOf(typo);
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(id, "Impossible time.")).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.SchoolRecordVoid && a.EntityId == id.ToString())
            .ToListAsync());
        var audit = Assert.Single(rows);
        Assert.Contains("reinstatedRecordId", audit.NewValuesJson!, StringComparison.Ordinal);
        Assert.Contains("Aiden Brook", audit.NewValuesJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_void_writes_no_audit_row()
    {
        var seeded = await MintAsync("SW-NOAUDIT", 26.07m);
        var id = IdOf(Row(await BookAsync(), seeded.DefinitionCodes["SW-NOAUDIT"]));
        Assert.Equal(HttpStatusCode.BadRequest, (await VoidAsync(id, "  ")).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.SchoolRecordVoid && a.EntityId == id.ToString())
            .ToListAsync());
        Assert.Empty(rows);
    }

    // ---- The custody read --------------------------------------------------

    [Fact]
    public async Task The_custody_read_shows_what_stands_and_what_was_voided_and_nothing_else()
    {
        var first = await MintAsync("SW-LIST", 31.2m, "Aiden Brook");
        var second = await MintAsync("SW-LIST", 30.1m, "Blake Chen",
            reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.NotEqual(first.CarnivalId, second.CarnivalId);
        var code = first.DefinitionCodes["SW-LIST"];

        // One row: Aiden's is Superseded, which is the ORDINARY way out of the
        // book and not this screen's business.
        var standing = Row(await CustodyAsync(), code);
        Assert.Equal("Blake Chen", standing.GetProperty("holderDisplayName").GetString());
        Assert.False(standing.GetProperty("voided").GetBoolean());
        Assert.Null(standing.GetProperty("voidReason").GetString());

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(IdOf(standing), "Wrong lane.")).StatusCode);

        // Now two: the reinstated Aiden and the voided Blake.
        var rows = (await CustodyAsync())
            .Where(r => r.GetProperty("eventCode").GetString() == code)
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => !r.GetProperty("voided").GetBoolean()
                                   && r.GetProperty("holderDisplayName").GetString() == "Aiden Brook");
        Assert.Contains(rows, r => r.GetProperty("voided").GetBoolean()
                                   && r.GetProperty("voidReason").GetString() == "Wrong lane.");
    }

    [Fact]
    public async Task The_book_never_carries_who_voided_what()
    {
        var seeded = await MintAsync("SW-PRIV", 27.08m);
        var code = seeded.DefinitionCodes["SW-PRIV"];
        var row = Row(await BookAsync(), code);
        // Present on the shape, null on this route — voided rows never reach it.
        Assert.False(row.GetProperty("voided").GetBoolean());
        Assert.Null(row.GetProperty("voidedBySubject").GetString());

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(IdOf(row), "Mistimed.")).StatusCode);
        Assert.DoesNotContain(await BookAsync(), r => r.GetProperty("eventCode").GetString() == code);
    }
}
