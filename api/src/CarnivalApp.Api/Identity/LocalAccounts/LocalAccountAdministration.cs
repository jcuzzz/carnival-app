using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Identity.LocalAccounts;

public enum LocalAccountStatus
{
    AwaitingSetup,
    Active,
    Disabled,
}

/// <summary>The account's newest link, so an administrator sees when and where it was used.</summary>
public sealed record SetupLinkSummary(DateTimeOffset ExpiresAtUtc, DateTimeOffset? UsedAtUtc, string? UsedFromAddress);

public sealed record LocalAccountDto(
    Guid Id,
    string Username,
    string DisplayName,
    LocalAccountStatus Status,
    bool AuthenticatorEnrolled,
    bool IsSystemAdministrator,
    bool LockedOut,
    DateTimeOffset? LastSignInAtUtc,
    SetupLinkSummary? SetupLink,
    DateTimeOffset CreatedAtUtc);

/// <summary>The token is shown once, to the administrator who hands it over.</summary>
public sealed record IssuedAccountLink(LocalAccountDto Account, string SetupToken, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Accounts are disabled, never deleted. Reset and disable rotate the security stamp, which ends
/// the account's sessions at their next request. An administrator never acts on their own account.
/// </summary>
public sealed class LocalAccountAdministration(
    CarnivalAppDbContext db,
    UserManager<LocalAccount> users,
    SetupLinks links,
    SignInAttemptLog attempts,
    AuditService audit,
    IConfiguration configuration,
    TimeProvider time)
{
    public async Task<IReadOnlyList<LocalAccountDto>> ListAsync(CancellationToken ct = default)
    {
        var accounts = await db.LocalAccounts.AsNoTracking().ToListAsync(ct);
        var result = new List<LocalAccountDto>(accounts.Count);
        foreach (var account in accounts.OrderBy(a => a.UserName, StringComparer.Ordinal))
            result.Add(await ProjectAsync(account, ct));
        return result;
    }

    public async Task<LocalAccountDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.LocalAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } account
            ? await ProjectAsync(account, ct)
            : null;

    public async Task<LocalAccountResult<IssuedAccountLink>> CreateAsync(
        AuditActor actor, string? username, string? displayName, CancellationToken ct = default)
    {
        if (LocalUsernames.Validate(username, configuration, out var name) is { } usernameError)
            return LocalAccountResult<IssuedAccountLink>.Fail(usernameError);
        if (LocalUsernames.NormaliseDisplayName(displayName) is not { } display)
            return LocalAccountResult<IssuedAccountLink>.Fail(ErrorCodes.AccountDisplayNameInvalid);
        var normalised = users.NormalizeName(name);
        if (await db.LocalAccounts.AnyAsync(a => a.NormalizedUserName == normalised, ct))
            return LocalAccountResult<IssuedAccountLink>.Fail(ErrorCodes.AccountUsernameTaken);

        var now = time.GetUtcNow();
        var account = new LocalAccount
        {
            Id = Guid.CreateVersion7(now),
            UserName = name,
            NormalizedUserName = normalised,
            DisplayName = display,
            CreatedAtUtc = now,
            CreatedBySubject = actor.Subject,
        };
        LocalAccountsSetup.RotateStamps(account);
        db.LocalAccounts.Add(account);
        await links.RevokeLiveAsync(null, ct);
        var link = await links.IssueAsync(account.Id, SetupLinkPurpose.Create, actor.Subject, ct);
        audit.Record(actor, AuditActions.LocalAccountCreate, nameof(LocalAccount), account.Id.ToString(),
            newValues: new { username = name, displayName = display, setupLinkExpiresAtUtc = link.ExpiresAtUtc });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique name index is the real guard: two concurrent creates both clear the check.
            db.ChangeTracker.Clear();
            if (!await db.LocalAccounts.AnyAsync(a => a.NormalizedUserName == normalised, ct)) throw;
            return LocalAccountResult<IssuedAccountLink>.Fail(ErrorCodes.AccountUsernameTaken);
        }

        return LocalAccountResult<IssuedAccountLink>.Ok(
            new IssuedAccountLink(await ProjectAsync(account, ct), link.Token, link.ExpiresAtUtc));
    }

    /// <summary>Clears the password, authenticator and recovery codes, and issues a new link.</summary>
    public async Task<LocalAccountResult<IssuedAccountLink>> ResetAsync(
        AuditActor actor, Guid id, CancellationToken ct = default)
    {
        var (account, refusal) = await FindForChangeAsync(actor, id, ct);
        if (account is null) return LocalAccountResult<IssuedAccountLink>.Fail(refusal!);
        if (account.DisabledAtUtc is not null)
            return LocalAccountResult<IssuedAccountLink>.Fail(ErrorCodes.AccountDisabled);

        await ClearCredentialsAsync(db, account, ct);
        var link = await links.IssueAsync(account.Id, SetupLinkPurpose.Reset, actor.Subject, ct);
        audit.Record(actor, AuditActions.LocalAccountReset, nameof(LocalAccount), account.Id.ToString(),
            newValues: new { setupLinkExpiresAtUtc = link.ExpiresAtUtc });
        await db.SaveChangesAsync(ct);

        return LocalAccountResult<IssuedAccountLink>.Ok(
            new IssuedAccountLink(await ProjectAsync(account, ct), link.Token, link.ExpiresAtUtc));
    }

    public async Task<LocalAccountResult<LocalAccountDto>> DisableAsync(
        AuditActor actor, Guid id, CancellationToken ct = default)
    {
        var (account, refusal) = await FindForChangeAsync(actor, id, ct);
        if (account is null) return LocalAccountResult<LocalAccountDto>.Fail(refusal!);
        if (account.DisabledAtUtc is null)
        {
            account.DisabledAtUtc = time.GetUtcNow();
            LocalAccountsSetup.RotateStamps(account);
            await links.RevokeLiveAsync(account.Id, ct);
            audit.Record(actor, AuditActions.LocalAccountDisable, nameof(LocalAccount), account.Id.ToString());
            await db.SaveChangesAsync(ct);
        }
        return LocalAccountResult<LocalAccountDto>.Ok(await ProjectAsync(account, ct));
    }

    public async Task<LocalAccountResult<LocalAccountDto>> EnableAsync(
        AuditActor actor, Guid id, CancellationToken ct = default)
    {
        var (account, refusal) = await FindForChangeAsync(actor, id, ct);
        if (account is null) return LocalAccountResult<LocalAccountDto>.Fail(refusal!);
        if (account.DisabledAtUtc is not null)
        {
            account.DisabledAtUtc = null;
            account.ConcurrencyStamp = Guid.NewGuid().ToString();
            audit.Record(actor, AuditActions.LocalAccountEnable, nameof(LocalAccount), account.Id.ToString());
            await db.SaveChangesAsync(ct);
        }
        return LocalAccountResult<LocalAccountDto>.Ok(await ProjectAsync(account, ct));
    }

    /// <summary>Clears the lockout at every address.</summary>
    public async Task<LocalAccountResult<LocalAccountDto>> ClearLockoutAsync(
        AuditActor actor, Guid id, IPAddress? clientAddress, CancellationToken ct = default)
    {
        var (account, refusal) = await FindForChangeAsync(actor, id, ct);
        if (account is null) return LocalAccountResult<LocalAccountDto>.Fail(refusal!);
        attempts.Add(account.Id, SignInAttemptLog.AddressText(clientAddress), SignInOutcome.LockoutCleared);
        audit.Record(actor, AuditActions.LocalAccountLockoutClear, nameof(LocalAccount), account.Id.ToString());
        await db.SaveChangesAsync(ct);
        return LocalAccountResult<LocalAccountDto>.Ok(await ProjectAsync(account, ct));
    }

    /// <summary>Staged on the context; the caller saves.</summary>
    internal static async Task ClearCredentialsAsync(CarnivalAppDbContext db, LocalAccount account, CancellationToken ct)
    {
        account.PasswordHash = null;
        account.TwoFactorEnabled = false;
        account.LastTotpStep = null;
        LocalAccountsSetup.RotateStamps(account);
        db.RemoveRange(await db.Set<IdentityUserToken<Guid>>().Where(t => t.UserId == account.Id).ToListAsync(ct));
    }

    private async Task<(LocalAccount? Account, string? Refusal)> FindForChangeAsync(
        AuditActor actor, Guid id, CancellationToken ct)
    {
        if (actor.Subject == LocalAccount.SubjectFor(id)) return (null, ErrorCodes.AccountSelfChange);
        var account = await db.LocalAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        return account is null ? (null, ErrorCodes.AccountNotFound) : (account, null);
    }

    private async Task<LocalAccountDto> ProjectAsync(LocalAccount account, CancellationToken ct)
    {
        // Newest by Id: v7 ids are time-ordered, and SQLite cannot order a DateTimeOffset.
        var link = await db.LocalAccountSetupLinks.AsNoTracking()
            .Where(l => l.LocalAccountId == account.Id)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync(ct);
        var status = account.DisabledAtUtc is not null ? LocalAccountStatus.Disabled
            : account.PasswordHash is null ? LocalAccountStatus.AwaitingSetup
            : LocalAccountStatus.Active;
        return new LocalAccountDto(
            account.Id,
            account.UserName!,
            account.DisplayName,
            status,
            account.TwoFactorEnabled,
            await LocalAccountPolicy.IsSystemAdministratorAsync(db, account.Id, ct),
            await attempts.IsLockedOutAnywhereAsync(account.Id, ct),
            await attempts.LastSignInAsync(account.Id, ct),
            link is null ? null : new SetupLinkSummary(link.ExpiresAtUtc, link.UsedAtUtc, link.UsedFromAddress),
            account.CreatedAtUtc);
    }
}
