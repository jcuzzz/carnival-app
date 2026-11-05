using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Identity.LocalAccounts;

namespace CarnivalApp.Api.Data.Configurations;

public class LocalAccountConfiguration : IEntityTypeConfiguration<LocalAccount>
{
    public void Configure(EntityTypeBuilder<LocalAccount> b)
    {
        b.ToTable("LocalAccounts");
        b.Property(x => x.UserName).HasMaxLength(LocalUsernames.MaxLength);
        b.Property(x => x.NormalizedUserName).HasMaxLength(LocalUsernames.MaxLength);
        b.HasIndex(x => x.NormalizedUserName).HasDatabaseName("IX_LocalAccounts_NormalizedUserName");
        b.HasIndex(x => x.NormalizedEmail).HasDatabaseName("IX_LocalAccounts_NormalizedEmail");
        b.Property(x => x.SecurityStamp).HasMaxLength(64);
        b.Property(x => x.ConcurrencyStamp).HasMaxLength(64);
        b.Property(x => x.PasswordHash).HasMaxLength(256);
        b.Property(x => x.PhoneNumber).HasMaxLength(32);
        b.Property(x => x.DisplayName).HasMaxLength(LocalAccountLimits.MaxDisplayNameLength);
        b.Property(x => x.CreatedBySubject).HasMaxLength(128);
    }
}

public class LocalAccountClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> b)
    {
        b.ToTable("LocalAccountClaims");
        b.Property(x => x.ClaimType).HasMaxLength(256);
        b.Property(x => x.ClaimValue).HasMaxLength(256);
    }
}

public class LocalAccountLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> b)
    {
        b.ToTable("LocalAccountLogins");
        b.Property(x => x.LoginProvider).HasMaxLength(128);
        b.Property(x => x.ProviderKey).HasMaxLength(128);
        b.Property(x => x.ProviderDisplayName).HasMaxLength(128);
    }
}

public class LocalAccountTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> b)
    {
        b.ToTable("LocalAccountTokens");
        b.Property(x => x.LoginProvider).HasMaxLength(128);
        b.Property(x => x.Name).HasMaxLength(128);
        // Ten recovery-code hashes joined by ';' is the longest value.
        b.Property(x => x.Value).HasMaxLength(1024);
    }
}

public class LocalAccountSetupLinkConfiguration : IEntityTypeConfiguration<LocalAccountSetupLink>
{
    public void Configure(EntityTypeBuilder<LocalAccountSetupLink> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.CreatedBySubject).HasMaxLength(128);
        b.Property(x => x.UsedFromAddress).HasMaxLength(SignInAttemptLog.MaxAddressLength);

        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.LocalAccountId);
        b.HasOne<LocalAccount>().WithMany().HasForeignKey(x => x.LocalAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class SignInAttemptConfiguration : IEntityTypeConfiguration<SignInAttempt>
{
    public void Configure(EntityTypeBuilder<SignInAttempt> b)
    {
        b.Property(x => x.ClientAddress).HasMaxLength(SignInAttemptLog.MaxAddressLength);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);

        b.HasIndex(x => new { x.LocalAccountId, x.ClientAddress, x.Id });
        b.HasIndex(x => x.AtUtc);
        b.HasOne<LocalAccount>().WithMany().HasForeignKey(x => x.LocalAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class EndedLocalSessionConfiguration : IEntityTypeConfiguration<EndedLocalSession>
{
    public void Configure(EntityTypeBuilder<EndedLocalSession> b)
    {
        b.HasKey(x => x.SessionId);
        b.Property(x => x.SessionId).HasMaxLength(EndedLocalSession.MaxSessionIdLength);
    }
}
