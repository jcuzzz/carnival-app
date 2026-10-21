using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Two devices add the same late swimmer. The loser's create passes every
/// existence check and then loses the <c>(CarnivalEventId, StudentId)</c> unique
/// index at the insert. That landed in <see cref="IdempotencyService"/>'s catch
/// — written for a race on the KEY — whose re-read found no winning key row and
/// threw, so the device saw a retryable 500 and its drain stopped, where
/// <c>409 ENTRY_EXISTS</c> was available all along (ADR-0002/ADR-0011).
///
/// <see cref="RaceInterceptor"/> puts the rival insert immediately before ours,
/// on the transaction the keyed path has already opened.
/// </summary>
public sealed class EntryCreateRaceTests : IDisposable
{
    private const string EntryInsert = "INSERT INTO \"Entries\"";

    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _recorder;

    public EntryCreateRaceTests() => _recorder = _factory.CreateClientAs("race-marshal", "Recorder");

    public void Dispose() => _factory.Dispose();

    private void ArmRivalEntry(Guid eventId, Guid studentId) =>
        _factory.Race.ArmOnce(EntryInsert, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            var snapshot = db.RosterSnapshots.Single(r => r.StudentId == studentId && r.IsCurrent);
            db.Entries.Add(new Entry
            {
                Id = Guid.CreateVersion7(),
                CarnivalEventId = eventId,
                StudentId = studentId,
                RosterSnapshotId = snapshot.Id,
                SnapshotDisplayName = "Rival Entry",
                SnapshotYearLevel = snapshot.YearLevel,
                SnapshotHouseCode = snapshot.HouseCode,
                SnapshotAgeGroup = snapshot.CompetitionAgeGroup,
                SnapshotCategory = snapshot.CompetitionCategory,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "rival-device",
            });
            db.SaveChanges();
        });

    private Task<HttpResponseMessage> CreateEntryAsync(SeededCarnival seeded, Guid studentId, Guid? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries")
        {
            Content = JsonContent.Create(new { studentId }),
        };
        if (key is Guid supplied) request.Headers.Add(IdempotencyService.HeaderName, supplied.ToString());
        return _recorder.SendAsync(request);
    }

    [Fact]
    public async Task A_queued_create_that_loses_the_insert_race_is_409_not_500()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        ArmRivalEntry(seeded.EventId, seeded.EligibleBoyId);

        var response = await CreateEntryAsync(seeded, seeded.EligibleBoyId, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EntryExists);
    }

    [Fact]
    public async Task An_online_create_that_loses_the_insert_race_is_409_too()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        ArmRivalEntry(seeded.EventId, seeded.SecondEligibleBoyId);

        var response = await CreateEntryAsync(seeded, seeded.SecondEligibleBoyId, key: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EntryExists);
    }

    /// <summary>
    /// ADR-0002: a refusal burns nothing, so the device keeps the item and its
    /// key. (What the retry then answers is not assertable here — the rival write
    /// rides the handler's own transaction and rolls back with it, see
    /// <see cref="RaceInterceptor"/>.)
    /// </summary>
    [Fact]
    public async Task The_losing_key_is_not_burned()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var key = Guid.CreateVersion7();
        ArmRivalEntry(seeded.EventId, seeded.EligibleBoyId);

        var response = await CreateEntryAsync(seeded, seeded.EligibleBoyId, key);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Empty(await CarnivalSeed.QueryAsync(_factory,
            db => db.IdempotencyKeys.Where(k => k.Key == key).ToListAsync()));
    }
}
