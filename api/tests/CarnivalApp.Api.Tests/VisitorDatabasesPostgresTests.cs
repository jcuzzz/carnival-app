using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Demo;
using Testcontainers.PostgreSql;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0061 §1: a visitor database is a template clone — seeded rows arrive, writes stay in their
/// own clone, and a drop removes exactly one. Skips without Docker, like the other Postgres tests.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class VisitorDatabasesPostgresTests
{
    private static readonly Lazy<bool> DockerAvailable = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            return process is not null && process.WaitForExit(15000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static CarnivalAppDbContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<CarnivalAppDbContext>().UseNpgsql(connectionString).Options);

    [SkippableFact]
    public async Task Clones_carry_the_template_and_stay_apart()
    {
        Skip.IfNot(DockerAvailable.Value, "Docker is not available on this machine.");
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var server = container.GetConnectionString();

        await ExecuteAsync(server, "CREATE DATABASE carnival_demo_template");
        var templateString = new NpgsqlConnectionStringBuilder(server) { Database = "carnival_demo_template", Pooling = false }
            .ConnectionString;
        await using (var template = Open(templateString))
        {
            await template.Database.MigrateAsync();
            template.SchoolConfigurations.Add(new SchoolConfiguration
            {
                SchoolIdentifier = "demo", Name = "Example Synthetic College", Timezone = "Australia/Melbourne",
            });
            await template.SaveChangesAsync();
        }
        await ExecuteAsync(server, "ALTER DATABASE carnival_demo_template IS_TEMPLATE true");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CarnivalAppDb"] = server,
            [$"ConnectionStrings:{DemoMode.ControlConnectionName}"] = server,
        }).Build();
        var databases = new PostgresVisitorDatabases(configuration);
        var a = VisitorDatabase.NewName();
        var b = VisitorDatabase.NewName();

        await databases.CreateAsync(a, CancellationToken.None);
        await databases.CreateAsync(b, CancellationToken.None);

        await using (var db = Open(DemoMode.VisitorConnectionString(configuration, a)))
        {
            var school = await db.SchoolConfigurations.SingleAsync();
            Assert.Equal("Example Synthetic College", school.Name);
            school.Name = "Visitor A's college";
            await db.SaveChangesAsync();
        }
        await using (var db = Open(DemoMode.VisitorConnectionString(configuration, b)))
            Assert.Equal("Example Synthetic College", (await db.SchoolConfigurations.SingleAsync()).Name);

        Assert.Equal(new[] { a, b }.Order(), (await databases.ListAsync(CancellationToken.None)).Order());

        await databases.DropAsync(a, CancellationToken.None);

        Assert.Equal([b], await databases.ListAsync(CancellationToken.None));
    }
}
