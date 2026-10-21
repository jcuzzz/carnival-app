using Microsoft.EntityFrameworkCore;

namespace CarnivalApp.Api.Demo;

public class DemoSessionRow
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the cookie token, hex; the token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    public required string DatabaseName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}

/// <summary>
/// Created with EnsureCreated, not migrated: every demo deploy recreates it with its sessions
/// (ADR-0061 §6).
/// </summary>
public class DemoControlDbContext(DbContextOptions<DemoControlDbContext> options) : DbContext(options)
{
    public DbSet<DemoSessionRow> Sessions => Set<DemoSessionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<DemoSessionRow>();
        session.ToTable("DemoSessions");
        session.Property(row => row.TokenHash).HasMaxLength(64);
        session.Property(row => row.DatabaseName).HasMaxLength(63);
        session.HasIndex(row => row.TokenHash).IsUnique();
        session.HasIndex(row => row.DatabaseName).IsUnique();
    }
}
