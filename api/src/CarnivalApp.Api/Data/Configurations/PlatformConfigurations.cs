using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Data.Configurations;

public class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> b)
    {
        b.Property(x => x.SubjectId).HasMaxLength(128);
        b.Property(x => x.SubjectUpn).HasMaxLength(RoleAssignmentLimits.MaxUpnLength);
        b.Property(x => x.SubjectDisplayName).HasMaxLength(200);
        b.Property(x => x.Scope).HasMaxLength(128);
        b.Property(x => x.Reason).HasMaxLength(RoleAssignmentLimits.MaxReasonLength);
        b.Property(x => x.RevocationReason).HasMaxLength(RoleAssignmentLimits.MaxReasonLength);
        b.Property(x => x.RevokedBySubject).HasMaxLength(128);
        b.Property(x => x.ReviewedBySubject).HasMaxLength(128);
        b.Property(x => x.CreatedBySubject).HasMaxLength(128);
        b.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(8);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);

        b.HasIndex(x => x.SubjectId);

        // Active rows only: revoked rows stay as history,
        // so an unfiltered index would refuse re-grants.
        b.HasIndex(x => new { x.SubjectUpn, x.Role })
            .IsUnique()
            .HasFilter("\"RevokedAtUtc\" IS NULL");
    }
}

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.Property(x => x.ActorSubject).HasMaxLength(128);
        // The ';'-joined effective role list; the insert is atomic with the mutation, so an
        // overflow fails the mutation. 256 fits every role combination.
        b.Property(x => x.ActorRole).HasMaxLength(256);
        b.Property(x => x.SchoolTimezone).HasMaxLength(64);
        b.Property(x => x.Action).HasMaxLength(64);
        b.Property(x => x.EntityType).HasMaxLength(64);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.ApplicationVersion).HasMaxLength(32);
        b.Property(x => x.RuleVersion).HasMaxLength(64);

        b.HasIndex(x => x.AtUtc);
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        b.HasIndex(x => x.ActorSubject);
    }
}

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).ValueGeneratedNever();

        b.Property(x => x.PayloadHash).HasMaxLength(64);
        b.Property(x => x.ActorSubject).HasMaxLength(128);
        b.Property(x => x.DeviceId).HasMaxLength(64);

        b.HasIndex(x => x.CreatedAtUtc);
    }
}
