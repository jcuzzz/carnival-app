using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Identity's store, except that recovery codes are kept as SHA-256 hashes rather than as text.
/// The codes are random, about 50 bits each, so an unsalted fast hash is enough.
/// </summary>
public sealed class LocalAccountStore(CarnivalAppDbContext context, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<LocalAccount, CarnivalAppDbContext, Guid>(context, describer)
{
    // Identity's own names for the row, so the token layout matches what it expects.
    private const string InternalLoginProvider = "[AspNetUserStore]";
    private const string RecoveryCodeTokenName = "RecoveryCodes";

    public static string HashRecoveryCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));

    public override Task ReplaceCodesAsync(
        LocalAccount user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken) =>
        SetHashesAsync(user, recoveryCodes.Select(HashRecoveryCode), cancellationToken);

    public override async Task<bool> RedeemCodeAsync(LocalAccount user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(code)) return false;

        var stored = (await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var typed = Encoding.ASCII.GetBytes(HashRecoveryCode(code));
        var match = stored.FirstOrDefault(hash =>
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), typed));
        if (match is null) return false;

        await SetHashesAsync(user, stored.Where(hash => hash != match), cancellationToken);
        return true;
    }

    private Task SetHashesAsync(LocalAccount user, IEnumerable<string> hashes, CancellationToken ct) =>
        SetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, string.Join(";", hashes), ct);
}
