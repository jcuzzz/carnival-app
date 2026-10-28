using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// K11: the eligible-student search predicates are the one place the codebase
/// branches on the database provider, so both branches are pinned here with
/// ToQueryString — which needs no live server, so the PostgreSQL translation is
/// verified on a machine with no PostgreSQL (the Docker-backed integration
/// tests skip locally).
///
/// What is asserted is the SHAPE that makes the query indexable: the searched
/// COLUMN must appear bare, never wrapped in <c>lower(...)</c>, on the
/// PostgreSQL path. SQLite is the dev/test provider only and keeps the
/// translatable <c>lower(...)</c> form.
/// </summary>
public class EligibleSearchQueryTests
{
    private static CarnivalAppDbContext Postgres() => new(
        new DbContextOptionsBuilder<CarnivalAppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_placeholder;Username=x;Password=x").Options);

    private static CarnivalAppDbContext Sqlite() => new(
        new DbContextOptionsBuilder<CarnivalAppDbContext>()
            .UseSqlite("DataSource=:memory:").Options);

    [Fact]
    public void Name_search_uses_ILIKE_on_postgres()
    {
        using var db = Postgres();
        var sql = db.RosterSnapshots.WhereNameMatches(db, "o'br%ien").ToQueryString();

        Assert.Contains("ILIKE", sql);
        Assert.DoesNotContain("lower(", sql);
        // LIKE metacharacters an official types are literals, not wildcards.
        Assert.Contains(@"%o'br\%ien%", sql);
    }

    [Fact]
    public void House_filter_uses_ILIKE_equality_on_postgres()
    {
        using var db = Postgres();
        var sql = db.RosterSnapshots.WhereHouseMatches(db, "banksia").ToQueryString();

        Assert.Contains("ILIKE", sql);
        Assert.DoesNotContain("upper(", sql);
    }

    [Fact]
    public void Both_filters_still_translate_on_sqlite()
    {
        using var db = Sqlite();

        // The assertion that matters is that neither throws: an untranslatable
        // predicate is a runtime failure of the whole search on the dev loop.
        Assert.Contains("lower(", db.RosterSnapshots.WhereNameMatches(db, "smith").ToQueryString());
        Assert.Contains("upper(", db.RosterSnapshots.WhereHouseMatches(db, "banksia").ToQueryString());
    }

    // --- Age division filter (ADR-0058 §1) --------------------------------
    // The age is one code on a bare, indexable column — the eligible-student
    // search runs on every entry screen.

    private static CarnivalEvent EventScoped(string? ageGroup = null) => new()
    {
        Id = Guid.CreateVersion7(),
        CarnivalId = Guid.CreateVersion7(),
        EventDefinitionId = Guid.CreateVersion7(),
        AgeGroup = ageGroup ?? AgeScope.OpenKey,
    };

    [Theory]
    [InlineData("15")]
    [InlineData("SNR")]
    public void The_age_is_matched_on_the_bare_column_as_a_parameter(string age)
    {
        using var postgres = Postgres();
        using var sqlite = Sqlite();

        foreach (var sql in new[]
        {
            EligibilityService.WhereDivisionMatches(postgres.RosterSnapshots, EventScoped(age)).ToQueryString(),
            EligibilityService.WhereDivisionMatches(sqlite.RosterSnapshots, EventScoped(age)).ToQueryString(),
        })
        {
            // The column is bare (indexable), and the age travels as a parameter
            // rather than being inlined into the text of the query.
            Assert.Contains("\"CompetitionAgeGroup\"", sql);
            Assert.DoesNotContain($"= '{age}'", sql);
        }
    }

    [Fact]
    public void An_open_scope_adds_no_age_predicate_at_all()
    {
        // An all-ages house relay must not filter on the axis — not even with a
        // never-false catch-all that the planner would still have to evaluate.
        using var db = Postgres();
        var sql = EligibilityService.WhereDivisionMatches(db.RosterSnapshots, EventScoped()).ToQueryString();
        Assert.DoesNotContain("CompetitionAgeGroup\" =", sql);
        Assert.DoesNotContain("WHERE", sql);
    }
}
