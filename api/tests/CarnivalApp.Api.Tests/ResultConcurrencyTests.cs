using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// One result per entry is now a UNIQUE INDEX, not just a read-then-insert
/// check (ADR-0053 implementation notes): two officials at one pit both read
/// "no result yet" for an athlete and both created one, and the loser used to
/// come back as a 500 or a parked CONCURRENCY_CONFLICT. The attempt path
/// re-reads and re-applies once instead.
///
/// <see cref="RaceInterceptor"/> places the competing writer at
/// <see cref="ResultEndpoints.AttemptCreateTag"/> (inside the handler, before
/// the batch's savepoint, so the competitor SURVIVES the loser's rollback) or
/// immediately before an UPDATE (inside the savepoint, so it does not).
/// </summary>
public sealed class ResultConcurrencyTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _official;

    public ResultConcurrencyTests()
    {
        _admin = _factory.CreateClientAs("result-race-admin", "CarnivalAdministrator");
        _official = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
    }

    public void Dispose()
    {
        _factory.Race.Disarm();
        _factory.Dispose();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static string EventPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

    private async Task<Guid> EnterAsync(SeededCarnival seeded, Guid studentId)
    {
        var response = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/entries", new { studentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> RecordAttemptAsync(
        SeededCarnival seeded, Guid entryId, int ordinal, object body) =>
        _official.SendAsync(new HttpRequestMessage(
            HttpMethod.Put, $"{EventPath(seeded)}/entries/{entryId}/attempts/{ordinal}")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        });

    /// <summary>The series as the wire carries it: a mark, "X" for a foul, "-" for untaken.</summary>
    private static IReadOnlyList<string> Series(JsonElement body) =>
        [.. body.GetProperty("attempts").EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Null
            ? "-"
            : a.TryGetProperty("foul", out _) ? "X" : a.GetProperty("value").GetDecimal().ToString())];

    private Task<int> ResultCountAsync(Guid entryId) => CarnivalSeed.QueryAsync(_factory,
        db => db.Results.AsNoTracking().CountAsync(r => r.EntryId == entryId));

    /// <summary>The other official's request, on whatever transaction ours is inside.</summary>
    private void ArmCompetingWrite(string marker, Action<CarnivalAppDbContext> write) =>
        _factory.Race.ArmOnce(marker, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            write(db);
            db.SaveChanges();
        });

    private static Result TheirResult(Guid entryId, string attemptsJson, decimal? best) => new()
    {
        Id = Guid.CreateVersion7(),
        EntryId = entryId,
        AttemptsJson = attemptsJson,
        MeasuredValue = best,
        Status = ResultStatus.Ok,
        LifecycleState = ResultLifecycleState.Verified,
        CurrentRevision = 1,
        RecordedBySubject = "other-official",
        RecordedAtUtc = DateTimeOffset.UtcNow,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Two_attempts_for_one_entry_leave_one_result_and_the_second_appends()
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var created = await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var appended = await RecordAttemptAsync(seeded, entryId, 2, new { value = "4.10" });
        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);

        Assert.Equal(["4.32", "4.10"], Series(await Json(appended)));
        Assert.Equal(1, await ResultCountAsync(entryId));
    }

    [Fact]
    public async Task A_create_that_loses_the_unique_index_re_reads_and_appends()
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        // The other official's first attempt lands while ours is between "no
        // result yet" and its own INSERT. The unique index throws ours out.
        ArmCompetingWrite(ResultEndpoints.AttemptCreateTag,
            db => db.Results.Add(TheirResult(entryId, """[{"value":4.40}]""", 4.40m)));

        var response = await RecordAttemptAsync(seeded, entryId, 2, new { value = "4.10" });
        // Not a 500, and not a conflict the recorder has to think about: the
        // capture is good, so the re-read turns it into the append it always was.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["4.40", "4.10"], Series(await Json(response)));
        Assert.Equal(1, await ResultCountAsync(entryId));
    }

    [Fact]
    public async Task An_append_that_loses_the_revision_token_re_reads_and_re_applies()
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.Created,
            (await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" })).StatusCode);

        // A competing append bumps the concurrency token under ours, exactly as a
        // stale tracked entity would: our UPDATE then matches no row.
        ArmCompetingWrite("UPDATE \"Results\"", db =>
            db.Results.Single(r => r.EntryId == entryId).CurrentRevision++);

        var response = await RecordAttemptAsync(seeded, entryId, 3, new { value = "4.55" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["4.32", "-", "4.55"], Series(await Json(response)));
        Assert.Equal(1, await ResultCountAsync(entryId));
    }

    // ---- The card takes the same once-retry (ADR-0056 §2) -----------------

    private async Task<SeededCarnival> LadderEventAsync(params decimal[] heights)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == seeded.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");
        foreach (var height in heights)
            Assert.Equal(HttpStatusCode.OK,
                (await _official.PostAsJsonAsync($"{EventPath(seeded)}/heights", new { height })).StatusCode);
        return seeded;
    }

    private Task<HttpResponseMessage> JumpAsync(
        SeededCarnival seeded, Guid entryId, decimal height, string mark) =>
        _official.SendAsync(new HttpRequestMessage(
            HttpMethod.Put,
            $"{EventPath(seeded)}/entries/{entryId}/jumps/" +
            height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
        {
            Content = JsonContent.Create(new { mark }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        });

    private static string Card(JsonElement body) => string.Join(" · ",
        body.GetProperty("jumps").EnumerateArray().Select(
            j => $"{j.GetProperty("height").GetDecimal():0.00} {j.GetProperty("marks").GetString()}"));

    [Fact]
    public async Task A_first_mark_that_loses_the_unique_index_re_reads_and_appends()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        // The other official's first mark lands while ours is between "no result
        // yet" and its own INSERT.
        ArmCompetingWrite(ResultEndpoints.AttemptCreateTag,
            db => db.Results.Add(TheirResult(entryId, """[{"height":1.20,"marks":"O"}]""", 1.20m)));

        var response = await JumpAsync(seeded, entryId, 1.25m, "X");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.20 O · 1.25 X", Card(await Json(response)));
        Assert.Equal(1, await ResultCountAsync(entryId));
    }

    [Fact]
    public async Task A_mark_that_loses_the_revision_token_re_reads_and_re_applies()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.Created, (await JumpAsync(seeded, entryId, 1.20m, "O")).StatusCode);

        ArmCompetingWrite("UPDATE \"Results\"", db =>
            db.Results.Single(r => r.EntryId == entryId).CurrentRevision++);

        var response = await JumpAsync(seeded, entryId, 1.25m, "X");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.20 O · 1.25 X", Card(await Json(response)));
        Assert.Equal(1, await ResultCountAsync(entryId));
    }

    [Fact]
    public async Task A_submit_that_loses_the_unique_index_answers_result_exists()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        ArmCompetingWrite("INSERT INTO \"Results\"",
            db => db.Results.Add(TheirResult(entryId, null!, 34.56m)));

        var response = await _official.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.99" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        });

        // The same answer the read-then-check gives, so a device sees one rule
        // whichever side of the race it lost on.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ResultExists);
    }

    private async Task<HttpResponseMessage> SubmitFaultingAsync(SeededCarnival seeded, Exception fault)
    {
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        _factory.Race.ArmOnce("INSERT INTO \"Results\"", _ => throw fault);
        return await _official.SendAsync(new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.99" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        });
    }

    [Fact]
    public async Task A_submit_lost_to_a_serialisation_failure_is_a_concurrency_conflict_not_result_exists()
    {
        var response = await SubmitFaultingAsync(await CarnivalSeed.SeedAsync(_factory),
            new Npgsql.PostgresException("could not serialize access", "ERROR", "ERROR",
                Npgsql.PostgresErrorCodes.SerializationFailure));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);
    }

    [Fact]
    public async Task A_submit_lost_to_the_revision_token_is_a_concurrency_conflict()
    {
        var response = await SubmitFaultingAsync(await CarnivalSeed.SeedAsync(_factory),
            new DbUpdateConcurrencyException("token moved"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);
    }

    [Fact]
    public async Task Any_other_save_failure_on_submit_stays_a_retryable_500()
    {
        var response = await SubmitFaultingAsync(await CarnivalSeed.SeedAsync(_factory),
            new InvalidOperationException("disk gone"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.InternalError);
    }

    [Fact]
    public async Task The_database_refuses_a_second_result_for_one_entry()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(TheirResult(entryId, null!, 34.56m)));

        await Assert.ThrowsAsync<DbUpdateException>(() => CarnivalSeed.MutateAsync(
            _factory, db => db.Results.Add(TheirResult(entryId, null!, 35.00m))));
    }
}
