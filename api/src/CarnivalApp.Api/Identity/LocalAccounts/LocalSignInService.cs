using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Stateless: each step resends the password. <c>Code</c> is the authenticator's, <c>RecoveryCode</c>
/// one of the account's recovery codes, <c>EnrolmentCode</c> the first code from a newly added
/// authenticator when the school requires one.
/// </summary>
public sealed record LocalSignInInput(
    string? Username,
    string? Password,
    string? Code = null,
    string? RecoveryCode = null,
    string? EnrolmentCode = null);

public abstract record LocalSignInOutcome
{
    /// <summary><c>RecoveryCodes</c> is set when this sign-in enrolled an authenticator.</summary>
    public sealed record SignedIn(LocalAccount Account, IReadOnlyList<string>? RecoveryCodes) : LocalSignInOutcome;

    public sealed record CodeRequired : LocalSignInOutcome;

    /// <summary>The school requires an authenticator and the account has none yet.</summary>
    public sealed record EnrolmentRequired(string Secret, string OtpauthUri) : LocalSignInOutcome;

    /// <summary>
    /// <c>SIGN_IN_FAILED</c>, <c>CODE_INVALID</c>, <c>LOCAL_SIGN_IN_NOT_ALLOWED</c> or
    /// <c>VALIDATION_FAILED</c>; from a setup link also <c>SETUP_LINK_INVALID</c>, the password
    /// codes and the account-name codes.
    /// </summary>
    public sealed record Failed(string ErrorCode) : LocalSignInOutcome;
}

/// <summary>
/// Every path that answers <c>SIGN_IN_FAILED</c> verifies one password hash, so the answer and its
/// timing say nothing about which names exist or why the attempt failed. The policy is checked only
/// once the credentials are proven, so a refusal reveals nothing to a guesser.
/// </summary>
public sealed class LocalSignInService(
    CarnivalAppDbContext db,
    UserManager<LocalAccount> users,
    SignInAttemptLog attempts,
    LocalAccountPolicy policy,
    LocalAccountSelfService self,
    SchoolSettingsProvider settings,
    AuditService audit,
    TimeProvider time)
{
    private static readonly LocalSignInOutcome SignInFailed = new LocalSignInOutcome.Failed(ErrorCodes.SignInFailed);
    private static readonly LocalSignInOutcome CodeInvalid = new LocalSignInOutcome.Failed(ErrorCodes.CodeInvalid);

    public async Task<LocalSignInOutcome> SignInAsync(
        LocalSignInInput input, IPAddress? clientAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrEmpty(input.Password))
            return new LocalSignInOutcome.Failed(ErrorCodes.ValidationFailed);
        if (LocalPasswordValidator.IsOverLength(input.Password)) return SignInFailed;

        var address = SignInAttemptLog.AddressText(clientAddress);
        var password = LocalPasswordValidator.Normalise(input.Password);
        var username = LocalUsernames.Normalise(input.Username)!;
        var account = username.Length <= LocalUsernames.MaxLength ? await users.FindByNameAsync(username) : null;

        // The same lockout queries run for an unknown name, so their timing does not tell the two apart.
        var locked = await attempts.IsLockedOutAsync(account?.Id ?? Guid.Empty, address, ct);
        if (account is null)
            return await RefuseAsync(null, password, address, SignInOutcome.WrongPassword, ct);
        if (locked)
            return await RefuseAsync(account, password, address, SignInOutcome.LockedOut, ct);
        if (account.DisabledAtUtc is not null)
            return await RefuseAsync(account, password, address, SignInOutcome.Disabled, ct);
        if (account.PasswordHash is null)
            return await RefuseAsync(account, password, address, SignInOutcome.WrongPassword, ct);
        if (!await users.CheckPasswordAsync(account, password))
        {
            await attempts.RecordAsync(account.Id, address, SignInOutcome.WrongPassword, ct);
            return SignInFailed;
        }

        var usedRecoveryCode = false;
        if (account.TwoFactorEnabled)
        {
            if (!string.IsNullOrWhiteSpace(input.RecoveryCode))
            {
                // Staged first, so the redemption's own save writes it; a wrong code or a lost race
                // leaves it and the redemption unsaved, and the tracker is cleared before recording.
                audit.Record(LocalAccountsSetup.SelfActor(account), AuditActions.LocalAccountRecoveryCodeRedeem,
                    nameof(LocalAccount), account.Id.ToString(),
                    newValues: new { recoveryCodesLeft = await users.CountRecoveryCodesAsync(account) - 1 });
                if (!(await users.RedeemTwoFactorRecoveryCodeAsync(account, input.RecoveryCode)).Succeeded)
                {
                    db.ChangeTracker.Clear();
                    await attempts.RecordAsync(account.Id, address, SignInOutcome.WrongCode, ct);
                    return CodeInvalid;
                }
                usedRecoveryCode = true;
            }
            else if (!string.IsNullOrWhiteSpace(input.Code))
            {
                if (!await AcceptCodeAsync(account, input.Code, ct))
                {
                    await attempts.RecordAsync(account.Id, address, SignInOutcome.WrongCode, ct);
                    return CodeInvalid;
                }
            }
            else
                return new LocalSignInOutcome.CodeRequired();
        }

        return await FinishAsync(account, address, input.EnrolmentCode, usedRecoveryCode, ct);
    }

    /// <summary>The credentials are proven; what remains is the policy and any required enrolment.</summary>
    internal async Task<LocalSignInOutcome> FinishAsync(
        LocalAccount account, string address, string? enrolmentCode, bool usedRecoveryCode, CancellationToken ct)
    {
        if (!await policy.IsPermittedAsync(account.Id, ct))
        {
            await attempts.RecordAsync(account.Id, address, SignInOutcome.NotPermitted, ct);
            return new LocalSignInOutcome.Failed(ErrorCodes.LocalSignInNotAllowed);
        }

        IReadOnlyList<string>? recoveryCodes = null;
        if (!account.TwoFactorEnabled && (await settings.GetAsync(ct)).RequireAuthenticator)
        {
            if (string.IsNullOrWhiteSpace(enrolmentCode))
            {
                var setup = await self.PendingAuthenticatorAsync(account, fresh: false, ct);
                return new LocalSignInOutcome.EnrolmentRequired(setup.Secret, setup.OtpauthUri);
            }
            recoveryCodes = await self.EnrolAsync(account, enrolmentCode, ct);
            if (recoveryCodes is null)
            {
                await attempts.RecordAsync(account.Id, address, SignInOutcome.WrongCode, ct);
                return CodeInvalid;
            }
        }

        await attempts.RecordAsync(account.Id, address,
            usedRecoveryCode ? SignInOutcome.RecoveryCodeUsed : SignInOutcome.Succeeded, ct);
        return new LocalSignInOutcome.SignedIn(account, recoveryCodes);
    }

    private async Task<LocalSignInOutcome> RefuseAsync(
        LocalAccount? account, string password, string address, SignInOutcome outcome, CancellationToken ct)
    {
        users.PasswordHasher.VerifyHashedPassword(
            account ?? new LocalAccount(), LocalAccountsSetup.DummyPasswordHash.Value, password);
        await attempts.RecordAsync(account?.Id, address, outcome, ct);
        return SignInFailed;
    }

    // The step guard is a conditional write, so two requests racing with one code cannot both pass.
    private async Task<bool> AcceptCodeAsync(LocalAccount account, string code, CancellationToken ct)
    {
        var key = await users.GetAuthenticatorKeyAsync(account);
        if (string.IsNullOrEmpty(key) || !Totp.Verify(key, code, time.GetUtcNow(), out var step)) return false;
        if (account.LastTotpStep >= step) return false;

        var accepted = await db.LocalAccounts
            .Where(a => a.Id == account.Id && (a.LastTotpStep == null || a.LastTotpStep < step))
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.LastTotpStep, (long?)step), ct);
        if (accepted != 1) return false;

        var entry = db.Entry(account).Property(a => a.LastTotpStep);
        entry.CurrentValue = entry.OriginalValue = step;
        entry.IsModified = false;
        return true;
    }
}
