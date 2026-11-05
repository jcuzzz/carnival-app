using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Whether a local account may sign in: always with no identity provider; beside one, as the
/// school setting says. Checked at sign-in and on every request of a local session.
/// </summary>
public sealed class LocalAccountPolicy(
    CarnivalAppDbContext db, SchoolSettingsProvider settings, IConfiguration configuration)
{
    public static bool ProviderConfigured(IConfiguration configuration) =>
        IdentitySetup.ResolveProvider(configuration)
            is IdentitySetup.IdentityProvider.Entra or IdentitySetup.IdentityProvider.Adfs;

    // Read from the database, not the role-assignment cache: the host command grants from another
    // process, whose writes no cache here would see.
    public async Task<bool> IsPermittedAsync(Guid accountId, CancellationToken ct = default)
    {
        if (!ProviderConfigured(configuration)) return true;
        if ((await settings.GetAsync(ct)).LocalAccounts == LocalAccountAccess.Everyone) return true;
        return await IsSystemAdministratorAsync(db, accountId, ct);
    }

    public static Task<bool> IsSystemAdministratorAsync(
        CarnivalAppDbContext db, Guid accountId, CancellationToken ct = default)
    {
        var subject = LocalAccount.SubjectFor(accountId);
        return db.RoleAssignments.AsNoTracking().AnyAsync(row => row.SubjectId == subject
            && row.Role == PlatformRole.SystemAdministrator && row.RevokedAtUtc == null, ct);
    }
}
