using Microsoft.Extensions.Configuration;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity.LocalAccounts;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The pure rules behind local accounts: the password policy, the username shape, and RFC 6238.
/// </summary>
public sealed class LocalAccountRulesTests
{
    private static readonly string?[] SchoolWords =
        ["Test Synthetic College", "BANKSIA", "Banksia", "MOUNTAIN ASH", "Mountain Ash", "jsmith", "Jo Smith"];

    [Theory]
    [InlineData("copper kettle on a quiet river", null)]
    [InlineData("fifteen chars!!", null)]
    [InlineData("fourteen chars", ErrorCodes.PasswordTooShort)]
    [InlineData("short", ErrorCodes.PasswordTooShort)]
    // Eight emoji are sixteen UTF-16 units but eight characters.
    [InlineData("🐢🐢🐢🐢🐢🐢🐢🐢", ErrorCodes.PasswordTooShort)]
    [InlineData("qwertyuiopasdfgh", ErrorCodes.PasswordTooCommon)]
    [InlineData("PASSWORDPASSWORD", ErrorCodes.PasswordTooCommon)]
    [InlineData("password1234567!", ErrorCodes.PasswordTooCommon)]
    [InlineData("iloveyou20262026", ErrorCodes.PasswordTooCommon)]
    // Full-width letters and digits normalise (NFKC) to "password1234567".
    [InlineData("ｐａｓｓｗｏｒｄ１２３４５６７", ErrorCodes.PasswordTooCommon)]
    [InlineData("zzzzzzzzzzzzzzzzzzzz", ErrorCodes.PasswordTooPredictable)]
    [InlineData("Test Synthetic College 2026!", ErrorCodes.PasswordTooPredictable)]
    [InlineData("banksia-mountain-ash-2026", ErrorCodes.PasswordTooPredictable)]
    [InlineData("jsmith smith jo 1999!!", ErrorCodes.PasswordTooPredictable)]
    [InlineData("8364917250193847", ErrorCodes.PasswordTooPredictable)]
    [InlineData("banksia copper kettle river", null)]
    [InlineData("ein ruhiger Fluss am Morgen", null)]
    public void The_password_policy(string password, string? expected) =>
        Assert.Equal(expected, LocalPasswordValidator.Check(password, SchoolWords));

    [Fact]
    public void A_password_over_256_characters_is_refused_and_256_is_allowed()
    {
        var words = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"w{i:D3}x"));
        Assert.Null(LocalPasswordValidator.Check(words[..256], SchoolWords));
        Assert.Equal(ErrorCodes.PasswordTooLong, LocalPasswordValidator.Check(words[..257], SchoolWords));
    }

    [Theory]
    [InlineData("jsmith", "jsmith", null)]
    [InlineData("  J.Smith ", "j.smith", null)]
    [InlineData("backup-admin", "backup-admin", null)]
    [InlineData("helper_1", "helper_1", null)]
    [InlineData("ab", "ab", null)]
    [InlineData("a", "a", ErrorCodes.AccountUsernameInvalid)]
    [InlineData("-admin", "-admin", ErrorCodes.AccountUsernameInvalid)]
    [InlineData("name@example.org", "name@example.org", ErrorCodes.AccountUsernameInvalid)]
    [InlineData("jo smith", "jo smith", ErrorCodes.AccountUsernameInvalid)]
    [InlineData("über", "über", ErrorCodes.AccountUsernameInvalid)]
    [InlineData("", "", ErrorCodes.AccountUsernameInvalid)]
    [InlineData("root-admin", "root-admin", ErrorCodes.AccountUsernameReserved)]
    [InlineData("Root-Admin", "root-admin", ErrorCodes.AccountUsernameReserved)]
    public void Usernames(string typed, string normalised, string? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:BootstrapAdministrators"] = "admin@example.org, root-admin",
            })
            .Build();

        Assert.Equal(expected, LocalUsernames.Validate(typed, configuration, out var actual));
        Assert.Equal(normalised, actual);
    }

    [Fact]
    public void Usernames_are_at_most_64_characters()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Null(LocalUsernames.Validate(new string('a', 64), configuration, out _));
        Assert.Equal(ErrorCodes.AccountUsernameInvalid, LocalUsernames.Validate(new string('a', 65), configuration, out _));
    }

    // RFC 6238 appendix B, SHA-1 key "12345678901234567890"; six digits are the last six of eight.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Totp_matches_the_RFC_6238_vectors(long unixSeconds, string expected)
    {
        const string rfcKey = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        Assert.Equal(expected, Totp.Code(Totp.DecodeBase32(rfcKey)!, Totp.StepAt(at)));
        Assert.True(Totp.Verify(rfcKey, expected, at, out var step));
        Assert.Equal(Totp.StepAt(at), step);
    }

    [Fact]
    public void Totp_accepts_one_step_either_side_and_reports_the_step_that_matched()
    {
        const string key = "JBSWY3DPEHPK3PXP";
        var now = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        var current = Totp.StepAt(now);
        var bytes = Totp.DecodeBase32(key)!;

        foreach (var drift in new[] { -1, 0, 1 })
        {
            Assert.True(Totp.Verify(key, Totp.Code(bytes, current + drift), now, out var matched));
            Assert.Equal(current + drift, matched);
        }
        Assert.False(Totp.Verify(key, Totp.Code(bytes, current - 2), now, out _));
        Assert.False(Totp.Verify(key, Totp.Code(bytes, current + 2), now, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Totp_refuses_anything_but_six_digits(string? code) =>
        Assert.False(Totp.Verify("JBSWY3DPEHPK3PXP", code, DateTimeOffset.UnixEpoch, out _));

    [Fact]
    public void Totp_ignores_spaces_typed_into_the_code()
    {
        const string key = "JBSWY3DPEHPK3PXP";
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var code = Totp.Code(Totp.DecodeBase32(key)!, Totp.StepAt(now));
        Assert.True(Totp.Verify(key, $"{code[..3]} {code[3..]}", now, out _));
    }

    [Fact]
    public void A_colon_or_spaces_in_the_school_name_cannot_break_the_otpauth_label()
    {
        var uri = Totp.OtpauthUri("St Mary's: Senior Campus", "backup-admin", "JBSWY3DPEHPK3PXP");

        var label = uri["otpauth://totp/".Length..uri.IndexOf('?')];
        Assert.Equal(1, label.Count(c => c == ':'));
        var (issuer, account) = (label.Split(':')[0], label.Split(':')[1]);
        Assert.Equal("St Mary's: Senior Campus", Uri.UnescapeDataString(issuer));
        Assert.Equal("backup-admin", account);
        Assert.Contains("&issuer=St%20Mary%27s%3A%20Senior%20Campus&", uri);
    }

    [Fact]
    public void The_otpauth_uri_names_the_school_and_the_username()
    {
        var uri = Totp.OtpauthUri("Test Synthetic College", "backup-admin", "JBSWY3DPEHPK3PXP");
        Assert.Equal(
            "otpauth://totp/Test%20Synthetic%20College:backup-admin?secret=JBSWY3DPEHPK3PXP"
            + "&issuer=Test%20Synthetic%20College&algorithm=SHA1&digits=6&period=30", uri);
    }
}
