using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity.LocalAccounts;
using CarnivalApp.Api.School;
using Xunit;
using static CarnivalApp.Api.Tests.LocalAccountsHarness;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// <see cref="LocalSignInService"/>: the steps, the one answer for every refusal, lockout per
/// account and address, the authenticator and recovery codes, and the work factor.
/// </summary>
public sealed class LocalSignInTests
{
    private const string Wrong = "not the password at all";

    private static async Task FailTimesAsync(LocalAccountsHarness h, string username, int times, System.Net.IPAddress? from = null)
    {
        for (var i = 0; i < times; i++)
            AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync(username, Wrong, from));
    }

    private static Task<List<SignInAttempt>> AttemptsAsync(LocalAccountsHarness h) =>
        h.DbAsync(db => db.SignInAttempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync());

    private static async Task SetSchoolAsync(LocalAccountsHarness h, Action<SchoolConfiguration> change)
    {
        await h.WithAsync(async services =>
        {
            var db = services.GetRequiredService<Data.CarnivalAppDbContext>();
            change(await db.SchoolConfigurations.SingleAsync());
            await db.SaveChangesAsync();
        });
        h.Services.GetRequiredService<SchoolSettingsProvider>().Invalidate();
    }

    private static Task RequireAuthenticatorAsync(LocalAccountsHarness h) =>
        SetSchoolAsync(h, row => row.SettingsJson = SchoolSettings.SerialiseSignIn(true, LocalAccountAccess.SystemAdministrators));

    /// <summary>Enrols through a required-authenticator sign-in; returns the secret and the codes.</summary>
    private static async Task<(string Secret, IReadOnlyList<string> Codes)> EnrolAsync(LocalAccountsHarness h, string username)
    {
        await RequireAuthenticatorAsync(h);
        var required = Assert.IsType<LocalSignInOutcome.EnrolmentRequired>(await h.SignInAsync(username));
        var signedIn = Assert.IsType<LocalSignInOutcome.SignedIn>(
            await h.SignInAsync(username, enrolmentCode: CodeFor(required.Secret, h.Time.Now)));
        return (required.Secret, signedIn.RecoveryCodes!);
    }

    [Fact]
    public async Task The_right_password_signs_in_and_records_a_success()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");

        var signedIn = Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync(" JSmith "));

        Assert.Equal(id, signedIn.Account.Id);
        Assert.Null(signedIn.RecoveryCodes);
        Assert.Equal(SignInOutcome.Succeeded, (await AttemptsAsync(h))[^1].Outcome);
    }

    [Fact]
    public async Task Unknown_wrong_disabled_and_awaiting_setup_all_get_the_same_answer()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");
        var disabled = await h.ActiveAccountAsync("gone");
        await h.WithAsync(s => s.GetRequiredService<LocalAccountAdministration>().DisableAsync(h.Admin, disabled));
        await h.CreateAccountAsync("pending");

        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("nobody"));
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith", Wrong));
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("gone"));
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("pending", StrongPassword));
        AssertFailed(ErrorCodes.ValidationFailed, await h.SignInAsync("", StrongPassword));
    }

    [Fact]
    public async Task An_unknown_name_is_recorded_with_no_account_and_the_typed_text_is_never_stored()
    {
        await using var h = await CreateAsync();
        const string typed = "copper-kettle-typed-as-a-name";

        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync(typed, StrongPassword));

        var attempt = Assert.Single(await AttemptsAsync(h));
        Assert.Null(attempt.LocalAccountId);
        Assert.Equal("10.0.0.10", attempt.ClientAddress);
        Assert.Equal(SignInOutcome.WrongPassword, attempt.Outcome);
        Assert.Empty(await h.DbAsync(db => db.AuditEvents.Where(e => e.NewValuesJson!.Contains(typed)).ToListAsync()));
    }

    [Fact]
    public async Task Ten_failures_lock_the_account_at_that_address_only()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");

        await FailTimesAsync(h, "jsmith", 9);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));

        await FailTimesAsync(h, "jsmith", 10);
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith"));
        Assert.Equal(SignInOutcome.LockedOut, (await AttemptsAsync(h))[^1].Outcome);

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith", from: Elsewhere));
        Assert.True(await h.WithAsync(s => s.GetRequiredService<SignInAttemptLog>().IsLockedOutAnywhereAsync(id)));
    }

    [Fact]
    public async Task A_success_resets_the_count_for_its_address()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");

        await FailTimesAsync(h, "jsmith", 9);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
        await FailTimesAsync(h, "jsmith", 9);

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
    }

    [Fact]
    public async Task The_lockout_ends_15_minutes_after_the_tenth_failure_and_attempts_while_locked_do_not_extend_it()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");
        await FailTimesAsync(h, "jsmith", 10);
        var tenth = h.Time.Now;

        h.Time.Now = tenth.AddMinutes(10);
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith"));
        h.Time.Now = tenth + SignInAttemptLog.LockoutWindow - TimeSpan.FromSeconds(1);
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith"));

        h.Time.Now = tenth + SignInAttemptLog.LockoutWindow;
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
    }

    [Fact]
    public async Task Failures_spread_over_more_than_15_minutes_do_not_lock()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");
        await FailTimesAsync(h, "jsmith", 5);
        h.Time.Now += TimeSpan.FromMinutes(16);
        await FailTimesAsync(h, "jsmith", 9);

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
    }

    [Fact]
    public async Task An_administrator_clearing_the_lockout_unlocks_every_address()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        await FailTimesAsync(h, "jsmith", 10);
        await FailTimesAsync(h, "jsmith", 10, Elsewhere);

        var cleared = await h.WithAsync(s => s.GetRequiredService<LocalAccountAdministration>()
            .ClearLockoutAsync(h.Admin, id, Office));
        Assert.False(cleared.Value!.LockedOut);

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith", from: Elsewhere));
    }

    [Fact]
    public async Task Beside_a_provider_only_system_administrators_sign_in_locally_unless_the_school_opens_it()
    {
        await using var h = await CreateAsync(new Dictionary<string, string?>
        {
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000002",
        });
        var issued = await h.CreateAccountAsync("helper1");
        AssertFailed(ErrorCodes.LocalSignInNotAllowed, await h.CompleteAsync(issued.SetupToken));

        AssertFailed(ErrorCodes.LocalSignInNotAllowed, await h.SignInAsync("helper1"));
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("helper1", Wrong));
        Assert.Equal(SignInOutcome.NotPermitted, (await AttemptsAsync(h))[^2].Outcome);

        await SetSchoolAsync(h, row => row.SettingsJson = SchoolSettings.SerialiseSignIn(false, LocalAccountAccess.Everyone));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("helper1"));
    }

    [Fact]
    public async Task Requiring_an_authenticator_enrols_at_the_next_sign_in_and_returns_ten_recovery_codes()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        await RequireAuthenticatorAsync(h);

        var first = Assert.IsType<LocalSignInOutcome.EnrolmentRequired>(await h.SignInAsync("jsmith"));
        var again = Assert.IsType<LocalSignInOutcome.EnrolmentRequired>(await h.SignInAsync("jsmith"));
        Assert.Equal(first.Secret, again.Secret);
        Assert.StartsWith("otpauth://totp/Test%20Synthetic%20College:jsmith?secret=" + first.Secret, first.OtpauthUri);

        AssertFailed(ErrorCodes.CodeInvalid, await h.SignInAsync("jsmith", enrolmentCode: "000000"));
        var signedIn = Assert.IsType<LocalSignInOutcome.SignedIn>(
            await h.SignInAsync("jsmith", enrolmentCode: CodeFor(first.Secret, h.Time.Now)));
        Assert.Equal(LocalAccountSelfService.RecoveryCodeCount, signedIn.RecoveryCodes!.Count);
        Assert.True(await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id).Select(a => a.TwoFactorEnabled).SingleAsync()));

        Assert.IsType<LocalSignInOutcome.CodeRequired>(await h.SignInAsync("jsmith"));
    }

    [Fact]
    public async Task An_authenticator_code_is_accepted_once_and_never_replayed()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");
        var (secret, _) = await EnrolAsync(h, "jsmith");

        // The enrolment code's own step is spent.
        AssertFailed(ErrorCodes.CodeInvalid, await h.SignInAsync("jsmith", code: CodeFor(secret, h.Time.Now)));

        h.Time.Now += Totp.Step;
        var code = CodeFor(secret, h.Time.Now);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith", code: code));
        AssertFailed(ErrorCodes.CodeInvalid, await h.SignInAsync("jsmith", code: code, from: Elsewhere));

        // An older step inside the window is a replay too.
        AssertFailed(ErrorCodes.CodeInvalid,
            await h.SignInAsync("jsmith", code: CodeFor(secret, h.Time.Now - Totp.Step)));
    }

    [Fact]
    public async Task Wrong_codes_count_towards_the_lockout()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");
        var (secret, _) = await EnrolAsync(h, "jsmith");

        for (var i = 0; i < SignInAttemptLog.FailureThreshold; i++)
            AssertFailed(ErrorCodes.CodeInvalid, await h.SignInAsync("jsmith", code: "000000"));

        h.Time.Now += Totp.Step;
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith", code: CodeFor(secret, h.Time.Now)));
    }

    [Fact]
    public async Task Recovery_codes_are_stored_hashed_and_each_works_once()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var (_, codes) = await EnrolAsync(h, "jsmith");

        var stored = await h.DbAsync(db => db.UserTokens.AsNoTracking()
            .Where(t => t.UserId == id && t.Name == "RecoveryCodes").Select(t => t.Value!).SingleAsync());
        var hashes = stored.Split(';');
        Assert.Equal(codes.Count, hashes.Length);
        Assert.All(codes, code => Assert.DoesNotContain(code, stored, StringComparison.OrdinalIgnoreCase));
        Assert.All(hashes, hash => Assert.Matches("^[0-9a-f]{64}$", hash));
        Assert.Contains(LocalAccountStore.HashRecoveryCode(codes[0]), hashes);

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith", recoveryCode: codes[0].ToLowerInvariant()));
        Assert.Equal(SignInOutcome.RecoveryCodeUsed, (await AttemptsAsync(h))[^1].Outcome);
        AssertFailed(ErrorCodes.CodeInvalid, await h.SignInAsync("jsmith", recoveryCode: codes[0]));

        var left = await h.WithAsync(s => s.GetRequiredService<LocalAccountSelfService>().GetAsync(id));
        Assert.Equal(codes.Count - 1, left!.RecoveryCodesLeft);
        Assert.Single(await h.DbAsync(db => db.AuditEvents
            .Where(e => e.Action == Auditing.AuditActions.LocalAccountRecoveryCodeRedeem).ToListAsync()));
    }

    [Fact]
    public async Task Passwords_are_hashed_at_220k_iterations_and_an_older_hash_upgrades_at_sign_in()
    {
        await using var h = await CreateAsync(iterations: LocalAccountsSetup.PasswordHashIterations);
        var id = await h.ActiveAccountAsync("jsmith");

        Assert.Equal(220_000, await IterationsAsync(h, id));
        Assert.Equal(220_000, h.Services.GetRequiredService<IOptions<PasswordHasherOptions>>().Value.IterationCount);

        var older = new PasswordHasher<LocalAccount>(Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }))
            .HashPassword(new LocalAccount(), LocalAccountsHarness.StrongPassword);
        await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.PasswordHash, older)));
        Assert.Equal(100_000, await IterationsAsync(h, id));

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
        Assert.Equal(220_000, await IterationsAsync(h, id));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith"));
    }

    // Identity v3 format: 0x01, PRF, iteration count, salt length, salt, subkey; big-endian.
    private static async Task<int> IterationsAsync(LocalAccountsHarness h, Guid id)
    {
        var hash = Convert.FromBase64String(
            await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id).Select(a => a.PasswordHash!).SingleAsync()));
        Assert.Equal(0x01, hash[0]);
        Assert.Equal((uint)KeyDerivationPrfSha512, BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(1)));
        return (int)BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(5));
    }

    private const int KeyDerivationPrfSha512 = 2;

    [Fact]
    public async Task Changing_the_password_rotates_the_stamp_and_a_wrong_current_password_counts()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var before = await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id).Select(a => a.SecurityStamp).SingleAsync());

        var wrong = await h.WithAsync(s => s.GetRequiredService<LocalAccountSelfService>()
            .ChangePasswordAsync(id, Wrong, "a brand new river crossing", Office));
        Assert.Equal(ErrorCodes.CurrentPasswordWrong, wrong.ErrorCode);
        Assert.Equal(SignInOutcome.WrongPassword, (await AttemptsAsync(h))[^1].Outcome);

        var weak = await h.WithAsync(s => s.GetRequiredService<LocalAccountSelfService>()
            .ChangePasswordAsync(id, StrongPassword, "password1234567", Office));
        Assert.Equal(ErrorCodes.PasswordTooCommon, weak.ErrorCode);

        var changed = await h.WithAsync(s => s.GetRequiredService<LocalAccountSelfService>()
            .ChangePasswordAsync(id, StrongPassword, "a brand new river crossing", Office));
        Assert.True(changed.Succeeded);
        Assert.NotEqual(before, changed.Value!.SecurityStamp);

        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith"));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("jsmith", "a brand new river crossing"));
    }

    private static Task<LocalAccountResult<AuthenticatorSetup>> BeginAsync(
        LocalAccountsHarness h, Guid id, string password = StrongPassword) =>
        h.WithAsync(s => s.GetRequiredService<LocalAccountSelfService>().BeginAuthenticatorAsync(id, password, Office));

    [Fact]
    public async Task Self_service_enrolment_reproves_the_password_issues_a_fresh_key_each_time_and_refuses_a_second_enrolment()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");

        Assert.Equal(ErrorCodes.CurrentPasswordWrong, (await BeginAsync(h, id, Wrong)).ErrorCode);
        Assert.Equal(SignInOutcome.WrongPassword, (await AttemptsAsync(h))[^1].Outcome);

        var stale = await BeginAsync(h, id);
        var begun = await BeginAsync(h, id);
        Assert.NotEqual(stale.Value!.Secret, begun.Value!.Secret);
        var stampAfterBegin = await h.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id).Select(a => a.SecurityStamp).SingleAsync());

        var self = (Func<IServiceProvider, LocalAccountSelfService>)(s => s.GetRequiredService<LocalAccountSelfService>());
        Assert.Equal(ErrorCodes.CodeInvalid, (await h.WithAsync(s => self(s).ConfirmAuthenticatorAsync(id, "000000"))).ErrorCode);
        Assert.Equal(ErrorCodes.CodeInvalid, (await h.WithAsync(s => self(s)
            .ConfirmAuthenticatorAsync(id, CodeFor(stale.Value.Secret, h.Time.Now)))).ErrorCode);

        var confirmed = await h.WithAsync(s => self(s).ConfirmAuthenticatorAsync(id, CodeFor(begun.Value.Secret, h.Time.Now)));
        Assert.True(confirmed.Succeeded);
        Assert.Equal(10, confirmed.Value!.RecoveryCodes.Count);
        Assert.NotEqual(stampAfterBegin, confirmed.Value.Account.SecurityStamp);

        Assert.Equal(ErrorCodes.AuthenticatorAlreadyEnrolled, (await BeginAsync(h, id)).ErrorCode);
    }

    [Fact]
    public async Task Of_two_concurrent_sign_ins_with_one_authenticator_code_exactly_one_wins()
    {
        await using var h = await CreateAsync(iterations: LocalAccountsSetup.PasswordHashIterations);
        await h.ActiveAccountAsync("jsmith");
        var (secret, _) = await EnrolAsync(h, "jsmith");
        h.Time.Now += Totp.Step;
        var code = CodeFor(secret, h.Time.Now);

        var outcomes = await Task.WhenAll(
            Task.Run(() => h.SignInAsync("jsmith", code: code)),
            Task.Run(() => h.SignInAsync("jsmith", code: code, from: Elsewhere)));

        Assert.Single(outcomes.OfType<LocalSignInOutcome.SignedIn>());
        AssertFailed(ErrorCodes.CodeInvalid, Assert.Single(outcomes.OfType<LocalSignInOutcome.Failed>()));
    }

    [Fact]
    public async Task Of_two_concurrent_sign_ins_with_one_recovery_code_exactly_one_wins()
    {
        await using var h = await CreateAsync(iterations: LocalAccountsSetup.PasswordHashIterations);
        await h.ActiveAccountAsync("jsmith");
        var (_, codes) = await EnrolAsync(h, "jsmith");

        var outcomes = await Task.WhenAll(
            Task.Run(() => h.SignInAsync("jsmith", recoveryCode: codes[0])),
            Task.Run(() => h.SignInAsync("jsmith", recoveryCode: codes[0], from: Elsewhere)));

        Assert.Single(outcomes.OfType<LocalSignInOutcome.SignedIn>());
        AssertFailed(ErrorCodes.CodeInvalid, Assert.Single(outcomes.OfType<LocalSignInOutcome.Failed>()));
        Assert.Single(await h.DbAsync(db => db.AuditEvents
            .Where(e => e.Action == Auditing.AuditActions.LocalAccountRecoveryCodeRedeem).ToListAsync()));
    }

    [Fact]
    public async Task A_locked_pair_is_refused_even_with_the_right_password_and_a_valid_code_and_keeps_its_recovery_codes()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var (secret, codes) = await EnrolAsync(h, "jsmith");
        await FailTimesAsync(h, "jsmith", 10);
        h.Time.Now += Totp.Step;

        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith", code: CodeFor(secret, h.Time.Now)));
        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith", recoveryCode: codes[0]));

        var left = await h.WithAsync(s => s.GetRequiredService<LocalAccountSelfService>().GetAsync(id));
        Assert.Equal(codes.Count, left!.RecoveryCodesLeft);
        Assert.Empty(await h.DbAsync(db => db.AuditEvents
            .Where(e => e.Action == Auditing.AuditActions.LocalAccountRecoveryCodeRedeem).ToListAsync()));
    }

    [Fact]
    public async Task An_over_length_password_is_refused_before_it_is_normalised_or_hashed()
    {
        await using var h = await CreateAsync();
        var id = await h.ActiveAccountAsync("jsmith");
        var huge = new string('x', LocalPasswordValidator.MaximumInputLength + 1);
        var attemptsBefore = (await AttemptsAsync(h)).Count;

        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("jsmith", huge));
        Assert.Equal(attemptsBefore, (await AttemptsAsync(h)).Count);

        var issued = await h.CreateAccountAsync("pending");
        AssertFailed(ErrorCodes.PasswordTooLong, await h.CompleteAsync(issued.SetupToken, huge));
        var self = (Func<IServiceProvider, LocalAccountSelfService>)(s => s.GetRequiredService<LocalAccountSelfService>());
        Assert.Equal(ErrorCodes.CurrentPasswordWrong,
            (await h.WithAsync(s => self(s).ChangePasswordAsync(id, huge, "a brand new river crossing", Office))).ErrorCode);
        Assert.Equal(ErrorCodes.PasswordTooLong,
            (await h.WithAsync(s => self(s).ChangePasswordAsync(id, StrongPassword, huge, Office))).ErrorCode);
        Assert.Equal(ErrorCodes.CurrentPasswordWrong, (await BeginAsync(h, id, huge)).ErrorCode);
    }

    [Fact]
    public async Task NFKC_equivalent_passwords_are_the_same_password_at_set_and_at_sign_in()
    {
        const string fullWidth = "ｃｏｐｐｅｒ　ｋｅｔｔｌｅ　ｏｎ　ａ　ｑｕｉｅｔ　ｒｉｖｅｒ";
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("set-wide", fullWidth);
        await h.ActiveAccountAsync("set-plain");

        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("set-wide", StrongPassword));
        Assert.IsType<LocalSignInOutcome.SignedIn>(await h.SignInAsync("set-plain", fullWidth));
    }

    [Fact]
    public async Task Case_variants_of_a_username_share_one_lockout_count()
    {
        await using var h = await CreateAsync();
        await h.ActiveAccountAsync("jsmith");

        await FailTimesAsync(h, "JSmith", 5);
        await FailTimesAsync(h, " jsmith", 5);

        AssertFailed(ErrorCodes.SignInFailed, await h.SignInAsync("JSMITH"));
        Assert.Equal(SignInOutcome.LockedOut, (await AttemptsAsync(h))[^1].Outcome);
    }
}
