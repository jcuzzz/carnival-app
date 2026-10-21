using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Data.Configurations;

public class CarnivalConfiguration : IEntityTypeConfiguration<Carnival>
{
    public void Configure(EntityTypeBuilder<Carnival> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Venue).HasMaxLength(200);
        b.Property(x => x.RuleVersion).HasMaxLength(64);
        b.Property(x => x.OwnerSubject).HasMaxLength(128);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.SettingsJson).HasMaxLength(4000);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.Date);
    }
}

public class EventDefinitionConfiguration : IEntityTypeConfiguration<EventDefinition>
{
    public void Configure(EntityTypeBuilder<EventDefinition> b)
    {
        b.Property(x => x.Code).HasMaxLength(FieldLimits.Code);
        b.Property(x => x.Name).HasMaxLength(FieldLimits.Name);
        b.Property(x => x.Sport).HasMaxLength(FieldLimits.Sport);
        b.Property(x => x.Units).HasMaxLength(FieldLimits.Units);
        b.Property(x => x.MeasureType).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Format).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LegsJson).HasMaxLength(500);
        b.Property(x => x.TagsJson).HasMaxLength(500);
        // {"start":1.20,"step":0.05} and nothing else.
        b.Property(x => x.LadderJson).HasMaxLength(64);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public class CarnivalDivisionConfiguration : IEntityTypeConfiguration<CarnivalDivision>
{
    public void Configure(EntityTypeBuilder<CarnivalDivision> b)
    {
        b.Property(x => x.AgeGroup).HasMaxLength(AgeScope.MaxCodeLength);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(1);
        b.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);

        b.HasOne(x => x.Carnival)
            .WithMany(c => c.Divisions)
            .HasForeignKey(x => x.CarnivalId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.CarnivalId, x.AgeGroup, x.Category, x.Tier }).IsUnique();
        b.HasIndex(x => x.AgeGroup);
    }
}

public class CarnivalEventConfiguration : IEntityTypeConfiguration<CarnivalEvent>
{
    public void Configure(EntityTypeBuilder<CarnivalEvent> b)
    {
        b.Property(x => x.AgeGroup).HasMaxLength(AgeScope.MaxCodeLength);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(1);
        b.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ScoringMode).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.HeightsJson).HasMaxLength(JumpCard.MaxHeightsJsonLength);
        b.Property(x => x.Version).IsConcurrencyToken();

        b.HasOne(x => x.Carnival)
            .WithMany(c => c.Events)
            .HasForeignKey(x => x.CarnivalId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.EventDefinition)
            .WithMany()
            .HasForeignKey(x => x.EventDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
        // Removing a slot must never remove events. ClientSetNull, not SET NULL: a second cascade
        // path from Carnival is refused by SQL Server, so the endpoints unschedule explicitly.
        b.HasOne(x => x.ScheduleSlot)
            .WithMany(s => s.Events)
            .HasForeignKey(x => x.ScheduleSlotId)
            .OnDelete(DeleteBehavior.ClientSetNull);
        b.HasIndex(x => x.ScheduleSlotId);

        b.HasIndex(x => new { x.CarnivalId, x.EventDefinitionId, x.AgeGroup, x.Category, x.Tier })
            .IsUnique();
    }
}

public class ScheduleStreamConfiguration : IEntityTypeConfiguration<ScheduleStream>
{
    public void Configure(EntityTypeBuilder<ScheduleStream> b)
    {
        b.Property(x => x.Name).HasMaxLength(100);

        b.HasOne(x => x.Carnival)
            .WithMany(c => c.ScheduleStreams)
            .HasForeignKey(x => x.CarnivalId)
            .OnDelete(DeleteBehavior.Cascade);

        // Template application recognises the stream it already created by name.
        b.HasIndex(x => new { x.CarnivalId, x.Name }).IsUnique();
    }
}

public class ScheduleSlotConfiguration : IEntityTypeConfiguration<ScheduleSlot>
{
    public void Configure(EntityTypeBuilder<ScheduleSlot> b)
    {
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Label).HasMaxLength(200);
        b.Property(x => x.AdjustmentCategory).HasMaxLength(64); // = HousePointsAdjustment.Category

        b.HasOne(x => x.Stream)
            .WithMany(s => s.Slots)
            .HasForeignKey(x => x.StreamId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.StreamId);
    }
}

public class RaceConfiguration : IEntityTypeConfiguration<Race>
{
    public void Configure(EntityTypeBuilder<Race> b)
    {
        b.Property(x => x.Label).HasMaxLength(200); // sized with ScheduleSlot.Label

        b.HasOne(x => x.Carnival)
            .WithMany(c => c.Races)
            .HasForeignKey(x => x.CarnivalId)
            .OnDelete(DeleteBehavior.Cascade);

        // ClientSetNull on both optional parents, as for CarnivalEvent → ScheduleSlot; the slot and
        // division endpoints detach races explicitly.
        b.HasOne(x => x.ScheduleSlot)
            .WithMany(s => s.Races)
            .HasForeignKey(x => x.ScheduleSlotId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        b.HasOne(x => x.CarnivalEvent)
            .WithMany()
            .HasForeignKey(x => x.CarnivalEventId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        b.HasIndex(x => x.CarnivalId);
        b.HasIndex(x => x.ScheduleSlotId);
        b.HasIndex(x => x.CarnivalEventId);
    }
}

public class CarnivalEventOfficialConfiguration : IEntityTypeConfiguration<CarnivalEventOfficial>
{
    public void Configure(EntityTypeBuilder<CarnivalEventOfficial> b)
    {
        b.Property(x => x.SubjectId).HasMaxLength(FieldLimits.Subject);
        b.Property(x => x.SubjectDisplayName).HasMaxLength(FieldLimits.DisplayName);
        b.Property(x => x.AssignedBySubject).HasMaxLength(128);

        b.HasOne(x => x.CarnivalEvent)
            .WithMany(e => e.Officials)
            .HasForeignKey(x => x.CarnivalEventId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.CarnivalEventId, x.SubjectId }).IsUnique();
        b.HasIndex(x => x.SubjectId);
    }
}

public class EntryConfiguration : IEntityTypeConfiguration<Entry>
{
    public void Configure(EntityTypeBuilder<Entry> b)
    {
        b.Property(x => x.SnapshotDisplayName).HasMaxLength(200);
        b.Property(x => x.SnapshotHouseCode).HasMaxLength(32);
        b.Property(x => x.SnapshotAgeGroup).HasMaxLength(16);
        b.Property(x => x.SnapshotCategory).HasConversion<string>().HasMaxLength(1);
        b.Property(x => x.HouseCode).HasMaxLength(32);
        b.Property(x => x.CategoryOverride).HasConversion<string>().HasMaxLength(1);
        b.Property(x => x.CategoryOverrideActorSubject).HasMaxLength(128);
        b.Property(x => x.CategoryOverrideReason).HasMaxLength(1000);
        b.Property(x => x.CreatedBySubject).HasMaxLength(128);

        b.HasOne(x => x.CarnivalEvent)
            .WithMany(e => e.Entries)
            .HasForeignKey(x => x.CarnivalEventId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Student)
            .WithMany()
            .HasForeignKey(x => x.StudentId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.RosterSnapshot)
            .WithMany()
            .HasForeignKey(x => x.RosterSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        // ClientSetNull as for every Carnival-owned optional parent; the race endpoints unassign
        // explicitly, so deleting a race never deletes entries.
        b.HasOne(x => x.Race)
            .WithMany(r => r.Entries)
            .HasForeignKey(x => x.RaceId)
            .OnDelete(DeleteBehavior.ClientSetNull);
        b.HasIndex(x => x.RaceId);

        // No HasFilter: its raw SQL reaches every provider.
        // PostgreSQL/SQLite treat NULLs as distinct, and SQL Server's EF
        // convention adds correctly quoted IS NOT NULL filters itself.
        b.HasIndex(x => new { x.RaceId, x.Lane }).IsUnique();

        // NULLs are distinct in a unique index, so each index ignores the other shape of entry.
        b.HasIndex(x => new { x.CarnivalEventId, x.StudentId }).IsUnique();
        b.HasIndex(x => new { x.CarnivalEventId, x.HouseCode }).IsUnique();
        b.HasIndex(x => x.StudentId);

        b.ToTable(t => t.HasCheckConstraint(
            "CK_Entry_IndividualOrTeam",
            "(\"StudentId\" IS NOT NULL AND \"HouseCode\" IS NULL) OR " +
            "(\"StudentId\" IS NULL AND \"HouseCode\" IS NOT NULL)"));
    }
}

public class HousePointsAdjustmentConfiguration : IEntityTypeConfiguration<HousePointsAdjustment>
{
    public void Configure(EntityTypeBuilder<HousePointsAdjustment> b)
    {
        b.Property(x => x.HouseCode).HasMaxLength(FieldLimits.HouseCode);
        b.Property(x => x.Category).HasMaxLength(FieldLimits.Category);
        b.Property(x => x.Reason).HasMaxLength(FieldLimits.Reason);
        b.Property(x => x.RecordedBySubject).HasMaxLength(128);
        // ';'-joined role list, sized with AuditEvent.ActorRole.
        b.Property(x => x.RecordedByRole).HasMaxLength(256);
        b.Property(x => x.VoidedBySubject).HasMaxLength(128);
        b.Property(x => x.VoidReason).HasMaxLength(FieldLimits.Reason);

        b.HasOne(x => x.Carnival)
            .WithMany()
            .HasForeignKey(x => x.CarnivalId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.CarnivalId);
    }
}

public class RelayLegConfiguration : IEntityTypeConfiguration<RelayLeg>
{
    public void Configure(EntityTypeBuilder<RelayLeg> b)
    {
        b.Property(x => x.Stroke).HasMaxLength(32);
        b.Property(x => x.SnapshotDisplayName).HasMaxLength(200);
        b.Property(x => x.SnapshotHouseCode).HasMaxLength(32);
        b.Property(x => x.SnapshotAgeGroup).HasMaxLength(16);
        b.Property(x => x.SnapshotCategory).HasConversion<string>().HasMaxLength(1);

        b.HasOne(x => x.Entry)
            .WithMany(e => e.RelayLegs)
            .HasForeignKey(x => x.EntryId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Student)
            .WithMany()
            .HasForeignKey(x => x.StudentId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.RosterSnapshot)
            .WithMany()
            .HasForeignKey(x => x.RosterSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.EntryId, x.LegOrder }).IsUnique();
        b.HasIndex(x => new { x.EntryId, x.StudentId }).IsUnique();
    }
}

public class DeviceSyncStatusConfiguration : IEntityTypeConfiguration<DeviceSyncStatus>
{
    public void Configure(EntityTypeBuilder<DeviceSyncStatus> b)
    {
        b.HasKey(x => new { x.CarnivalId, x.DeviceId });

        // Sized with IdempotencyKey.DeviceId / Result.DeviceId: the same X-Device-Id value.
        b.Property(x => x.DeviceId).HasMaxLength(64);
        b.Property(x => x.LastSubject).HasMaxLength(128);
        b.Property(x => x.LastDisplayName).HasMaxLength(128);
        b.Property(x => x.AppVersion).HasMaxLength(32);

        b.HasOne(x => x.Carnival)
            .WithMany()
            .HasForeignKey(x => x.CarnivalId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
