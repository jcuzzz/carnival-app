using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// <c>dotnet CarnivalApp.Api.dll accounts …</c> on the host, where shell access is the trust anchor.
/// Exit codes: 0 done, 2 nothing to do (the reason on stderr), 1 failure.
/// </summary>
public static class AccountsCommand
{
    public const int Done = 0;
    public const int Failure = 1;
    public const int NothingToDo = 2;

    public const string HostSubject = "host";

    private static readonly AuditActor Host = new(HostSubject, null);

    private const string Usage = """
        Usage:
          accounts first-admin              Print a setup link for the first System administrator,
                                            when no local account exists yet.
          accounts setup-link <username>    Print a setup link for <username> and clear its lockout,
                                            creating it as a System administrator if needed.
        """;

    public static Task<int> RunAsync(IServiceProvider services, string[] args) =>
        RunAsync(services, args, Console.Out, Console.Error);

    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            switch (args)
            {
                case ["first-admin"]:
                    return await FirstAdministratorAsync(scope.ServiceProvider, output, error);
                case ["setup-link", var username]:
                    return await SetupLinkAsync(scope.ServiceProvider, username, output, error);
                default:
                    await error.WriteLineAsync(Usage);
                    return Failure;
            }
        }
        catch (Exception failure)
        {
            await error.WriteLineAsync($"accounts: {failure.Message}");
            return Failure;
        }
    }

    private static async Task<int> FirstAdministratorAsync(
        IServiceProvider services, TextWriter output, TextWriter error)
    {
        var db = services.GetRequiredService<CarnivalAppDbContext>();
        if (await db.LocalAccounts.AnyAsync())
        {
            await error.WriteLineAsync(
                "A local account already exists, so no first-administrator link was issued. "
                + "To recover an account, run: accounts setup-link <username>");
            return NothingToDo;
        }

        var link = await services.GetRequiredService<SetupLinks>()
            .IssueAsync(null, SetupLinkPurpose.FirstAdministrator, HostSubject);
        services.GetRequiredService<AuditService>().Record(Host, AuditActions.LocalAccountSetupLink,
            nameof(LocalAccountSetupLink), link.LinkId.ToString(),
            newValues: new { purpose = SetupLinkPurpose.FirstAdministrator, expiresAtUtc = link.ExpiresAtUtc });
        await db.SaveChangesAsync();

        await output.WriteLineAsync($"First administrator: open {LinkText(services, link)}");
        return Done;
    }

    private static async Task<int> SetupLinkAsync(
        IServiceProvider services, string username, TextWriter output, TextWriter error)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (LocalUsernames.Validate(username, configuration, out var name) is { } refusal)
        {
            await error.WriteLineAsync($"'{username}' cannot be a local username ({refusal}): use 2 to 64 "
                + "lower-case letters, digits, '.', '_' or '-', starting with a letter or digit, and not a "
                + "bootstrap administrator entry.");
            return Failure;
        }

        var db = services.GetRequiredService<CarnivalAppDbContext>();
        var users = services.GetRequiredService<UserManager<LocalAccount>>();
        var audit = services.GetRequiredService<AuditService>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var normalised = users.NormalizeName(name);
        var account = await db.LocalAccounts.FirstOrDefaultAsync(a => a.NormalizedUserName == normalised);
        var created = account is null;
        var enabled = account?.DisabledAtUtc is not null;
        if (account is null)
        {
            account = new LocalAccount
            {
                Id = Guid.CreateVersion7(now),
                UserName = name,
                NormalizedUserName = normalised,
                DisplayName = name,
                CreatedAtUtc = now,
                CreatedBySubject = HostSubject,
            };
            LocalAccountsSetup.RotateStamps(account);
            db.LocalAccounts.Add(account);
            await services.GetRequiredService<SetupLinks>().RevokeLiveAsync(null);
            audit.Record(Host, AuditActions.LocalAccountCreate, nameof(LocalAccount), account.Id.ToString(),
                newValues: new { username = name, displayName = name });
        }
        else
        {
            await LocalAccountAdministration.ClearCredentialsAsync(db, account, default);
            account.DisabledAtUtc = null;
        }

        services.GetRequiredService<SignInAttemptLog>()
            .Add(account.Id, SignInAttemptLog.HostAddress, SignInOutcome.LockoutCleared);
        await EnsureSystemAdministratorAsync(db, audit, account, now);
        var link = await services.GetRequiredService<SetupLinks>()
            .IssueAsync(account.Id, SetupLinkPurpose.Host, HostSubject);
        audit.Record(Host, AuditActions.LocalAccountSetupLink, nameof(LocalAccount), account.Id.ToString(),
            newValues: new
            {
                purpose = SetupLinkPurpose.Host,
                expiresAtUtc = link.ExpiresAtUtc,
                credentialsCleared = !created,
                enabled,
                lockoutCleared = true,
            });
        await db.SaveChangesAsync();
        services.GetService<RoleAssignmentProvider>()?.Invalidate();

        await output.WriteLineAsync($"Setup link for {name}: open {LinkText(services, link)}");
        return Done;
    }

    private static async Task EnsureSystemAdministratorAsync(
        CarnivalAppDbContext db, AuditService audit, LocalAccount account, DateTimeOffset now)
    {
        var subject = LocalAccount.SubjectFor(account.Id);
        if (await db.RoleAssignments.AnyAsync(row => row.RevokedAtUtc == null
                && row.Role == PlatformRole.SystemAdministrator && row.SubjectId == subject))
            return;

        var assignment = new RoleAssignment
        {
            Id = Guid.CreateVersion7(now),
            SubjectId = subject,
            SubjectUpn = account.UserName!,
            SubjectType = RoleSubjectType.User,
            SubjectDisplayName = account.DisplayName,
            Role = PlatformRole.SystemAdministrator,
            Reason = "Host recovery (accounts setup-link)",
            CreatedAtUtc = now,
            CreatedBySubject = HostSubject,
        };
        db.RoleAssignments.Add(assignment);
        audit.Record(Host, AuditActions.RoleAssignmentGrant, nameof(RoleAssignment), assignment.Id.ToString(),
            newValues: new { upn = assignment.SubjectUpn, role = assignment.Role, reason = assignment.Reason });
    }

    private static string LinkText(IServiceProvider services, IssuedSetupLink link) =>
        $"{SetupLinks.Link(services.GetRequiredService<IConfiguration>(), link.Token)}  "
        + $"(valid {(int)SetupLinks.Lifetime.TotalHours} hours)";
}
