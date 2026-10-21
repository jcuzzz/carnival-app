using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Data.Configurations;

public class ResultConfiguration : IEntityTypeConfiguration<Result>
{
    public void Configure(EntityTypeBuilder<Result> b)
    {
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.MeasuredValue).HasPrecision(10, 3);
        // Sized for the high-jump card (~30 chars per bar height), not the six-attempt series.
        b.Property(x => x.AttemptsJson).HasMaxLength(2000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.LifecycleState).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.RecordedBySubject).HasMaxLength(128);
        b.Property(x => x.DeviceId).HasMaxLength(64);

        b.Property(x => x.CurrentRevision).IsConcurrencyToken();

        b.HasOne(x => x.Entry)
            .WithMany(e => e.Results)
            .HasForeignKey(x => x.EntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Only the index makes one-result-per-entry hold when two captures race.
        b.HasIndex(x => x.EntryId).IsUnique();
        b.HasIndex(x => x.LifecycleState);
    }
}

public class ResultRevisionConfiguration : IEntityTypeConfiguration<ResultRevision>
{
    public void Configure(EntityTypeBuilder<ResultRevision> b)
    {
        b.Property(x => x.ActorSubject).HasMaxLength(128);
        // ';'-joined role list, sized with AuditEvent.ActorRole.
        b.Property(x => x.ActorRole).HasMaxLength(256);
        b.Property(x => x.Reason).HasMaxLength(1000);

        b.HasOne(x => x.Result)
            .WithMany(r => r.Revisions)
            .HasForeignKey(x => x.ResultId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.ResultId, x.RevisionNumber }).IsUnique();
    }
}

public class SchoolRecordConfiguration : IEntityTypeConfiguration<SchoolRecord>
{
    public void Configure(EntityTypeBuilder<SchoolRecord> b)
    {
        b.Property(x => x.AgeGroup).HasMaxLength(Carnivals.AgeScope.MaxCodeLength);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(1);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.RecordValue).HasPrecision(10, 3);
        b.Property(x => x.HolderDisplayName).HasMaxLength(200);
        b.Property(x => x.ApprovedBySubject).HasMaxLength(128);
        b.Property(x => x.ApprovalNote).HasMaxLength(1000);
        b.Property(x => x.VoidedBySubject).HasMaxLength(128);
        b.Property(x => x.VoidReason).HasMaxLength(1000);

        b.HasOne(x => x.EventDefinition)
            .WithMany()
            .HasForeignKey(x => x.EventDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Result)
            .WithMany()
            .HasForeignKey(x => x.ResultId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PreviousRecord)
            .WithMany()
            .HasForeignKey(x => x.PreviousRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        // One standing record per category is enforced in code:
        // a filtered unique index would be provider-specific.
        b.HasIndex(x => new { x.EventDefinitionId, x.AgeGroup, x.Category, x.Status });
    }
}
