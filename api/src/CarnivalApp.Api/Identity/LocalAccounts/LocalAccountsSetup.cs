using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Identity's core services only: no SignInManager, token providers, roles or Identity lockout.
/// Lockout comes from <see cref="SignInAttemptLog"/>, roles from role assignments.
/// </summary>
public static class LocalAccountsSetup
{
    /// <summary>OWASP's figure for PBKDF2-HMAC-SHA512; an older hash upgrades at its next sign-in.</summary>
    public const int PasswordHashIterations = 220_000;

    public static IServiceCollection AddLocalAccounts(this IServiceCollection services)
    {
        services.AddIdentityCore<LocalAccount>(options =>
            {
                options.Password.RequiredLength = LocalPasswordValidator.MinimumLength;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = false;
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789._-";
                options.User.RequireUniqueEmail = false;
            })
            .AddUserStore<LocalAccountStore>()
            .AddPasswordValidator<LocalPasswordValidator>();
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = PasswordHashIterations;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SignInAttemptLog>();
        services.AddScoped<LocalAccountPolicy>();
        services.AddScoped<LocalAccountSelfService>();
        services.AddScoped<LocalSignInService>();
        services.AddScoped<SetupLinks>();
        services.AddScoped<LocalAccountAdministration>();
        return services;
    }

    /// <summary>
    /// Verified when there is no real hash to check, so an unknown name costs what a known one does.
    /// Always at the full work factor, whatever a test host configures.
    /// </summary>
    internal static readonly Lazy<string> DummyPasswordHash = new(() =>
        new PasswordHasher<LocalAccount>(Options.Create(new PasswordHasherOptions
        {
            IterationCount = PasswordHashIterations,
        })).HashPassword(new LocalAccount(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    /// <summary>A new security stamp ends the account's other sessions at their next request.</summary>
    internal static void RotateStamps(LocalAccount account)
    {
        account.SecurityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        account.ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    internal static AuditActor SelfActor(LocalAccount account) => new(LocalAccount.SubjectFor(account.Id), null);
}

/// <summary><see cref="ErrorCode"/> is one of <c>ErrorCodes</c>; null on success.</summary>
public sealed record LocalAccountResult<T>(T? Value, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;

    public static LocalAccountResult<T> Ok(T value) => new(value, null);

    public static LocalAccountResult<T> Fail(string errorCode) => new(default, errorCode);
}
