using System.Net;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Lockout is derived from the attempts, per account and address: a pair is locked while it has
/// <see cref="FailureThreshold"/> failures inside <see cref="LockoutWindow"/> since its last success
/// and the account's last lockout clear. Ordered by Id, which is time order, because SQLite cannot
/// compare a <see cref="DateTimeOffset"/>.
/// </summary>
public sealed class SignInAttemptLog(CarnivalAppDbContext db, TimeProvider time)
{
    public const int FailureThreshold = 10;
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

    /// <summary>The longest IPv6 text form.</summary>
    public const int MaxAddressLength = 45;

    public const string UnknownAddress = "unknown";
    public const string HostAddress = "host";

    // Bounds the in-memory walk behind the administrator's list.
    private const int RecentRowsScanned = 5000;

    private static readonly SignInOutcome[] Failures = [SignInOutcome.WrongPassword, SignInOutcome.WrongCode];
    private static readonly SignInOutcome[] Successes = [SignInOutcome.Succeeded, SignInOutcome.RecoveryCodeUsed];

    public static string AddressText(IPAddress? address)
    {
        if (address is null) return UnknownAddress;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var text = address.ToString();
        return text.Length <= MaxAddressLength ? text : text[..MaxAddressLength];
    }

    /// <summary>Staged on the context; the caller saves.</summary>
    public SignInAttempt Add(Guid? accountId, string clientAddress, SignInOutcome outcome)
    {
        var attempt = new SignInAttempt
        {
            AtUtc = time.GetUtcNow(),
            LocalAccountId = accountId,
            ClientAddress = clientAddress,
            Outcome = outcome,
        };
        db.SignInAttempts.Add(attempt);
        return attempt;
    }

    public async Task RecordAsync(
        Guid? accountId, string clientAddress, SignInOutcome outcome, CancellationToken ct = default)
    {
        Add(accountId, clientAddress, outcome);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsLockedOutAsync(Guid accountId, string clientAddress, CancellationToken ct = default)
    {
        var failures = await db.SignInAttempts.AsNoTracking()
            .Where(a => a.LocalAccountId == accountId && a.ClientAddress == clientAddress
                && (a.Outcome == SignInOutcome.WrongPassword || a.Outcome == SignInOutcome.WrongCode))
            .OrderByDescending(a => a.Id)
            .Take(FailureThreshold)
            .Select(a => new { a.Id, a.AtUtc })
            .ToListAsync(ct);
        if (failures.Count < FailureThreshold) return false;

        var oldest = failures[^1];
        if (oldest.AtUtc <= time.GetUtcNow() - LockoutWindow) return false;

        return !await db.SignInAttempts.AsNoTracking()
            .AnyAsync(a => a.Id > oldest.Id && a.LocalAccountId == accountId
                && (a.Outcome == SignInOutcome.LockoutCleared
                    || (a.ClientAddress == clientAddress && (a.Outcome == SignInOutcome.Succeeded
                        || a.Outcome == SignInOutcome.RecoveryCodeUsed))), ct);
    }

    /// <summary>Whether the account is locked at any address; for the administrator's list.</summary>
    public async Task<bool> IsLockedOutAnywhereAsync(Guid accountId, CancellationToken ct = default)
    {
        var rows = await db.SignInAttempts.AsNoTracking()
            .Where(a => a.LocalAccountId == accountId)
            .OrderByDescending(a => a.Id)
            .Take(RecentRowsScanned)
            .Select(a => new { a.AtUtc, a.ClientAddress, a.Outcome })
            .ToListAsync(ct);

        var windowStart = time.GetUtcNow() - LockoutWindow;
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Outcome == SignInOutcome.LockoutCleared) break;
            if (settled.Contains(row.ClientAddress)) continue;
            if (Successes.Contains(row.Outcome))
            {
                settled.Add(row.ClientAddress);
                continue;
            }
            if (!Failures.Contains(row.Outcome) || row.AtUtc <= windowStart) continue;
            failures[row.ClientAddress] = failures.GetValueOrDefault(row.ClientAddress) + 1;
            if (failures[row.ClientAddress] >= FailureThreshold) return true;
        }
        return false;
    }

    public async Task<DateTimeOffset?> LastSignInAsync(Guid accountId, CancellationToken ct = default) =>
        await db.SignInAttempts.AsNoTracking()
            .Where(a => a.LocalAccountId == accountId
                && (a.Outcome == SignInOutcome.Succeeded || a.Outcome == SignInOutcome.RecoveryCodeUsed))
            .OrderByDescending(a => a.Id)
            .Select(a => (DateTimeOffset?)a.AtUtc)
            .FirstOrDefaultAsync(ct);
}
