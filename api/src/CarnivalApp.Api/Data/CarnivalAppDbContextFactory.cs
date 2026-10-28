using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CarnivalApp.Api.Data;

/// <summary>For `dotnet ef`; the connection string is a placeholder, never connected to.</summary>
public sealed class CarnivalAppDbContextFactory : IDesignTimeDbContextFactory<CarnivalAppDbContext>
{
    public CarnivalAppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CarnivalAppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_placeholder;Username=design;Password=design")
            .Options;
        return new CarnivalAppDbContext(options);
    }
}
