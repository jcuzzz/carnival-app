using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>Today's name behind a stored subject, as the import and role lists show it.</summary>
public sealed class SubjectNamesTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static LocalAccount Account(Guid id, string displayName) => new()
    {
        Id = id,
        UserName = $"user-{id:N}",
        NormalizedUserName = $"USER-{id:N}",
        DisplayName = displayName,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static RoleAssignment Grant(string subjectId, string name, int daysAgo) => new()
    {
        Id = Guid.CreateVersion7(),
        SubjectId = subjectId,
        SubjectUpn = $"{Guid.NewGuid():N}@example.org",
        SubjectDisplayName = name,
        Role = PlatformRole.Viewer,
        Reason = "test",
        CreatedBySubject = "test",
        CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo),
    };

    private async Task<Dictionary<string, string>> ResolveAsync(
        Action<CarnivalAppDbContext> seed, params string[] subjects)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
        return await SubjectNames.ResolveAsync(db, subjects, default);
    }

    [Fact]
    public async Task A_local_accounts_name_wins_over_any_grants_name()
    {
        var id = Guid.CreateVersion7();
        var subject = LocalAccount.SubjectFor(id);

        var names = await ResolveAsync(db =>
        {
            db.LocalAccounts.Add(Account(id, "Jo Admin"));
            db.RoleAssignments.Add(Grant(subject, "Jo From A Grant", daysAgo: 0));
        }, subject);

        Assert.Equal("Jo Admin", names[subject]);
    }

    [Fact]
    public async Task A_blank_local_name_falls_through_to_the_newest_grants_name()
    {
        var id = Guid.CreateVersion7();
        var subject = LocalAccount.SubjectFor(id);

        var names = await ResolveAsync(db =>
        {
            db.LocalAccounts.Add(Account(id, "  "));
            // Newest in the middle, so neither insertion order nor its reverse picks it by accident.
            db.RoleAssignments.AddRange(
                Grant(subject, "Jo Old", daysAgo: 30),
                Grant(subject, "Jo New", daysAgo: 1),
                Grant(subject, "Jo Older", daysAgo: 60));
        }, subject);

        Assert.Equal("Jo New", names[subject]);
    }

    [Fact]
    public async Task The_newest_grant_names_a_provider_subject_and_an_unknown_one_is_left_out()
    {
        var names = await ResolveAsync(db => db.RoleAssignments.AddRange(
                Grant("entra-1", "Sam Older", daysAgo: 60),
                Grant("entra-1", "Sam Newest", daysAgo: 1),
                Grant("entra-1", "Sam Old", daysAgo: 30)),
            "entra-1", "nobody", "host");

        Assert.Equal("Sam Newest", names["entra-1"]);
        Assert.False(names.ContainsKey("nobody"));
        Assert.False(names.ContainsKey("host"));
    }
}
