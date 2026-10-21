using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CarnivalApp.Api.Data;

/// <summary>For `dotnet ef`; the connection string is a placeholder, never connected to.</summary>
public sealed class SportsDbContextFactory : IDesignTimeDbContextFactory<SportsDbContext>
{
    public SportsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SportsDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_placeholder;Username=design;Password=design")
            .Options;
        return new SportsDbContext(options);
    }
}
