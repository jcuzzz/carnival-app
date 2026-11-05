using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Identity.LocalAccounts;

public sealed record IssuedSetupLink(Guid LinkId, string Token, DateTimeOffset ExpiresAtUtc);

public enum SetupLinkKind
{
    /// <summary>The person chooses the username and becomes a System administrator.</summary>
    FirstAdministrator,
    Account,
}

public sealed record SetupLinkInspection(
    SetupLinkKind Kind,
    string? Username,
    string? DisplayName,
    DateTimeOffset ExpiresAtUtc,
    bool RequireAuthenticator);

/// <summary><c>Username</c> and <c>DisplayName</c> are read for a first-administrator link only.</summary>
public sealed record SetupCompletion(string? Token, string? Username, string? DisplayName, string? Password);

/// <summary>
/// Single use, 24 hours, and only the token's hash is stored. Inspecting never uses a link up, so a
/// link preview or a mail scanner's fetch leaves it good; setting the password does.
/// </summary>
public sealed class SetupLinks(
    CarnivalAppDbContext db,
    UserManager<LocalAccount> users,
    LocalSignInService signIn,
    SchoolSettingsProvider settings,
    AuditService audit,
    IConfiguration configuration,
    TimeProvider time,
    RoleAssignmentProvider? roleAssignments = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public const string SiteAddressConfig = "Site:Address";

    private const int TokenBytes = 32;
    private const int MaxTokenLength = 128;

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Absolute when <c>Site:Address</c> is set. A hash route, so the token reaches neither the proxy's
    /// nor the API's request log.
    /// </summary>
    public static string Link(IConfiguration configuration, string token)
    {
        var path = $"/#/setup/{token}";
        var site = configuration[SiteAddressConfig]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(site)) return path;
        return (site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site) + path;
    }

    /// <summary>
    /// Staged on the context, revoking any live link for the same account (or, for a first
    /// administrator, every live first-administrator link); the caller saves.
    /// </summary>
    public async Task<IssuedSetupLink> IssueAsync(
        Guid? accountId, SetupLinkPurpose purpose, string createdBySubject, CancellationToken ct = default)
    {
        await RevokeLiveAsync(accountId, ct);
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = time.GetUtcNow();
        var link = new LocalAccountSetupLink
        {
            Id = Guid.CreateVersion7(now),
            LocalAccountId = accountId,
            TokenHash = HashToken(token),
            Purpose = purpose,
            CreatedAtUtc = now,
            CreatedBySubject = createdBySubject,
            ExpiresAtUtc = now + Lifetime,
        };
        db.LocalAccountSetupLinks.Add(link);
        return new IssuedSetupLink(link.Id, token, link.ExpiresAtUtc);
    }

    /// <summary>Staged on the context; the caller saves.</summary>
    public async Task RevokeLiveAsync(Guid? accountId, CancellationToken ct = default)
    {
        var live = accountId is { } id
            ? db.LocalAccountSetupLinks.Where(l => l.LocalAccountId == id)
            : db.LocalAccountSetupLinks.Where(l => l.LocalAccountId == null
                && l.Purpose == SetupLinkPurpose.FirstAdministrator);
        var now = time.GetUtcNow();
        foreach (var link in await live.Where(l => l.UsedAtUtc == null && l.RevokedAtUtc == null).ToListAsync(ct))
            link.RevokedAtUtc = now;
    }

    /// <summary>
    /// Null when the link is unknown, used, revoked, expired or its account disabled, or when it is a
    /// first-administrator link and a local account exists.
    /// </summary>
    public async Task<SetupLinkInspection?> InspectAsync(string? token, CancellationToken ct = default)
    {
        if (await FindLiveAsync(token, ct) is not { } found) return null;
        var requireAuthenticator = (await settings.GetAsync(ct)).RequireAuthenticator;
        return found.Account is { } account
            ? new SetupLinkInspection(SetupLinkKind.Account, account.UserName, account.DisplayName,
                found.Link.ExpiresAtUtc, requireAuthenticator)
            : new SetupLinkInspection(SetupLinkKind.FirstAdministrator, null, null,
                found.Link.ExpiresAtUtc, requireAuthenticator);
    }

    /// <summary>
    /// Sets the password and uses the link in one transaction, then continues as a sign-in: the
    /// outcome is <c>SignedIn</c>, <c>EnrolmentRequired</c> when the school requires an
    /// authenticator, or <c>Failed</c>.
    /// </summary>
    public async Task<LocalSignInOutcome> CompleteAsync(
        SetupCompletion input, IPAddress? clientAddress, CancellationToken ct = default)
    {
        if (await FindLiveAsync(input.Token, ct) is not { } found) return Failed(ErrorCodes.SetupLinkInvalid);

        var now = time.GetUtcNow();
        var address = SignInAttemptLog.AddressText(clientAddress);
        var creating = found.Account is null;
        LocalAccount account;
        if (found.Account is { } existing)
            account = await db.LocalAccounts.SingleAsync(a => a.Id == existing.Id, ct);
        else
        {
            if (LocalUsernames.Validate(input.Username, configuration, out var username) is { } usernameError)
                return Failed(usernameError);
            var displayName = string.IsNullOrWhiteSpace(input.DisplayName)
                ? username
                : LocalUsernames.NormaliseDisplayName(input.DisplayName);
            if (displayName is null) return Failed(ErrorCodes.AccountDisplayNameInvalid);
            account = new LocalAccount
            {
                Id = Guid.CreateVersion7(now),
                UserName = username,
                NormalizedUserName = users.NormalizeName(username),
                DisplayName = displayName,
                CreatedAtUtc = now,
                CreatedBySubject = found.Link.CreatedBySubject,
            };
        }

        if (LocalPasswordValidator.IsOverLength(input.Password)) return Failed(ErrorCodes.PasswordTooLong);
        var password = LocalPasswordValidator.Normalise(input.Password ?? "");
        if (await LocalPasswordValidator.FirstErrorAsync(users, account, password) is { } passwordError)
            return Failed(passwordError);
        account.PasswordHash = users.PasswordHasher.HashPassword(account, password);
        LocalAccountsSetup.RotateStamps(account);

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var used = await db.LocalAccountSetupLinks
                .Where(l => l.Id == found.Link.Id && l.UsedAtUtc == null && l.RevokedAtUtc == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(l => l.UsedAtUtc, now)
                    .SetProperty(l => l.UsedFromAddress, address), ct);
            // Checked under the transaction: an account created since the link was read ends it.
            if (used != 1 || (creating && await db.LocalAccounts.AnyAsync(ct)))
            {
                db.ChangeTracker.Clear();
                return Failed(ErrorCodes.SetupLinkInvalid);
            }

            if (creating)
            {
                db.LocalAccounts.Add(account);
                GrantFirstAdministrator(account, found.Link.CreatedBySubject, now);
            }
            audit.Record(LocalAccountsSetup.SelfActor(account), AuditActions.LocalAccountSetupComplete,
                nameof(LocalAccount), account.Id.ToString(),
                newValues: new
                {
                    username = account.UserName,
                    displayName = account.DisplayName,
                    purpose = found.Link.Purpose,
                    usedAtUtc = now,
                    usedFromAddress = address,
                });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (creating)
            {
                // The unique name index is the real guard against a concurrent first administrator.
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                if (await users.FindByNameAsync(account.UserName!) is null) throw;
                return Failed(ErrorCodes.AccountUsernameTaken);
            }
            await transaction.CommitAsync(ct);
        }

        if (creating) roleAssignments?.Invalidate();
        return await signIn.FinishAsync(account, address, enrolmentCode: null, usedRecoveryCode: false, ct);
    }

    private void GrantFirstAdministrator(LocalAccount account, string grantedBy, DateTimeOffset now)
    {
        var assignment = new RoleAssignment
        {
            Id = Guid.CreateVersion7(now),
            SubjectId = LocalAccount.SubjectFor(account.Id),
            SubjectUpn = account.UserName!,
            SubjectType = RoleSubjectType.User,
            SubjectDisplayName = account.DisplayName,
            Role = PlatformRole.SystemAdministrator,
            Reason = "First administrator",
            CreatedAtUtc = now,
            CreatedBySubject = grantedBy,
        };
        db.RoleAssignments.Add(assignment);
        audit.Record(new AuditActor(grantedBy, null), AuditActions.RoleAssignmentGrant, nameof(RoleAssignment),
            assignment.Id.ToString(),
            newValues: new { upn = assignment.SubjectUpn, role = assignment.Role, reason = assignment.Reason });
    }

    private sealed record LiveLink(LocalAccountSetupLink Link, LocalAccount? Account);

    private async Task<LiveLink?> FindLiveAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return null;
        var hash = HashToken(token);
        var link = await db.LocalAccountSetupLinks.AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash, ct);
        if (link is null || link.UsedAtUtc is not null || link.RevokedAtUtc is not null
            || link.ExpiresAtUtc <= time.GetUtcNow())
            return null;
        if (link.LocalAccountId is not { } accountId)
            return await db.LocalAccounts.AnyAsync(ct) ? null : new LiveLink(link, null);

        var account = await db.LocalAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        return account is null || account.DisabledAtUtc is not null ? null : new LiveLink(link, account);
    }

    private static LocalSignInOutcome Failed(string code) => new LocalSignInOutcome.Failed(code);
}
