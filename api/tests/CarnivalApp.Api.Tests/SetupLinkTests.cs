using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity.LocalAccounts;
using Xunit;
using static CarnivalApp.Api.Tests.LocalAccountsHarness;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Setup links: single use, 24 hours, hash only; inspecting never uses one; and the administrator's
/// create, reset, disable and enable around them.
/// </summary>
public sealed class SetupLinkTests
{
    private static Task<SetupLinkInspection?> InspectAsync(LocalAccountsHarness h, string token) =>
        h.WithAsync(s => s.GetRequiredService<SetupLinks>().InspectAsync(token));

    private static LocalAccountAdministration Admin(IServiceProvider s) =>
        s.GetRequiredService<LocalAccountAdministration>();

    [Fact]
    public async Task Only_the_token_hash_is_stored()
    {
        await using var h = await CreateAsync();
        var issued = await h.CreateAccountAsync("jsmith");

        var row = await h.DbAsync(db => db.LocalAccountSetupLinks.AsNoTracking().SingleAsync());
        Assert.Equal(SetupLinks.HashToken(issued.SetupToken), row.TokenHash);
        Assert.NotEqual(issued.SetupToken, row.TokenHash);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", issued.SetupToken);
        Assert.Empty(await h.DbAsync(db => db.AuditEvents
            .Where(e => e.NewValuesJson!.Contains(issued.SetupToken)).ToListAsync()));
    }

    [Fact]
    public async Task Inspecting_never_uses_the_link_and_setting_the_password_uses_it_once()
    {
        await using var h = await CreateAsync();
        var issued = await h.CreateAccountAsync("jsmith", "Jo Smith");

        var inspected = await InspectAsync(h, issued.SetupToken);
        Assert.Equal(new SetupLinkInspection(SetupLinkKind.Account, "jsmith", "Jo Smith",
            h.Time.Now + SetupLinks.Lifetime, RequireAuthenticator: false), inspected);
        Assert.NotNull(await InspectAsync(h, issued.SetupToken));

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(issued.SetupToken, from: Elsewhere));

        Assert.Null(await InspectAsync(h, issued.SetupToken));
        AssertFailed(ErrorCodes.SetupLinkInvalid, await h.CompleteAsync(issued.SetupToken, "another long passphrase"));
        var link = await h.DbAsync(db => db.LocalAccountSetupLinks.AsNoTracking().SingleAsync());
        Assert.Equal(h.Time.Now, link.UsedAtUtc);
        Assert.Equal("10.0.0.20", link.UsedFromAddress);

        var dto = await h.WithAsync(s => Admin(s).GetAsync(issued.Account.Id));
        Assert.Equal(LocalAccountStatus.Active, dto!.Status);
        Assert.Equal(new SetupLinkSummary(link.ExpiresAtUtc, h.Time.Now, "10.0.0.20"), dto.SetupLink);
        Assert.Equal(h.Time.Now, dto.LastSignInAtUtc);
    }

    [Fact]
    public async Task A_refused_password_leaves_the_link_usable()
    {
        await using var h = await CreateAsync();
        var issued = await h.CreateAccountAsync("jsmith");

        AssertFailed(ErrorCodes.PasswordTooShort, await h.CompleteAsync(issued.SetupToken, "too short"));
        AssertFailed(ErrorCodes.PasswordTooPredictable, await h.CompleteAsync(issued.SetupToken, "jsmith jsmith 1234567"));

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(issued.SetupToken));
    }

    [Fact]
    public async Task A_link_lasts_24_hours()
    {
        await using var h = await CreateAsync();
        var issued = await h.CreateAccountAsync("jsmith");
        var start = h.Time.Now;

        h.Time.Now = start + SetupLinks.Lifetime - TimeSpan.FromSeconds(1);
        Assert.NotNull(await InspectAsync(h, issued.SetupToken));

        h.Time.Now = start + SetupLinks.Lifetime;
        Assert.Null(await InspectAsync(h, issued.SetupToken));
        AssertFailed(ErrorCodes.SetupLinkInvalid, await h.CompleteAsync(issued.SetupToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task An_unknown_token_is_invalid(string? token)
    {
        await using var h = await CreateAsync();
        Assert.Null(await h.WithAsync(s => s.GetRequiredService<SetupLinks>().InspectAsync(token)));
        AssertFailed(ErrorCodes.SetupLinkInvalid, await h.CompleteAsync(token!));
    }

    [Fact]
    public async Task Of_two_concurrent_completions_exactly_one_wins()
    {
        await using var h = await CreateAsync(iterations: LocalAccountsSetup.PasswordHashIterations);
        var issued = await h.CreateAccountAsync("jsmith");

        var outcomes = await Task.WhenAll(
            Task.Run(() => h.CompleteAsync(issued.SetupToken, "first long passphrase here")),
            Task.Run(() => h.CompleteAsync(issued.SetupToken, "second long passphrase here")));

        Assert.Single(outcomes.OfType<LocalSignInOutcome.SignedIn>());
        AssertFailed(ErrorCodes.SetupLinkInvalid, Assert.Single(outcomes.OfType<LocalSignInOutcome.Failed>()));
    }

    [Fact]
    public async Task A_first_administrator_link_creates_a_bound_system_administrator()
    {
        await using var h = await CreateAsync();
        var (exit, output, _) = await h.RunCommandAsync("first-admin");
        Assert.Equal(AccountsCommand.Done, exit);
        var token = output.Split("/#/setup/")[1].Split(' ')[0];

        var inspected = await InspectAsync(h, token);
        Assert.Equal(SetupLinkKind.FirstAdministrator, inspected!.Kind);
        Assert.Null(inspected.Username);

        AssertFailed(ErrorCodes.AccountUsernameInvalid, await h.CompleteAsync(token, username: "admin@example.org"));
        var signedIn = Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(token, username: "Backup-Admin"));

        Assert.Equal("backup-admin", signedIn.Account.UserName);
        var grant = await h.DbAsync(db => db.RoleAssignments.AsNoTracking().SingleAsync());
        Assert.Equal(PlatformRole.SystemAdministrator, grant.Role);
        Assert.Equal(LocalAccount.SubjectFor(signedIn.Account.Id), grant.SubjectId);
        Assert.Equal("backup-admin", grant.SubjectUpn);
        Assert.Equal(AccountsCommand.HostSubject, grant.CreatedBySubject);
        var complete = await h.DbAsync(db => db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.LocalAccountSetupComplete));
        Assert.Contains("\"usedFromAddress\":\"10.0.0.10\"", complete.NewValuesJson);
    }

    [Theory]
    [InlineData("people")]
    [InlineData("host")]
    [InlineData("database")]
    public async Task A_first_administrator_link_ends_once_any_local_account_exists(string createdThrough)
    {
        await using var h = await CreateAsync();
        var token = (await h.RunCommandAsync("first-admin")).Output.Split("/#/setup/")[1].Split(' ')[0];

        switch (createdThrough)
        {
            case "people":
                await h.CreateAccountAsync("jsmith");
                break;
            case "host":
                Assert.Equal(AccountsCommand.Done, (await h.RunCommandAsync("setup-link", "jsmith")).Exit);
                break;
            default:
                await h.DbAsync(async db =>
                {
                    db.LocalAccounts.Add(new LocalAccount
                    {
                        Id = Guid.CreateVersion7(), UserName = "jsmith", NormalizedUserName = "JSMITH",
                        DisplayName = "jsmith", CreatedBySubject = "test", SecurityStamp = "stamp",
                    });
                    return await db.SaveChangesAsync();
                });
                break;
        }

        Assert.Null(await InspectAsync(h, token));
        AssertFailed(ErrorCodes.SetupLinkInvalid, await h.CompleteAsync(token, username: "backup-admin"));
        Assert.Empty(await h.DbAsync(db => db.RoleAssignments.Where(r => r.SubjectUpn == "backup-admin").ToListAsync()));
        if (createdThrough != "database")
            Assert.NotNull((await h.DbAsync(db => db.LocalAccountSetupLinks.AsNoTracking()
                .SingleAsync(l => l.Purpose == SetupLinkPurpose.FirstAdministrator))).RevokedAtUtc);
    }

    [Fact]
    public async Task With_an_authenticator_required_setup_asks_for_enrolment_before_signing_in()
    {
        await using var h = await CreateAsync();
        await h.WithAsync(async s =>
        {
            var db = s.GetRequiredService<Data.CarnivalAppDbContext>();
            (await db.SchoolConfigurations.SingleAsync()).SettingsJson =
                School.SchoolSettings.SerialiseSignIn(true, School.LocalAccountAccess.SystemAdministrators);
            await db.SaveChangesAsync();
        });
        h.Services.GetRequiredService<School.SchoolSettingsProvider>().Invalidate();
        var issued = await h.CreateAccountAsync("jsmith");

        Assert.True((await InspectAsync(h, issued.SetupToken))!.RequireAuthenticator);
        var required = Assert.IsType<LocalSignInOutcome.EnrolmentRequired>(await h.CompleteAsync(issued.SetupToken));

        Assert.IsType<LocalSignInOutcome.SignedIn>(
            await h.SignInAsync("jsmith", enrolmentCode: CodeFor(required.Secret, h.Time.Now)));
    }

    [Fact]
    public async Task Reset_clears_the_credentials_and_revokes_the_previous_link()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var stamp = await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id).Select(a => a.SecurityStamp).SingleAsync());

        var first = await h.WithAsync(s => Admin(s).ResetAsync(h.Admin, id));
        Assert.Equal(LocalAccountStatus.AwaitingSetup, first.Value!.Account.Status);
        var second = await h.WithAsync(s => Admin(s).ResetAsync(h.Admin, id));

        Assert.NotEqual(stamp, await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id).Select(a => a.SecurityStamp).SingleAsync()));
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith"));
        Assert.Null(await InspectAsync(h, first.Value.SetupToken));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.CompleteAsync(second.Value!.SetupToken, "a fresh long passphrase"));
    }

    [Fact]
    public async Task Disable_ends_sign_in_and_revokes_the_link_and_enable_restores_sign_in()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var pending = await h.CreateAccountAsync("pending");

        var disabled = await h.WithAsync(s => Admin(s).DisableAsync(h.Admin, id));
        Assert.Equal(LocalAccountStatus.Disabled, disabled.Value!.Status);
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith"));
        Assert.Equal(SignInOutcome.Disabled,
            (await h.DbAsync(db => db.SignInAttempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync()))[^1].Outcome);
        var reset = await h.WithAsync(s => Admin(s).ResetAsync(h.Admin, id));
        Assert.Equal(ErrorCodes.AccountDisabled, reset.ErrorCode);

        await h.WithAsync(s => Admin(s).DisableAsync(h.Admin, pending.Account.Id));
        Assert.Null(await InspectAsync(h, pending.SetupToken));

        var enabled = await h.WithAsync(s => Admin(s).EnableAsync(h.Admin, id));
        Assert.Equal(LocalAccountStatus.Active, enabled.Value!.Status);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
    }

    [Fact]
    public async Task An_administrator_cannot_change_their_own_account_and_an_unknown_id_is_not_found()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var self = new AuditActor(LocalAccount.SubjectFor(id), "SystemAdministrator");

        Assert.Equal(ErrorCodes.AccountSelfChange, (await h.WithAsync(s => Admin(s).ResetAsync(self, id))).ErrorCode);
        Assert.Equal(ErrorCodes.AccountSelfChange, (await h.WithAsync(s => Admin(s).DisableAsync(self, id))).ErrorCode);
        Assert.Equal(ErrorCodes.AccountSelfChange, (await h.WithAsync(s => Admin(s).EnableAsync(self, id))).ErrorCode);
        Assert.Equal(ErrorCodes.AccountSelfChange, (await h.WithAsync(s => Admin(s).ClearLockoutAsync(self, id, Office))).ErrorCode);
        Assert.Equal(ErrorCodes.AccountNotFound, (await h.WithAsync(s => Admin(s).DisableAsync(h.Admin, Guid.NewGuid()))).ErrorCode);
    }

    [Fact]
    public async Task Create_refuses_bad_and_taken_names_and_audits_the_creation()
    {
        await using var h = await CreateAsync(new Dictionary<string, string?>
        {
            ["Identity:BootstrapAdministrators"] = "root-admin",
        });

        Assert.Equal(ErrorCodes.AccountUsernameInvalid,
            (await h.WithAsync(s => Admin(s).CreateAsync(h.Admin, "jo@example.org", "Jo"))).ErrorCode);
        Assert.Equal(ErrorCodes.AccountUsernameReserved,
            (await h.WithAsync(s => Admin(s).CreateAsync(h.Admin, "root-admin", "Root"))).ErrorCode);
        Assert.Equal(ErrorCodes.AccountDisplayNameInvalid,
            (await h.WithAsync(s => Admin(s).CreateAsync(h.Admin, "jsmith", "  "))).ErrorCode);
        Assert.True((await h.WithAsync(s => Admin(s).CreateAsync(h.Admin, "jsmith", "Jo Smith"))).Succeeded);
        Assert.Equal(ErrorCodes.AccountUsernameTaken,
            (await h.WithAsync(s => Admin(s).CreateAsync(h.Admin, "JSmith", "Another Jo"))).ErrorCode);

        var created = await h.DbAsync(db => db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.LocalAccountCreate));
        Assert.Equal("admin-1", created.ActorSubject);
        var list = await h.WithAsync(s => Admin(s).ListAsync());
        var only = Assert.Single(list);
        Assert.Equal(LocalAccountStatus.AwaitingSetup, only.Status);
        Assert.False(only.IsSystemAdministrator);
        Assert.Null(only.LastSignInAtUtc);
    }
}
