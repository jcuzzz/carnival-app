using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using CarnivalApp.Api.Migrations;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0058 §5's refusal, read off the operations the migration emits. The
/// migration itself only ever runs on PostgreSQL (the SQLite dev loop and this
/// suite build from the model with EnsureCreated), so the guard is pinned as SQL
/// text and ORDER rather than by running it: a merged row that reached the
/// column drops would be gone before anyone could be told about it.
/// </summary>
public sealed class OneAgePerDivisionMigrationTests
{
    private static List<MigrationOperation> Up()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(OneAgePerDivision)
            .GetMethod("Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(new OneAgePerDivision(), [builder]);
        return [.. builder.Operations];
    }

    [Fact]
    public void The_guard_refuses_a_merged_age_on_every_table_that_could_hold_one()
    {
        var guard = Assert.IsType<SqlOperation>(Up()[0]);
        Assert.Contains("RAISE EXCEPTION", guard.Sql, StringComparison.Ordinal);
        foreach (var table in new[] { "CarnivalEvents", "CarnivalDivisions", "SchoolRecords" })
            Assert.Contains($"FROM \"{table}\" WHERE", guard.Sql, StringComparison.Ordinal);
        // The hyphen is the merged key's join character (ADR-0014's canonicaliser).
        Assert.Equal(3, guard.Sql.Split("LIKE '%-%'").Length - 1);
    }

    [Fact]
    public void The_backfill_runs_before_anything_is_dropped()
    {
        var operations = Up();
        var backfill = operations.FindIndex(o =>
            o is SqlOperation sql && sql.Sql.Contains("SET \"AgeGroup\" = \"AgeGroupKey\"", StringComparison.Ordinal));
        var firstDrop = operations.FindIndex(o => o is DropColumnOperation);
        Assert.InRange(backfill, 1, firstDrop - 1);
        // And the column it fills exists by then.
        Assert.InRange(
            operations.FindIndex(o => o is AddColumnOperation { Name: "AgeGroup" }), 1, backfill - 1);
    }
}
