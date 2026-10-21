using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Idempotency;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0002 server dedup contract: key + payload hash + stored response
/// inserted in the same transaction as the mutation, for SUCCESSFUL (2xx)
/// outcomes only; retries replay the stored response; same key with a
/// different payload is a distinct error; any non-2xx rolls back and leaves
/// the key unburned so a paused queue item can retry.
/// </summary>
public sealed class IdempotencyServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SportsDbContext> _options;

    public IdempotencyServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SportsDbContext>().UseSqlite(_connection).Options;
        using var db = new SportsDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private SportsDbContext NewDb() => new(_options);

    private static Func<CancellationToken, Task<(int, string)>> CreateStudent(
        SportsDbContext db, string key, Func<int>? counter = null) => async _ =>
    {
        counter?.Invoke();
        db.Students.Add(new Student
        {
            ExternalStudentId = key, Surname = "Idem", FirstName = "Potent",
            IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (201, $$"""{"id":"{{key}}"}""");
    };

    [Fact]
    public async Task First_call_runs_the_mutation_and_stores_key_and_response_atomically()
    {
        var key = Guid.NewGuid();
        await using (var db = NewDb())
        {
            var outcome = await new IdempotencyService(db).ExecuteAsync(
                key, IdempotencyService.HashPayload("payload-1"), "official-1", "device-9",
                CreateStudent(db, "S1"));
            Assert.Equal(201, outcome.StatusCode);
            Assert.False(outcome.Replayed);
        }

        await using var verify = NewDb();
        Assert.Single(await verify.Students.ToListAsync());
        var stored = Assert.Single(await verify.IdempotencyKeys.ToListAsync());
        Assert.Equal(key, stored.Key);
        Assert.Equal(201, stored.ResponseStatusCode);
        Assert.Equal("""{"id":"S1"}""", stored.ResponseBody);
        Assert.Equal("official-1", stored.ActorSubject);
        Assert.Equal("device-9", stored.DeviceId);
    }

    [Fact]
    public async Task Retry_with_same_key_and_payload_replays_the_stored_response_without_rerunning()
    {
        var key = Guid.NewGuid();
        var hash = IdempotencyService.HashPayload("payload-1");
        var runs = 0;
        await using var db = NewDb();
        var service = new IdempotencyService(db);
        await service.ExecuteAsync(key, hash, "official-1", null, CreateStudent(db, "S1", () => runs++));

        var retry = await service.ExecuteAsync(key, hash, "official-1", null, CreateStudent(db, "S1", () => runs++));

        Assert.True(retry.Replayed);
        Assert.Equal(201, retry.StatusCode);
        Assert.Equal("""{"id":"S1"}""", retry.Body);
        Assert.Equal(1, runs);                                   // mutation ran exactly once
        Assert.Single(await db.Students.ToListAsync());          // duplicate structurally impossible
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_a_distinct_422_error_never_a_replay()
    {
        var key = Guid.NewGuid();
        var runs = 0;
        await using var db = NewDb();
        var service = new IdempotencyService(db);
        await service.ExecuteAsync(key, IdempotencyService.HashPayload("payload-1"), "official-1", null,
            CreateStudent(db, "S1", () => runs++));

        var clash = await service.ExecuteAsync(key, IdempotencyService.HashPayload("DIFFERENT"), "official-1", null,
            CreateStudent(db, "S2", () => runs++));

        Assert.Equal(422, clash.StatusCode);
        Assert.False(clash.Replayed);
        Assert.Contains(ErrorCodes.IdempotencyPayloadMismatch, clash.Body);
        Assert.Equal(1, runs);
        Assert.Single(await db.Students.ToListAsync());
    }

    [Fact]
    public async Task Server_error_outcomes_are_rolled_back_and_not_recorded_so_the_client_can_retry()
    {
        var key = Guid.NewGuid();
        var hash = IdempotencyService.HashPayload("payload-1");
        await using var db = NewDb();
        var service = new IdempotencyService(db);

        var failure = await service.ExecuteAsync(key, hash, "official-1", null, async ct =>
        {
            db.Students.Add(new Student
            {
                ExternalStudentId = "S1", Surname = "Half", FirstName = "Done",
                IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return (500, """{"error":"transient"}""");
        });
        Assert.Equal(500, failure.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Students.ToListAsync());           // mutation rolled back
        Assert.Empty(await db.IdempotencyKeys.ToListAsync());    // key not burned

        var retry = await service.ExecuteAsync(key, hash, "official-1", null, CreateStudent(db, "S1"));
        Assert.Equal(201, retry.StatusCode);
        Assert.False(retry.Replayed);
    }

    // The failure mode that strands offline work: a queued submission the
    // server refuses because of state it can only see now (event still
    // Scheduled → 409; captured before the roster snapshot existed → 422).
    // Those refusals are transient, so they must not be recorded — the same
    // key has to work once the state changes (ADR-0002 pause-never-drop).
    [Theory]
    [InlineData(409, """{"error":"EVENT_NOT_OPEN","detail":"Results are only accepted while the event is open."}""")]
    [InlineData(422, """{"error":"ROSTER_SNAPSHOT_MISSING","detail":"The student has no current roster snapshot."}""")]
    public async Task Business_refusals_are_rolled_back_and_leave_the_key_unburned(int status, string body)
    {
        var key = Guid.NewGuid();
        var hash = IdempotencyService.HashPayload("payload-1");
        await using var db = NewDb();
        var service = new IdempotencyService(db);

        var refused = await service.ExecuteAsync(key, hash, "official-1", null, async ct =>
        {
            db.Students.Add(new Student
            {
                ExternalStudentId = "S1", Surname = "Half", FirstName = "Done",
                IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);                       // e.g. an audit row written before the refusal
            return (status, body);
        });
        Assert.Equal(status, refused.StatusCode);
        Assert.False(refused.Replayed);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Students.ToListAsync());           // partial work rolled back
        Assert.Empty(await db.IdempotencyKeys.ToListAsync());    // key not burned

        // The state changed (event opened / snapshot imported) and the client
        // retried the very same queued item: it re-executes and succeeds.
        var runs = 0;
        var retry = await service.ExecuteAsync(key, hash, "official-1", null,
            CreateStudent(db, "S1", () => runs++));
        Assert.Equal(201, retry.StatusCode);
        Assert.False(retry.Replayed);
        Assert.Equal(1, runs);

        // ...and from then on the stored 2xx replays as normal.
        var replay = await service.ExecuteAsync(key, hash, "official-1", null,
            CreateStudent(db, "S1", () => runs++));
        Assert.True(replay.Replayed);
        Assert.Equal(201, replay.StatusCode);
        Assert.Equal("""{"id":"S1"}""", replay.Body);
        Assert.Equal(1, runs);                                   // never re-ran after success
        Assert.Single(await db.Students.ToListAsync());
    }

    /// <summary>
    /// Commits a rival IdempotencyKey row in the exact race window: after
    /// ExecuteAsync's existence check, before its transaction opens. A second
    /// writer cannot commit any later than that under SQLite — EF opens the
    /// transaction with BEGIN IMMEDIATE, which holds the write lock for its
    /// whole life — so this interceptor is the seam that makes the race
    /// deterministic instead of a deadlock.
    /// </summary>
    private sealed class CommitRivalBeforeTransaction(Func<Task> commitRival) : DbTransactionInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await commitRival();
            }
            return result;
        }
    }

    // Two devices drain the same queued item at once. The loser's INSERT hits
    // the primary key, so it must re-read the winner's row and apply the replay
    // contract rather than surface a 500. Needs a genuinely separate writer, so
    // a file-backed database rather than the shared in-memory connection.
    [Fact]
    public async Task Concurrent_insert_race_re_reads_the_winner_and_replays_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idem-race-{Guid.NewGuid():N}.db");
        var connectionString = $"DataSource={path};Pooling=False";
        var key = Guid.NewGuid();
        var hash = IdempotencyService.HashPayload("payload-1");

        var winnerOptions = new DbContextOptionsBuilder<SportsDbContext>().UseSqlite(connectionString).Options;
        var loserOptions = new DbContextOptionsBuilder<SportsDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new CommitRivalBeforeTransaction(async () =>
            {
                await using var winner = new SportsDbContext(winnerOptions);
                winner.IdempotencyKeys.Add(new IdempotencyKey
                {
                    Key = key,
                    PayloadHash = hash,
                    ResponseStatusCode = 201,
                    ResponseBody = """{"id":"WINNER"}""",
                    ActorSubject = "official-2",
                    DeviceId = "device-winner",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                await winner.SaveChangesAsync();
            }))
            .Options;

        try
        {
            await using (var create = new SportsDbContext(winnerOptions))
                await create.Database.EnsureCreatedAsync();

            var runs = 0;
            await using var db = new SportsDbContext(loserOptions);
            var loser = await new IdempotencyService(db).ExecuteAsync(key, hash, "official-1", "device-loser",
                async ct =>
                {
                    runs++;
                    db.Students.Add(new Student
                    {
                        ExternalStudentId = "LOSER", Surname = "Race", FirstName = "Lost",
                        IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
                    });
                    await db.SaveChangesAsync(ct);
                    return (201, """{"id":"LOSER"}""");
                });

            Assert.Equal(1, runs);                               // the loser did run — it lost at commit
            Assert.True(loser.Replayed);
            Assert.Equal(201, loser.StatusCode);
            Assert.Equal("""{"id":"WINNER"}""", loser.Body);     // winner's response, not the loser's

            db.ChangeTracker.Clear();
            Assert.Empty(await db.Students.ToListAsync());       // loser's mutation rolled back — no duplicate
            var stored = Assert.Single(await db.IdempotencyKeys.ToListAsync());
            Assert.Equal("device-winner", stored.DeviceId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Exception_in_the_mutation_leaves_no_key_and_no_partial_write()
    {
        var key = Guid.NewGuid();
        await using var db = NewDb();
        var service = new IdempotencyService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(
            key, IdempotencyService.HashPayload("p"), "official-1", null, async ct =>
            {
                db.Students.Add(new Student
                {
                    ExternalStudentId = "S1", Surname = "Boom", FirstName = "Boom",
                    IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("handler blew up");
            }));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Students.ToListAsync());
        Assert.Empty(await db.IdempotencyKeys.ToListAsync());
    }

    /// <summary>
    /// The DbUpdateException catch is written for one thing — losing a race on
    /// the KEY — but it also wrapped the action, so a constraint the MUTATION
    /// broke landed there too, found no winning key row to replay, and threw an
    /// InvalidOperationException the client saw as a retryable 500. The original
    /// exception must surface instead, for the endpoint to answer as a conflict.
    /// </summary>
    [Fact]
    public async Task A_constraint_broken_by_the_mutation_surfaces_instead_of_a_failed_replay()
    {
        var key = Guid.NewGuid();
        await using var db = NewDb();

        await Assert.ThrowsAsync<DbUpdateException>(() => new IdempotencyService(db).ExecuteAsync(
            key, IdempotencyService.HashPayload("p"), "official-1", null,
            _ => throw new DbUpdateException("the mutation's own unique-index violation")));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.IdempotencyKeys.ToListAsync());
    }
}

/// <summary>
/// The reusable endpoint filter exercised end-to-end over HTTP with a demo
/// mutation endpoint (result endpoints, its eventual consumers, do not exist
/// yet). Hosted on a minimal in-process app, not the production Program.
/// </summary>
public sealed class IdempotencyEndpointFilterTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private bool _gateOpen;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<SportsDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddScoped<IdempotencyService>();

        _app = builder.Build();
        _app.UseIdempotencyBuffering();
        _app.MapPost("/demo", async (DemoSubmission submission, SportsDbContext db) =>
        {
            db.Students.Add(new Student
            {
                ExternalStudentId = submission.ExternalStudentId,
                Surname = "Demo", FirstName = "Demo",
                IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Created($"/demo/{submission.ExternalStudentId}", new { submission.ExternalStudentId });
        }).AddEndpointFilter<IdempotencyEndpointFilter>();

        // Stands in for a state-dependent business refusal (event not open yet).
        _app.MapPost("/demo-gated", async (DemoSubmission submission, SportsDbContext db) =>
        {
            if (!_gateOpen)
                return ApiError.Conflict(ErrorCodes.EventNotOpen, "The event is not open for results.");
            db.Students.Add(new Student
            {
                ExternalStudentId = submission.ExternalStudentId,
                Surname = "Gated", FirstName = "Demo",
                IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Created($"/demo-gated/{submission.ExternalStudentId}", new { submission.ExternalStudentId });
        }).AddEndpointFilter<IdempotencyEndpointFilter>();

        await _app.StartAsync();
        using (var scope = _app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<SportsDbContext>().Database.EnsureCreated();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _connection.Dispose();
    }

    private sealed record DemoSubmission(string ExternalStudentId);

    private HttpRequestMessage Request(Guid key, string studentId) => new(HttpMethod.Post, "/demo")
    {
        Content = JsonContent.Create(new DemoSubmission(studentId)),
        Headers = { { IdempotencyService.HeaderName, key.ToString() } },
    };

    [Fact]
    public async Task Missing_idempotency_key_header_is_a_400()
    {
        var response = await _client.PostAsJsonAsync("/demo", new DemoSubmission("D0"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.IdempotencyKeyRequired);
    }

    [Fact]
    public async Task Retry_over_http_replays_the_stored_response_and_does_not_duplicate()
    {
        var key = Guid.NewGuid();
        var first = await _client.SendAsync(Request(key, "D1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());

        var retry = await _client.SendAsync(Request(key, "D1"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Single(await db.Students.Where(s => s.ExternalStudentId == "D1").ToListAsync());
    }

    // C1: over HTTP, a 409 refusal is relayed but not recorded, so the queued
    // item keeps its key and succeeds once the state changes.
    [Fact]
    public async Task Business_refusal_over_http_does_not_burn_the_key()
    {
        var key = Guid.NewGuid();
        var request = () => new HttpRequestMessage(HttpMethod.Post, "/demo-gated")
        {
            Content = JsonContent.Create(new DemoSubmission("D3")),
            Headers = { { IdempotencyService.HeaderName, key.ToString() } },
        };

        var refused = await _client.SendAsync(request());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("false", refused.Headers.GetValues("Idempotency-Replayed").Single());

        _gateOpen = true;
        var retry = await _client.SendAsync(request());
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("false", retry.Headers.GetValues("Idempotency-Replayed").Single());

        var replay = await _client.SendAsync(request());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Single(await db.Students.Where(s => s.ExternalStudentId == "D3").ToListAsync());
    }

    [Fact]
    public async Task Same_key_different_payload_over_http_is_422()
    {
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(Request(key, "D2"))).StatusCode);

        var clash = await _client.SendAsync(Request(key, "D2-CHANGED"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        await clash.AssertErrorAsync(ErrorCodes.IdempotencyPayloadMismatch);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Empty(await db.Students.Where(s => s.ExternalStudentId == "D2-CHANGED").ToListAsync());
    }
}
