using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Data.Configurations;

public class SchoolConfigurationConfiguration : IEntityTypeConfiguration<SchoolConfiguration>
{
    public void Configure(EntityTypeBuilder<SchoolConfiguration> b)
    {
        b.Property(x => x.SchoolIdentifier).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Timezone).HasMaxLength(64);
        // The stamp the settings form round-trips, so two saves from one render cannot both land.
        b.Property(x => x.UpdatedAtUtc).IsConcurrencyToken();
        b.HasIndex(x => x.SchoolIdentifier).IsUnique();
    }
}

public class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> b)
    {
        b.Property(x => x.ExternalStudentId).HasMaxLength(32);
        b.Property(x => x.Surname).HasMaxLength(100);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.PreferredName).HasMaxLength(100);
        b.HasIndex(x => x.ExternalStudentId).IsUnique();
    }
}

public class RosterSnapshotConfiguration : IEntityTypeConfiguration<RosterSnapshot>
{
    public void Configure(EntityTypeBuilder<RosterSnapshot> b)
    {
        b.Property(x => x.HouseCode).HasMaxLength(32);
        b.Property(x => x.CompetitionAgeGroup).HasMaxLength(16);
        b.Property(x => x.HomeGroup).HasMaxLength(16);
        b.Property(x => x.CompetitionCategory).HasConversion<string>().HasMaxLength(1);

        b.HasOne(x => x.Student)
            .WithMany(s => s.RosterSnapshots)
            .HasForeignKey(x => x.StudentId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ImportBatch)
            .WithMany(i => i.RosterSnapshots)
            .HasForeignKey(x => x.ImportBatchId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.StudentId, x.ImportBatchId }).IsUnique();
        b.HasIndex(x => new { x.StudentId, x.IsCurrent });
        b.HasIndex(x => x.SchoolYear);
        // The selective part of eligible-student search;
        // the name match then runs over one division.
        b.HasIndex(x => new { x.IsCurrent, x.CompetitionAgeGroup, x.CompetitionCategory });
    }
}

public class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> b)
    {
        b.Property(x => x.SourceFileName).HasMaxLength(260);
        b.Property(x => x.SourceFileHash).HasMaxLength(64);
        b.Property(x => x.MappingVersion).HasMaxLength(64);
        b.Property(x => x.ImportedBySubject).HasMaxLength(128);
        b.Property(x => x.ReviewedBySubject).HasMaxLength(128);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        b.HasIndex(x => x.ImportedAtUtc);
        b.HasIndex(x => x.Status);
    }
}
