using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Data;

/// <summary>
/// Model and queries stay provider-clean — string enums, no Postgres-only SQL or column types —
/// to keep a SQL Server deployment possible.
/// </summary>
public class CarnivalAppDbContext(DbContextOptions<CarnivalAppDbContext> options) : DbContext(options)
{
    public DbSet<SchoolConfiguration> SchoolConfigurations => Set<SchoolConfiguration>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<RosterSnapshot> RosterSnapshots => Set<RosterSnapshot>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<Carnival> Carnivals => Set<Carnival>();
    public DbSet<EventDefinition> EventDefinitions => Set<EventDefinition>();
    public DbSet<CarnivalEvent> CarnivalEvents => Set<CarnivalEvent>();
    public DbSet<CarnivalEventOfficial> CarnivalEventOfficials => Set<CarnivalEventOfficial>();

    public DbSet<CarnivalDivision> CarnivalDivisions => Set<CarnivalDivision>();

    public DbSet<ScheduleStream> ScheduleStreams => Set<ScheduleStream>();
    public DbSet<ScheduleSlot> ScheduleSlots => Set<ScheduleSlot>();

    public DbSet<Race> Races => Set<Race>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<RelayLeg> RelayLegs => Set<RelayLeg>();
    public DbSet<Result> Results => Set<Result>();
    public DbSet<HousePointsAdjustment> HousePointsAdjustments => Set<HousePointsAdjustment>();
    public DbSet<ResultRevision> ResultRevisions => Set<ResultRevision>();

    public DbSet<DeviceSyncStatus> DeviceSyncStatuses => Set<DeviceSyncStatus>();

    public DbSet<SchoolRecord> SchoolRecords => Set<SchoolRecord>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CarnivalAppDbContext).Assembly);
    }
}
