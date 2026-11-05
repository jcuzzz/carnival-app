using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Idempotency keys go 30 days after creation; an unpromoted batch's staged rows go 7 days after
/// upload. Everything else is untouched.
/// </summary>
public sealed class RetentionSweepTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CarnivalAppDbContext> _options;
    private readonly ManualTime _time = new();

    public RetentionSweepTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<CarnivalAppDbContext>().UseSqlite(_connection).Options;
        using var db = new CarnivalAppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private CarnivalAppDbContext NewDb() => new(_options);

    private DateTimeOffset Now => _time.Now;

    private async Task<RetentionOutcome> SweepAsync()
    {
        await using var db = NewDb();
        return await new RetentionSweep(db, _time).RunAsync();
    }

    private async Task<Guid> AddKeyAsync(DateTimeOffset createdAt)
    {
        var key = Guid.CreateVersion7();
        await using var db = NewDb();
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Key = key,
            PayloadHash = "hash",
            ResponseStatusCode = 201,
            ResponseBody = """{"displayName":"Synthetic Student"}""",
            ActorSubject = "recorder",
            CreatedAtUtc = createdAt,
        });
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<Guid> AddBatchAsync(DateTimeOffset importedAt, ImportBatchStatus status)
    {
        var batch = new ImportBatch
        {
            SourceFileName = "roster.csv",
            SourceFileHash = "abc",
            MappingVersion = "import-mapping.example.json",
            ImportedBySubject = "importer",
            ImportedAtUtc = importedAt,
            Status = status,
            NewCount = 3,
            StagedRowsJson = status == ImportBatchStatus.Promoted ? null : """{"Rows":[]}""",
            ValidationResultsJson = status == ImportBatchStatus.Promoted ? null : """{"rowIssues":[]}""",
        };
        await using var db = NewDb();
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync();
        return batch.Id;
    }

    [Fact]
    public async Task Idempotency_keys_older_than_30_days_are_deleted_and_younger_ones_kept()
    {
        var expired = await AddKeyAsync(Now - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        var boundary = await AddKeyAsync(Now - TimeSpan.FromDays(30));
        var recent = await AddKeyAsync(Now - TimeSpan.FromDays(29));

        var outcome = await SweepAsync();

        Assert.Equal(1, outcome.IdempotencyKeysDeleted);
        await using var db = NewDb();
        var left = await db.IdempotencyKeys.Select(k => k.Key).ToListAsync();
        Assert.DoesNotContain(expired, left);
        Assert.Contains(boundary, left);
        Assert.Contains(recent, left);
    }

    [Theory]
    [InlineData(ImportBatchStatus.Validated)]
    [InlineData(ImportBatchStatus.Approved)]
    [InlineData(ImportBatchStatus.ValidationFailed)]
    [InlineData(ImportBatchStatus.Rejected)]
    public async Task An_unpromoted_batch_older_than_7_days_loses_its_rows_but_keeps_its_metadata(
        ImportBatchStatus status)
    {
        var old = await AddBatchAsync(Now - TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), status);
        var young = await AddBatchAsync(Now - TimeSpan.FromDays(6), status);

        var outcome = await SweepAsync();

        Assert.Equal(1, outcome.ImportBatchesPurged);
        await using var db = NewDb();
        var purged = await db.ImportBatches.SingleAsync(b => b.Id == old);
        Assert.Null(purged.StagedRowsJson);
        Assert.Null(purged.ValidationResultsJson);
        Assert.Equal(status, purged.Status);
        Assert.Equal("roster.csv", purged.SourceFileName);
        Assert.Equal("abc", purged.SourceFileHash);
        Assert.Equal(3, purged.NewCount);

        var kept = await db.ImportBatches.SingleAsync(b => b.Id == young);
        Assert.NotNull(kept.StagedRowsJson);
        Assert.NotNull(kept.ValidationResultsJson);
    }

    private async Task AddAttemptsAsync(params (DateTimeOffset At, SignInOutcome Outcome)[] attempts)
    {
        await using var db = NewDb();
        foreach (var (at, outcome) in attempts)
            db.SignInAttempts.Add(new SignInAttempt { AtUtc = at, ClientAddress = "10.0.0.10", Outcome = outcome });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddSetupLinkAsync(DateTimeOffset expiresAt)
    {
        var link = new LocalAccountSetupLink
        {
            Id = Guid.CreateVersion7(),
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Purpose = SetupLinkPurpose.FirstAdministrator,
            CreatedAtUtc = expiresAt - TimeSpan.FromHours(24),
            CreatedBySubject = "host",
            ExpiresAtUtc = expiresAt,
        };
        await using var db = NewDb();
        db.LocalAccountSetupLinks.Add(link);
        await db.SaveChangesAsync();
        return link.Id;
    }

    [Fact]
    public async Task Sign_in_attempts_older_than_90_days_are_deleted_and_younger_ones_kept()
    {
        // More than one delete chunk, so the walk has to continue past the first.
        await AddAttemptsAsync([.. Enumerable.Range(0, 1203)
            .Select(i => (Now - TimeSpan.FromDays(91) + TimeSpan.FromSeconds(i), SignInOutcome.WrongPassword))]);
        await AddAttemptsAsync(
            (Now - TimeSpan.FromDays(90), SignInOutcome.Succeeded),
            (Now - TimeSpan.FromDays(1), SignInOutcome.LockedOut));

        var outcome = await SweepAsync();

        Assert.Equal(1203, outcome.SignInAttemptsDeleted);
        await using var db = NewDb();
        Assert.Equal([SignInOutcome.Succeeded, SignInOutcome.LockedOut],
            await db.SignInAttempts.OrderBy(a => a.Id).Select(a => a.Outcome).ToListAsync());
    }

    [Fact]
    public async Task Setup_links_go_90_days_after_they_expire()
    {
        var old = await AddSetupLinkAsync(Now - TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
        var boundary = await AddSetupLinkAsync(Now - TimeSpan.FromDays(90));
        var live = await AddSetupLinkAsync(Now + TimeSpan.FromHours(1));

        var outcome = await SweepAsync();

        Assert.Equal(1, outcome.SetupLinksDeleted);
        await using var db = NewDb();
        var left = await db.LocalAccountSetupLinks.Select(l => l.Id).ToListAsync();
        Assert.DoesNotContain(old, left);
        Assert.Contains(boundary, left);
        Assert.Contains(live, left);
    }

    [Fact]
    public async Task Records_are_never_touched()
    {
        await AddBatchAsync(Now - TimeSpan.FromDays(90), ImportBatchStatus.Promoted);
        await using (var seed = NewDb())
        {
            seed.Students.Add(new Student
            {
                ExternalStudentId = "SYN1", Surname = "Synthetic", FirstName = "Sam",
                IsActive = true, CreatedAtUtc = Now - TimeSpan.FromDays(400),
            });
            seed.AuditEvents.Add(new AuditEvent
            {
                Action = "test.action", EntityType = "Student", EntityId = "SYN1",
                ActorSubject = "importer", AtUtc = Now - TimeSpan.FromDays(400),
            });
            await seed.SaveChangesAsync();
        }

        var outcome = await SweepAsync();

        Assert.Equal(new RetentionOutcome(0, 0), outcome);
        await using var db = NewDb();
        Assert.Equal(1, await db.Students.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
        Assert.Equal(1, await db.ImportBatches.CountAsync());
    }

    [Fact]
    public async Task The_clock_moving_on_ages_out_what_was_young()
    {
        var key = await AddKeyAsync(Now);
        var batch = await AddBatchAsync(Now, ImportBatchStatus.Validated);
        Assert.Equal(new RetentionOutcome(0, 0), await SweepAsync());

        _time.Now += TimeSpan.FromDays(8);
        Assert.Equal(new RetentionOutcome(0, 1), await SweepAsync());

        _time.Now += TimeSpan.FromDays(23);
        Assert.Equal(new RetentionOutcome(1, 0), await SweepAsync());

        await using var db = NewDb();
        Assert.False(await db.IdempotencyKeys.AnyAsync(k => k.Key == key));
        Assert.Null((await db.ImportBatches.SingleAsync(b => b.Id == batch)).StagedRowsJson);
    }

    [Fact]
    public async Task An_expired_batch_cannot_be_approved_or_promoted()
    {
        var validated = await AddBatchAsync(Now - TimeSpan.FromDays(8), ImportBatchStatus.Validated);
        var approved = await AddBatchAsync(Now - TimeSpan.FromDays(8), ImportBatchStatus.Approved);
        await SweepAsync();

        await using var db = NewDb();
        var service = Fixtures.ImportService(db);
        var approve = await Assert.ThrowsAsync<ImportPipelineException>(
            () => service.ApproveAsync(validated, true, null, Fixtures.Reviewer));
        Assert.Equal(CarnivalApp.Api.Http.ErrorCodes.ImportBatchExpired, approve.Code);
        Assert.Equal(409, approve.StatusCode);

        var promote = await Assert.ThrowsAsync<ImportPipelineException>(
            () => service.PromoteAsync(approved, Fixtures.Importer));
        Assert.Equal(CarnivalApp.Api.Http.ErrorCodes.ImportBatchExpired, promote.Code);
        await using var verify = NewDb();
        Assert.Equal(ImportBatchStatus.Approved,
            (await verify.ImportBatches.SingleAsync(b => b.Id == approved)).Status);
    }

    [Fact]
    public async Task The_job_runs_on_a_school_deployment_only_and_skips_a_database_that_is_not_postgres()
    {
        using var factory = new ApiTestFactory();
        // Development is the SQLite loop.
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => s is RetentionJob);

        using var school = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        Assert.Contains(school.Services.GetServices<IHostedService>(), s => s is RetentionJob);

        using var demo = new DemoHost();
        Assert.DoesNotContain(demo.Services.GetServices<IHostedService>(), s => s is RetentionJob);

        var job = new RetentionJob(
            school.Services.GetRequiredService<IServiceScopeFactory>(), _time,
            NullLogger<RetentionJob>.Instance);
        Assert.False(await job.SweepOnceAsync(CancellationToken.None));
    }
}
