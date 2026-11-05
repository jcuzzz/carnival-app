using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity.LocalAccounts;
using Xunit;
using static CarnivalApp.Api.Tests.LocalAccountsHarness;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The host's <c>accounts</c> command: the line the deploy script prints, and the exit codes it
/// branches on (0 done, 2 nothing to do, 1 failure).
/// </summary>
public sealed class AccountsCommandTests
{
    private const string LinkLine =
        @"^First administrator: open https://carnival\.example\.org/#/setup/([A-Za-z0-9_-]{43})  \(valid 24 hours\)\r?\n$";

    [Fact]
    public async Task First_admin_prints_one_link_while_no_local_account_exists()
    {
        await using var h = await CreateAsync();

        var (exit, output, error) = await h.RunCommandAsync("first-admin");

        Assert.Equal(AccountsCommand.Done, exit);
        Assert.Matches(LinkLine, output);
        Assert.Empty(error);
        var audit = await h.DbAsync(db => db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.LocalAccountSetupLink));
        Assert.Equal(AccountsCommand.HostSubject, audit.ActorSubject);
    }

    [Fact]
    public async Task A_second_first_admin_link_revokes_the_first()
    {
        await using var h = await CreateAsync();
        var first = Token((await h.RunCommandAsync("first-admin")).Output);
        var second = Token((await h.RunCommandAsync("first-admin")).Output);

        AssertFailed(ErrorCodes.SetupLinkInvalid, await h.CompleteAsync(first, username: "backup-admin"));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(second, username: "backup-admin"));
    }

    [Fact]
    public async Task First_admin_exits_2_once_a_local_account_exists()
    {
        await using var h = await CreateAsync();
        await h.CreateAccountAsync("jsmith");

        var (exit, output, error) = await h.RunCommandAsync("first-admin");

        Assert.Equal(AccountsCommand.NothingToDo, exit);
        Assert.Empty(output);
        Assert.Contains("setup-link", error);
        Assert.Single(await h.DbAsync(db => db.LocalAccountSetupLinks.ToListAsync()));
    }

    [Fact]
    public async Task First_admin_issues_a_link_beside_a_provider_too()
    {
        await using var h = await CreateAsync(new Dictionary<string, string?>
        {
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000002",
        });

        var (exit, output, _) = await h.RunCommandAsync("first-admin");

        Assert.Equal(AccountsCommand.Done, exit);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(Token(output), username: "backup-admin"));
    }

    [Fact]
    public async Task Without_a_site_address_the_link_is_relative()
    {
        await using var h = await CreateAsync(new Dictionary<string, string?> { [SetupLinks.SiteAddressConfig] = "" });

        var (_, output, _) = await h.RunCommandAsync("first-admin");

        Assert.Matches(@"^First administrator: open /#/setup/[A-Za-z0-9_-]{43}  \(valid 24 hours\)", output);
    }

    [Fact]
    public async Task Setup_link_creates_a_bound_system_administrator()
    {
        await using var h = await CreateAsync();

        var (exit, output, _) = await h.RunCommandAsync("setup-link", "Backup-Admin");

        Assert.Equal(AccountsCommand.Done, exit);
        Assert.StartsWith("Setup link for backup-admin: open https://carnival.example.org/#/setup/", output);
        var account = await h.DbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync());
        Assert.Equal("backup-admin", account.DisplayName);
        var grant = await h.DbAsync(db => db.RoleAssignments.AsNoTracking().SingleAsync());
        Assert.Equal((LocalAccount.SubjectFor(account.Id), PlatformRole.SystemAdministrator, AccountsCommand.HostSubject),
            (grant.SubjectId, grant.Role, grant.CreatedBySubject));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(Token(output)));
    }

    [Fact]
    public async Task Setup_link_resets_enables_and_unlocks_an_existing_account_without_a_second_grant()
    {
        await using var h = await CreateAsync();
        var first = Token((await h.RunCommandAsync("setup-link", "backup-admin")).Output);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(first));
        for (var i = 0; i < SignInAttemptLog.FailureThreshold; i++)
            await h.SignInAsync("backup-admin", "a wrong passphrase guess");
        var id = await h.DbAsync(db => db.LocalAccounts.Select(a => a.Id).SingleAsync());
        await h.DbAsync(db => db.LocalAccounts.ExecuteUpdateAsync(set => set.SetProperty(a => a.DisabledAtUtc, h.Time.Now)));

        var (exit, output, _) = await h.RunCommandAsync("setup-link", "backup-admin");

        Assert.Equal(AccountsCommand.Done, exit);
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("backup-admin"));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(Token(output), "a new passphrase for the safe"));
        Assert.Single(await h.DbAsync(db => db.RoleAssignments.ToListAsync()));
        Assert.Contains(await h.DbAsync(db => db.SignInAttempts.Where(a => a.LocalAccountId == id)
            .Select(a => a.Outcome).ToListAsync()), outcome => outcome == SignInOutcome.LockoutCleared);
    }

    [Theory]
    [InlineData("setup-link", "name@example.org")]
    [InlineData("setup-link", "root-admin")]
    [InlineData("setup-link")]
    [InlineData("rename", "jsmith")]
    [InlineData]
    public async Task Bad_arguments_exit_1_and_change_nothing(params string[] args)
    {
        await using var h = await CreateAsync(new Dictionary<string, string?>
        {
            ["Identity:BootstrapAdministrators"] = "root-admin",
        });

        var (exit, output, error) = await h.RunCommandAsync(args);

        Assert.Equal(AccountsCommand.Failure, exit);
        Assert.Empty(output);
        Assert.NotEmpty(error);
        Assert.Empty(await h.DbAsync(db => db.LocalAccounts.ToListAsync()));
    }

    private static string Token(string output) => output.Split("/#/setup/")[1].Split(' ')[0];
}
