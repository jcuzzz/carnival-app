using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity.LocalAccounts;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The local-account HTTP surface on a school deployment with no identity provider (ADR-0065):
/// sign-in steps, the session cookie and its checks on every request, My account, People and role
/// grants by username.
/// </summary>
public sealed class LocalAccountsHttpTests : IDisposable
{
    private const string Password = LocalAccountsHostFactory.Password;
    private const string NewPassword = "a lantern above the harbour wall";

    private readonly LocalAccountsHostFactory _host = new();

    public void Dispose() => _host.Dispose();

    private static object Credentials(string username, string password = Password, string? code = null,
        string? recoveryCode = null, string? enrolmentCode = null) =>
        new { username, password, code, recoveryCode, enrolmentCode };

    private async Task<HttpResponseMessage> SignInAsync(object body) =>
        await _host.Session().PostAsync("/api/v1/auth/local", body);

    private string CodeAt(string secret, int stepsFromNow = 0) =>
        LocalAccountsHarness.CodeFor(secret, _host.Time.Now + TimeSpan.FromSeconds(30 * stepsFromNow));

    private async Task<(string Secret, IReadOnlyList<string> RecoveryCodes)> EnrolAsync(LocalSession session)
    {
        var started = await session.PostAsync("/api/v1/account/authenticator", new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var secret = (await started.JsonAsync()).GetProperty("secret").GetString()!;
        var confirmed = await session.PostAsync("/api/v1/account/authenticator/confirm", new { code = CodeAt(secret) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var codes = (await confirmed.JsonAsync()).GetProperty("recoveryCodes").EnumerateArray()
            .Select(c => c.GetString()!).ToList();
        return (secret, codes);
    }

    [Fact]
    public async Task The_first_administrator_link_signs_in_a_local_system_administrator_with_the_session_cookie()
    {
        var (_, output) = await _host.RunAccountsCommandAsync("first-admin");
        var token = LocalAccountsHostFactory.TokenFrom(output);
        var session = _host.Session();

        var inspected = await (await session.PostAsync("/api/v1/auth/setup/inspect", new { token })).JsonAsync();
        Assert.Equal("firstAdministrator", inspected.GetProperty("kind").GetString());
        Assert.False(inspected.GetProperty("requireAuthenticator").GetBoolean());

        var completed = await session.PostAsync("/api/v1/auth/setup/complete",
            new { token, username = "backup-admin", password = Password });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var body = await completed.JsonAsync();
        Assert.Equal("signedIn", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("me").GetProperty("localAccount").GetBoolean());
        Assert.Contains("SystemAdministrator",
            body.GetProperty("me").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        var cookie = Assert.Single(session.SetCookies).ToLowerInvariant();
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("expires=", cookie);

        var me = await (await session.GetAsync("/api/v1/me")).JsonAsync();
        Assert.StartsWith(LocalAccount.SubjectPrefix, me.GetProperty("subject").GetString());
        Assert.True(me.GetProperty("localAccount").GetBoolean());

        var reused = await _host.Session().PostAsync("/api/v1/auth/setup/complete",
            new { token, username = "second-admin", password = Password });
        Assert.Equal(HttpStatusCode.Gone, reused.StatusCode);
        await reused.AssertErrorAsync(ErrorCodes.SetupLinkInvalid);
    }

    [Fact]
    public async Task Methods_and_identify_answer_password_with_no_provider()
    {
        var session = _host.Session();
        var methods = await (await session.GetAsync("/api/v1/auth/methods")).JsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, methods.GetProperty("provider").ValueKind);

        var identified = await (await session.PostAsync("/api/v1/auth/identify",
            new { username = "someone@school.test", returnUrl = "/deck" })).JsonAsync();
        Assert.Equal("password", identified.GetProperty("method").GetString());
        Assert.False(identified.TryGetProperty("loginUrl", out _));

        var blank = await session.PostAsync("/api/v1/auth/identify", new { username = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        await blank.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Unknown_wrong_disabled_awaiting_setup_and_locked_all_get_the_same_answer()
    {
        var admin = await _host.FirstAdministratorAsync();
        await _host.ActiveAccountAsync(admin, "helper-wrong");
        var (disabledId, _) = await _host.ActiveAccountAsync(admin, "helper-disabled");
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync($"/api/v1/admin/local-accounts/{disabledId}/disable")).StatusCode);
        await LocalAccountsHostFactory.CreateAccountAsync(admin, "helper-pending");
        await _host.ActiveAccountAsync(admin, "helper-locked");
        for (var i = 0; i < SignInAttemptLog.FailureThreshold; i++)
            await SignInAsync(Credentials("helper-locked", "not the password at all"));

        HttpResponseMessage[] answers =
        [
            await SignInAsync(Credentials("nobody-here")),
            await SignInAsync(Credentials("helper-wrong", "not the password at all")),
            await SignInAsync(Credentials("helper-disabled")),
            await SignInAsync(Credentials("helper-pending")),
            await SignInAsync(Credentials("helper-locked")),
        ];

        var bodies = new List<string>();
        foreach (var answer in answers)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
            Assert.False(answer.Headers.Contains("Set-Cookie"));
            bodies.Add(await answer.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
        await answers[0].AssertErrorAsync(ErrorCodes.SignInFailed);
    }

    [Fact]
    public async Task A_missing_password_is_a_validation_failure()
    {
        var response = await SignInAsync(new { username = "someone" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task An_enrolled_account_is_asked_for_a_code_and_a_recovery_code_works_once()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (_, helper) = await _host.ActiveAccountAsync(admin, "helper1");
        var (secret, recoveryCodes) = await EnrolAsync(helper);
        Assert.Equal(LocalAccountSelfService.RecoveryCodeCount, recoveryCodes.Count);

        var asked = await SignInAsync(Credentials("helper1"));
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal("codeRequired", (await asked.JsonAsync()).GetProperty("status").GetString());
        Assert.False(asked.Headers.Contains("Set-Cookie"));

        var stale = await SignInAsync(Credentials("helper1", code: CodeAt(secret, -5)));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        await stale.AssertErrorAsync(ErrorCodes.CodeInvalid);

        _host.Time.Now += TimeSpan.FromSeconds(30);
        var withCode = await SignInAsync(Credentials("helper1", code: CodeAt(secret)));
        Assert.Equal(HttpStatusCode.OK, withCode.StatusCode);
        Assert.Equal("signedIn", (await withCode.JsonAsync()).GetProperty("status").GetString());

        var replayed = await SignInAsync(Credentials("helper1", code: CodeAt(secret)));
        await replayed.AssertErrorAsync(ErrorCodes.CodeInvalid);

        var recovered = await SignInAsync(Credentials("helper1", recoveryCode: recoveryCodes[0]));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var again = await SignInAsync(Credentials("helper1", recoveryCode: recoveryCodes[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.CodeInvalid);

        var mine = await (await helper.GetAsync("/api/v1/account")).JsonAsync();
        Assert.True(mine.GetProperty("authenticatorEnrolled").GetBoolean());
        Assert.Equal(LocalAccountSelfService.RecoveryCodeCount - 1, mine.GetProperty("recoveryCodesLeft").GetInt32());
    }

    [Fact]
    public async Task A_required_authenticator_is_enrolled_at_the_next_sign_in_and_returns_recovery_codes()
    {
        var admin = await _host.FirstAdministratorAsync();
        await _host.ActiveAccountAsync(admin, "helper1");
        await LocalAccountsHostFactory.SetSignInSettingsAsync(admin, requireAuthenticator: true);

        var asked = await SignInAsync(Credentials("helper1"));
        var enrolment = await asked.JsonAsync();
        Assert.Equal("enrolmentRequired", enrolment.GetProperty("status").GetString());
        Assert.StartsWith("otpauth://totp/", enrolment.GetProperty("otpauthUri").GetString());
        var secret = enrolment.GetProperty("secret").GetString()!;

        var wrong = await SignInAsync(Credentials("helper1", enrolmentCode: CodeAt(secret, -5)));
        await wrong.AssertErrorAsync(ErrorCodes.CodeInvalid);

        var session = _host.Session();
        var enrolled = await session.PostAsync("/api/v1/auth/local", Credentials("helper1", enrolmentCode: CodeAt(secret)));
        var body = await enrolled.JsonAsync();
        Assert.Equal("signedIn", body.GetProperty("status").GetString());
        Assert.Equal(LocalAccountSelfService.RecoveryCodeCount, body.GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_clears_the_cookie_and_redirects_to_the_app()
    {
        var admin = await _host.FirstAdministratorAsync();

        var response = await admin.GetAsync("/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Null(admin.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_reset_ends_every_session_of_the_account_at_its_next_request_and_the_admin_carries_on()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (id, first) = await _host.ActiveAccountAsync(admin, "helper1");
        var second = await _host.SignInAsync("helper1");

        var reset = await admin.PostAsync($"/api/v1/admin/local-accounts/{id}/reset");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(string.IsNullOrEmpty((await reset.JsonAsync()).GetProperty("setupToken").GetString()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/me")).StatusCode);
        Assert.Null(first.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
        await (await SignInAsync(Credentials("helper1"))).AssertErrorAsync(ErrorCodes.SignInFailed);
    }

    [Fact]
    public async Task Disabling_ends_the_accounts_sessions_and_enabling_lets_it_sign_in_again()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (id, helper) = await _host.ActiveAccountAsync(admin, "helper1");

        var disabled = await admin.PostAsync($"/api/v1/admin/local-accounts/{id}/disable");
        Assert.Equal("Disabled", (await disabled.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await helper.GetAsync("/api/v1/me")).StatusCode);

        var enabled = await admin.PostAsync($"/api/v1/admin/local-accounts/{id}/enable");
        Assert.Equal("Active", (await enabled.JsonAsync()).GetProperty("status").GetString());
        await _host.SignInAsync("helper1");
    }

    [Fact]
    public async Task A_password_change_keeps_the_callers_session_and_its_expiry_and_ends_the_others()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (_, caller) = await _host.ActiveAccountAsync(admin, "helper1");
        var other = await _host.SignInAsync("helper1");
        var before = Ticket(caller.Cookie!);
        _host.Time.Now += TimeSpan.FromHours(1);

        var wrong = await caller.PostAsync("/api/v1/account/password",
            new { currentPassword = "not the password at all", newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        await wrong.AssertErrorAsync(ErrorCodes.CurrentPasswordWrong);

        var weak = await caller.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = "short" });
        await weak.AssertErrorAsync(ErrorCodes.PasswordTooShort);

        var changed = await caller.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var after = Ticket(caller.Cookie!);
        Assert.Equal(before.IssuedUtc, after.IssuedUtc);
        Assert.Equal(before.ExpiresUtc, after.ExpiresUtc);
        Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/me")).StatusCode);

        await (await SignInAsync(Credentials("helper1"))).AssertErrorAsync(ErrorCodes.SignInFailed);
        await _host.SignInAsync("helper1", NewPassword);
    }

    [Fact]
    public async Task Enrolling_an_authenticator_keeps_the_callers_session_and_ends_the_others()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (_, caller) = await _host.ActiveAccountAsync(admin, "helper1");
        var other = await _host.SignInAsync("helper1");

        var unproven = await caller.PostAsync("/api/v1/account/authenticator", new { currentPassword = "nope" });
        await unproven.AssertErrorAsync(ErrorCodes.CurrentPasswordWrong);

        await EnrolAsync(caller);

        Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync("/api/v1/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/me")).StatusCode);
        var twice = await caller.PostAsync("/api/v1/account/authenticator", new { currentPassword = Password });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        await twice.AssertErrorAsync(ErrorCodes.AuthenticatorAlreadyEnrolled);
    }

    [Fact]
    public async Task People_is_for_system_administrators_and_refuses_changes_to_ones_own_account()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (id, helper) = await _host.ActiveAccountAsync(admin, "helper1");

        Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync("/api/v1/admin/local-accounts")).StatusCode);

        var list = await (await admin.GetAsync("/api/v1/admin/local-accounts")).JsonAsync();
        var names = list.EnumerateArray().Select(a => a.GetProperty("username").GetString()).ToList();
        Assert.Equal(["backup-admin", "helper1"], names);
        var adminId = list.EnumerateArray().First().GetProperty("id").GetGuid();

        var self = await admin.PostAsync($"/api/v1/admin/local-accounts/{adminId}/disable");
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        await self.AssertErrorAsync(ErrorCodes.AccountSelfChange);

        var missing = await admin.PostAsync($"/api/v1/admin/local-accounts/{Guid.NewGuid()}/clear-lockout");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await missing.AssertErrorAsync(ErrorCodes.AccountNotFound);

        var taken = await admin.PostAsync("/api/v1/admin/local-accounts", new { username = "HELPER1", displayName = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        await taken.AssertErrorAsync(ErrorCodes.AccountUsernameTaken);

        var invalid = await admin.PostAsync("/api/v1/admin/local-accounts", new { username = "a@b.c", displayName = "X" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        await invalid.AssertErrorAsync(ErrorCodes.AccountUsernameInvalid);

        var cleared = await admin.PostAsync($"/api/v1/admin/local-accounts/{id}/clear-lockout");
        Assert.False((await cleared.JsonAsync()).GetProperty("lockedOut").GetBoolean());
    }

    [Fact]
    public async Task A_grant_to_a_username_binds_at_once_and_applies_at_the_next_request()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (_, helper) = await _host.ActiveAccountAsync(admin, "helper1");
        Assert.Empty((await (await helper.GetAsync("/api/v1/me")).JsonAsync()).GetProperty("roles").EnumerateArray());

        var granted = await admin.PostAsync("/api/v1/admin/role-assignments",
            new { upn = "Helper1", role = "Recorder", reason = "Deck on the day" });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var row = await granted.JsonAsync();
        Assert.Equal("helper1", row.GetProperty("upn").GetString());
        Assert.True(row.GetProperty("localAccount").GetBoolean());
        Assert.True(row.GetProperty("boundToSubject").GetBoolean());

        var roles = (await (await helper.GetAsync("/api/v1/me")).JsonAsync()).GetProperty("roles");
        Assert.Equal(["Recorder"], roles.EnumerateArray().Select(r => r.GetString()));

        var unknown = await admin.PostAsync("/api/v1/admin/role-assignments",
            new { upn = "nobody-here", role = "Recorder", reason = "Deck" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        await unknown.AssertErrorAsync(ErrorCodes.RoleLocalAccountUnknown);
    }

    [Fact]
    public async Task A_grant_written_by_another_process_reaches_a_local_session_without_a_restart()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (id, helper) = await _host.ActiveAccountAsync(admin, "helper1");
        // Both warm the in-process cache.
        await admin.GetAsync("/api/v1/me");
        Assert.Empty((await (await helper.GetAsync("/api/v1/me")).JsonAsync()).GetProperty("roles").EnumerateArray());

        await _host.DbAsync(async db =>
        {
            db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(),
                SubjectId = LocalAccount.SubjectFor(id),
                SubjectUpn = "helper1",
                Role = PlatformRole.SystemAdministrator,
                Reason = "Host recovery (accounts setup-link)",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = AccountsCommand.HostSubject,
            });
            return await db.SaveChangesAsync();
        });

        var roles = (await (await helper.GetAsync("/api/v1/me")).JsonAsync()).GetProperty("roles");
        Assert.Equal(["SystemAdministrator"], roles.EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task A_bootstrap_entry_is_refused_as_a_username_and_never_applies_to_a_local_session()
    {
        _host.With("Identity:BootstrapAdministrators", "boss");
        var admin = await _host.FirstAdministratorAsync();

        var reserved = await admin.PostAsync("/api/v1/admin/local-accounts", new { username = "Boss", displayName = "Boss" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reserved.StatusCode);
        await reserved.AssertErrorAsync(ErrorCodes.AccountUsernameReserved);

        var (id, helper) = await _host.ActiveAccountAsync(admin, "helper1");
        _host.Services.GetRequiredService<IConfiguration>()["Identity:BootstrapAdministrators"] =
            LocalAccount.SubjectFor(id);
        Assert.Empty((await (await helper.GetAsync("/api/v1/me")).JsonAsync()).GetProperty("roles").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync("/api/v1/admin/local-accounts")).StatusCode);
    }

    [Fact]
    public async Task The_sign_in_limiter_answers_429_on_every_password_or_code_check_once_full()
    {
        var admin = await _host.FirstAdministratorAsync();
        _host.Gate.Close();
        try
        {
            var holders = Enumerable.Range(0, RateLimiting.SignInPermits)
                .Select(i => _host.Session().PostAsync("/api/v1/auth/local", Credentials($"nobody-{i}")))
                .ToList();
            while (_host.Gate.Held < RateLimiting.SignInPermits) await Task.Delay(20);
            // The queue is full once the first request past it is refused.
            var queued = Enumerable.Range(0, RateLimiting.SignInQueue + 1)
                .Select(i => _host.Session().PostAsync("/api/v1/auth/local", Credentials($"queued-{i}")))
                .ToList();
            var overflow = await await Task.WhenAny(queued);
            Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
            await overflow.AssertErrorAsync(ErrorCodes.TooManyRequests);
            Assert.True(overflow.Headers.Contains("Retry-After"));

            HttpResponseMessage[] refused =
            [
                await SignInAsync(Credentials("backup-admin")),
                await _host.Session().PostAsync("/api/v1/auth/setup/complete", new { token = "x", password = Password }),
                await admin.PostAsync("/api/v1/account/password", new { currentPassword = Password, newPassword = NewPassword }),
                await admin.PostAsync("/api/v1/account/authenticator", new { currentPassword = Password }),
                await admin.PostAsync("/api/v1/account/authenticator/confirm", new { code = "123456" }),
            ];
            Assert.All(refused, r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));

            _host.Gate.Open();
            Assert.All(await Task.WhenAll(holders), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
            await Task.WhenAll(queued);
        }
        finally
        {
            _host.Gate.Open();
        }
    }

    [Fact]
    public async Task Signing_out_ends_the_session_on_the_server_and_leaves_the_accounts_other_devices()
    {
        var admin = await _host.FirstAdministratorAsync();
        var otherDevice = await _host.SignInAsync("backup-admin");
        var copied = admin.Cookie;

        Assert.Equal(HttpStatusCode.Found, (await admin.GetAsync("/api/v1/auth/logout")).StatusCode);

        var replay = _host.Session();
        replay.Cookie = copied;
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(1, await _host.DbAsync(db => db.EndedLocalSessions.CountAsync()));
    }

    [Fact]
    public async Task A_refreshed_cookie_keeps_its_session_id_so_signing_out_still_ends_it()
    {
        var admin = await _host.FirstAdministratorAsync();
        var before = Ticket(admin.Cookie!, out var sessionId);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/v1/account/password",
            new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        Ticket(admin.Cookie!, out var refreshedId);
        Assert.Equal(sessionId, refreshedId);
        Assert.NotNull(before.ExpiresUtc);

        var copied = admin.Cookie;
        await admin.GetAsync("/api/v1/auth/logout");
        var replay = _host.Session();
        replay.Cookie = copied;
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_local_session_is_never_renewed_however_busy()
    {
        var admin = await _host.FirstAdministratorAsync();
        admin.SetCookies.Clear();
        var issued = _host.Time.Now;

        foreach (var hours in new[] { 0, 4, 7, 11 })
        {
            _host.Time.Now = issued + TimeSpan.FromHours(hours);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/admin/local-accounts")).StatusCode);
        }
        Assert.Empty(admin.SetCookies);

        _host.Time.Now = issued + TimeSpan.FromHours(12.5);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_database_fault_in_the_session_check_is_a_500_and_keeps_the_cookie()
    {
        var admin = await _host.FirstAdministratorAsync();
        var cookie = admin.Cookie;
        await _host.DbAsync(db => db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE EndedLocalSessions RENAME TO EndedLocalSessionsAway"));

        var fault = await admin.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.InternalServerError, fault.StatusCode);
        Assert.False(fault.Headers.Contains("Set-Cookie"));
        Assert.Equal(cookie, admin.Cookie);
        await _host.DbAsync(db => db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE EndedLocalSessionsAway RENAME TO EndedLocalSessions"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Confirming_an_authenticator_counts_wrong_codes_and_honours_the_lockout()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (id, helper) = await _host.ActiveAccountAsync(admin, "helper1");
        var started = await helper.PostAsync("/api/v1/account/authenticator", new { currentPassword = Password });
        var secret = (await started.JsonAsync()).GetProperty("secret").GetString()!;

        var wrong = await helper.PostAsync("/api/v1/account/authenticator/confirm", new { code = CodeAt(secret, -5) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        await wrong.AssertErrorAsync(ErrorCodes.CodeInvalid);
        Assert.Equal(1, await _host.DbAsync(db =>
            db.SignInAttempts.CountAsync(a => a.LocalAccountId == id && a.Outcome == SignInOutcome.WrongCode)));

        for (var i = 1; i < SignInAttemptLog.FailureThreshold; i++)
            await helper.PostAsync("/api/v1/account/authenticator/confirm", new { code = CodeAt(secret, -5) });
        var locked = await helper.PostAsync("/api/v1/account/authenticator/confirm", new { code = CodeAt(secret) });
        await locked.AssertErrorAsync(ErrorCodes.CodeInvalid);
        Assert.True(await _host.DbAsync(db =>
            db.SignInAttempts.AnyAsync(a => a.LocalAccountId == id && a.Outcome == SignInOutcome.LockedOut)));
    }

    [Fact]
    public async Task A_hash_upgrade_while_starting_an_authenticator_keeps_the_caller_signed_in()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (id, caller) = await _host.ActiveAccountAsync(admin, "helper1");
        var other = await _host.SignInAsync("helper1");
        var weak = new Microsoft.AspNetCore.Identity.PasswordHasher<LocalAccount>(
            Options.Create(new Microsoft.AspNetCore.Identity.PasswordHasherOptions { IterationCount = 500 }))
            .HashPassword(new LocalAccount(), Password);
        await _host.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.PasswordHash, weak)));

        var started = await caller.PostAsync("/api/v1/account/authenticator", new { currentPassword = Password });

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.NotEqual(weak, await _host.DbAsync(db => db.LocalAccounts.Where(a => a.Id == id)
            .Select(a => a.PasswordHash).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await caller.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/me")).StatusCode);
    }

    private Microsoft.AspNetCore.Authentication.AuthenticationProperties Ticket(string cookie) =>
        Ticket(cookie, out _);

    private Microsoft.AspNetCore.Authentication.AuthenticationProperties Ticket(string cookie, out string? sessionId)
    {
        var ticket = _host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme)
            .TicketDataFormat.Unprotect(cookie)!;
        sessionId = ticket.Principal.FindFirst(CarnivalApp.Api.Identity.PlatformClaims.LocalSessionId)?.Value;
        return ticket.Properties;
    }
}
