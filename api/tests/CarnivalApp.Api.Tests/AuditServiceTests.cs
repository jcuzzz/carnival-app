using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Audit framework (proposal §9.3): rows carry actor identity + role, UTC
/// timestamp + school timezone, action, entity, prior/new values, reason,
/// import batch and app version — and commit in the SAME transaction as the
/// mutation they record (rollback removes both).
/// </summary>
public sealed class AuditServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SportsDbContext> _options;

    public AuditServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SportsDbContext>().UseSqlite(_connection).Options;
        using var db = new SportsDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private SportsDbContext NewDb() => new(_options);

    [Fact]
    public async Task Record_persists_all_section_9_3_fields()
    {
        var batchId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            Fixtures.Audit(db).Record(
                new("verifier-7", "CarnivalAdministrator"),
                AuditActions.ResultCorrect, "Result", "abc-123",
                priorValues: new { timeSeconds = 31.20 },
                newValues: new { timeSeconds = 30.95 },
                reason: "Timekeeper transcription error",
                importBatchId: batchId,
                ruleVersion: "scoring-v1");
            await db.SaveChangesAsync();
        }

        await using var verify = NewDb();
        var row = Assert.Single(await verify.AuditEvents.ToListAsync());
        Assert.Equal("verifier-7", row.ActorSubject);
        Assert.Equal("CarnivalAdministrator", row.ActorRole);
        Assert.Equal("Australia/Melbourne", row.SchoolTimezone);
        Assert.True(row.AtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(AuditActions.ResultCorrect, row.Action);
        Assert.Equal("Result", row.EntityType);
        Assert.Equal("abc-123", row.EntityId);
        Assert.Contains("31.2", row.PriorValuesJson);
        Assert.Contains("30.95", row.NewValuesJson);
        Assert.Equal("Timekeeper transcription error", row.Reason);
        Assert.Equal(batchId, row.ImportBatchId);
        Assert.Matches(@"^\d+\.\d+\.\d+$", row.ApplicationVersion);
        Assert.Equal("scoring-v1", row.RuleVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_blank_configured_timezone_stamps_the_default_zone(string configured)
    {
        await using (var db = NewDb())
        {
            new AuditService(db, Fixtures.Config(("School:Timezone", configured)))
                .Record(Fixtures.Importer, "student.create", nameof(Student), "X1");
            await db.SaveChangesAsync();
        }

        await using var verify = NewDb();
        Assert.Equal("Australia/Melbourne", Assert.Single(await verify.AuditEvents.ToListAsync()).SchoolTimezone);
    }

    [Fact]
    public async Task Audit_row_rolls_back_with_the_mutation_it_records()
    {
        await using (var db = NewDb())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Students.Add(new Student
            {
                ExternalStudentId = "X1", Surname = "Roll", FirstName = "Back",
                IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            Fixtures.Audit(db).Record(Fixtures.Importer, "student.create", nameof(Student), "X1");
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var verify = NewDb();
        Assert.Empty(await verify.AuditEvents.ToListAsync()); // no orphan audit row
        Assert.Empty(await verify.Students.ToListAsync());    // no unaudited mutation
    }

    [Fact]
    public async Task Import_stage_approve_promote_each_write_an_audit_row()
    {
        Guid batchId;
        await using (var db = NewDb())
        {
            var service = Fixtures.ImportService(db);
            var csv = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
                + "A1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n"));
            var outcome = await service.StageAsync(csv, "audit.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
            batchId = outcome.Batch.Id;
            await service.ApproveAsync(batchId, true, "looks right", Fixtures.Reviewer);
            await service.PromoteAsync(batchId, Fixtures.Importer);
        }

        await using var verify = NewDb();
        var events = await verify.AuditEvents.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal([AuditActions.ImportStage, AuditActions.ImportApprove, AuditActions.ImportPromote], events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Equal(nameof(ImportBatch), e.EntityType);
            Assert.Equal(batchId, e.ImportBatchId);
            Assert.Equal(batchId.ToString(), e.EntityId);
            Assert.NotNull(e.ApplicationVersion);
        });
        Assert.Equal("importer", events[0].ActorSubject);
        Assert.Equal("reviewer", events[1].ActorSubject);
        Assert.Equal("looks right", events[1].Reason);
        Assert.Contains("Validated", events[1].PriorValuesJson);
        Assert.Contains("Approved", events[1].NewValuesJson);
        Assert.Contains("Approved", events[2].PriorValuesJson);
        Assert.Contains("Promoted", events[2].NewValuesJson);
    }

    [Fact]
    public async Task Rejecting_a_batch_writes_an_import_reject_audit_row_with_reason()
    {
        await using var db = NewDb();
        var service = Fixtures.ImportService(db);
        var csv = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
            + "A1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n"));
        var outcome = await service.StageAsync(csv, "audit.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
        await service.ApproveAsync(outcome.Batch.Id, false, "wrong term export", Fixtures.Reviewer);

        var reject = await db.AuditEvents.SingleAsync(e => e.Action == AuditActions.ImportReject);
        Assert.Equal("wrong term export", reject.Reason);
        Assert.Contains("Rejected", reject.NewValuesJson);
    }
}
