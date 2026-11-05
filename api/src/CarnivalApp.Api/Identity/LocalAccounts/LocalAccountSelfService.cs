using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Identity.LocalAccounts;

public sealed record MyLocalAccount(
    string Username,
    string DisplayName,
    bool AuthenticatorEnrolled,
    int RecoveryCodesLeft,
    bool RequireAuthenticator);

/// <summary><c>Secret</c> is the base32 key, for typing in when the QR code cannot be scanned.</summary>
public sealed record AuthenticatorSetup(string Secret, string OtpauthUri);

/// <summary>The codes are shown once; only their hashes are kept.</summary>
public sealed record EnrolledAuthenticator(LocalAccount Account, IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// A signed-in local account acting on itself. Changing the password and enrolling an
/// authenticator rotate the security stamp; the returned account carries the new one.
/// </summary>
public sealed class LocalAccountSelfService(
    CarnivalAppDbContext db,
    UserManager<LocalAccount> users,
    IUserStore<LocalAccount> store,
    SchoolSettingsProvider settings,
    SignInAttemptLog attempts,
    AuditService audit,
    TimeProvider time)
{
    public const int RecoveryCodeCount = 10;

    public async Task<MyLocalAccount?> GetAsync(Guid accountId, CancellationToken ct = default)
    {
        if (await ActiveAsync(accountId, ct) is not { } account) return null;
        return new MyLocalAccount(
            account.UserName!,
            account.DisplayName,
            account.TwoFactorEnabled,
            account.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(account) : 0,
            (await settings.GetAsync(ct)).RequireAuthenticator);
    }

    /// <summary>A wrong current password counts towards the lockout at this address.</summary>
    public async Task<LocalAccountResult<LocalAccount>> ChangePasswordAsync(
        Guid accountId, string? currentPassword, string? newPassword, IPAddress? clientAddress,
        CancellationToken ct = default)
    {
        if (await ActiveAsync(accountId, ct) is not { } account)
            return LocalAccountResult<LocalAccount>.Fail(ErrorCodes.AccountNotFound);
        if (await VerifyCurrentPasswordAsync(account, currentPassword, clientAddress, ct) is { } refusal)
            return LocalAccountResult<LocalAccount>.Fail(refusal);
        if (LocalPasswordValidator.IsOverLength(newPassword))
            return LocalAccountResult<LocalAccount>.Fail(ErrorCodes.PasswordTooLong);

        var password = LocalPasswordValidator.Normalise(newPassword ?? "");
        if (await LocalPasswordValidator.FirstErrorAsync(users, account, password) is { } error)
            return LocalAccountResult<LocalAccount>.Fail(error);

        account.PasswordHash = users.PasswordHasher.HashPassword(account, password);
        LocalAccountsSetup.RotateStamps(account);
        audit.Record(LocalAccountsSetup.SelfActor(account), AuditActions.LocalAccountPasswordChange,
            nameof(LocalAccount), account.Id.ToString());
        await db.SaveChangesAsync(ct);
        return LocalAccountResult<LocalAccount>.Ok(account);
    }

    /// <summary>
    /// Re-proves the password, as changing it does, and issues a fresh pending key each time.
    /// </summary>
    public async Task<LocalAccountResult<AuthenticatorSetup>> BeginAuthenticatorAsync(
        Guid accountId, string? currentPassword, IPAddress? clientAddress, CancellationToken ct = default)
    {
        if (await ActiveAsync(accountId, ct) is not { } account)
            return LocalAccountResult<AuthenticatorSetup>.Fail(ErrorCodes.AccountNotFound);
        if (account.TwoFactorEnabled)
            return LocalAccountResult<AuthenticatorSetup>.Fail(ErrorCodes.AuthenticatorAlreadyEnrolled);
        if (await VerifyCurrentPasswordAsync(account, currentPassword, clientAddress, ct) is { } refusal)
            return LocalAccountResult<AuthenticatorSetup>.Fail(refusal);
        return LocalAccountResult<AuthenticatorSetup>.Ok(await PendingAuthenticatorAsync(account, fresh: true, ct));
    }

    /// <summary>A wrong code counts towards the lockout at this address, as at sign-in.</summary>
    public async Task<LocalAccountResult<EnrolledAuthenticator>> ConfirmAuthenticatorAsync(
        Guid accountId, string? code, IPAddress? clientAddress = null, CancellationToken ct = default)
    {
        if (await ActiveAsync(accountId, ct) is not { } account)
            return LocalAccountResult<EnrolledAuthenticator>.Fail(ErrorCodes.AccountNotFound);
        if (account.TwoFactorEnabled)
            return LocalAccountResult<EnrolledAuthenticator>.Fail(ErrorCodes.AuthenticatorAlreadyEnrolled);

        var address = SignInAttemptLog.AddressText(clientAddress);
        if (await attempts.IsLockedOutAsync(account.Id, address, ct))
        {
            await attempts.RecordAsync(account.Id, address, SignInOutcome.LockedOut, ct);
            return LocalAccountResult<EnrolledAuthenticator>.Fail(ErrorCodes.CodeInvalid);
        }
        if (await EnrolAsync(account, code, ct) is { } codes)
            return LocalAccountResult<EnrolledAuthenticator>.Ok(new EnrolledAuthenticator(account, codes));
        await attempts.RecordAsync(account.Id, address, SignInOutcome.WrongCode, ct);
        return LocalAccountResult<EnrolledAuthenticator>.Fail(ErrorCodes.CodeInvalid);
    }

    // At sign-in the pending key is kept, since each step is a new request; setting one does not
    // rotate the stamp, since nothing about signing in has changed yet.
    internal async Task<AuthenticatorSetup> PendingAuthenticatorAsync(
        LocalAccount account, bool fresh, CancellationToken ct)
    {
        var key = fresh ? null : await users.GetAuthenticatorKeyAsync(account);
        if (string.IsNullOrEmpty(key))
        {
            key = users.GenerateNewAuthenticatorKey();
            await ((IUserAuthenticatorKeyStore<LocalAccount>)store).SetAuthenticatorKeyAsync(account, key, ct);
            await db.SaveChangesAsync(ct);
        }
        var issuer = (await settings.GetAsync(ct)).Name;
        return new AuthenticatorSetup(key, Totp.OtpauthUri(issuer, account.UserName!, key));
    }

    /// <summary>Null when the code does not match the pending key.</summary>
    internal async Task<IReadOnlyList<string>?> EnrolAsync(LocalAccount account, string? code, CancellationToken ct)
    {
        var key = await users.GetAuthenticatorKeyAsync(account);
        if (string.IsNullOrEmpty(key) || !Totp.Verify(key, code, time.GetUtcNow(), out var step)) return null;

        account.TwoFactorEnabled = true;
        account.LastTotpStep = step;
        LocalAccountsSetup.RotateStamps(account);
        audit.Record(LocalAccountsSetup.SelfActor(account), AuditActions.LocalAccountAuthenticatorEnrol,
            nameof(LocalAccount), account.Id.ToString(), newValues: new { recoveryCodes = RecoveryCodeCount });

        // Saves everything staged above in one write, or nothing when the account changed meanwhile.
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(account, RecoveryCodeCount);
        if (codes is not null) return [.. codes];
        db.ChangeTracker.Clear();
        return null;
    }

    /// <summary>Null when proven; a wrong password counts towards the lockout at this address.</summary>
    private async Task<string?> VerifyCurrentPasswordAsync(
        LocalAccount account, string? password, IPAddress? clientAddress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || LocalPasswordValidator.IsOverLength(password))
            return ErrorCodes.CurrentPasswordWrong;

        var address = SignInAttemptLog.AddressText(clientAddress);
        if (await attempts.IsLockedOutAsync(account.Id, address, ct))
        {
            await attempts.RecordAsync(account.Id, address, SignInOutcome.LockedOut, ct);
            return ErrorCodes.CurrentPasswordWrong;
        }
        if (await users.CheckPasswordAsync(account, LocalPasswordValidator.Normalise(password))) return null;
        await attempts.RecordAsync(account.Id, address, SignInOutcome.WrongPassword, ct);
        return ErrorCodes.CurrentPasswordWrong;
    }

    private Task<LocalAccount?> ActiveAsync(Guid accountId, CancellationToken ct) =>
        db.LocalAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.DisabledAtUtc == null, ct);
}
