using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// Unions the group mapping, the in-app assignments and the bootstrap administrators. Runs on
/// every authenticated request regardless of scheme, so it must stay idempotent.
/// </summary>
public class GroupToRoleClaimsTransformation(
    IConfiguration configuration,
    ILogger<GroupToRoleClaimsTransformation> logger,
    IRoleAssignmentSource assignments)
    : IClaimsTransformation
{
    public const string ConfigSection = "Identity:GroupToRoleMappings";
    public const string GroupsClaimTypeConfig = "Identity:GroupsClaimType";

    public const string BootstrapAdministratorsConfig = "Identity:BootstrapAdministrators";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return principal;

        var existingRoles = new HashSet<string>(
            principal.FindAll(PlatformClaims.Role).Select(c => c.Value), StringComparer.OrdinalIgnoreCase);

        void Grant(PlatformRole role)
        {
            if (existingRoles.Add(role.ToString()))
                identity.AddClaim(new Claim(PlatformClaims.Role, role.ToString()));
        }

        AddGroupRoles(principal, Grant);

        var subject = principal.GetStableSubject();
        var upn = principal.GetUpn();
        if (IsBootstrapAdministrator(subject, upn)) Grant(PlatformRole.SystemAdministrator);

        await AddAssignedRolesAsync(principal, subject, upn, Grant);
        return principal;
    }

    /// <summary>
    /// An indexed array or a single value, any of them comma-separated. Read per request, never
    /// seeded: an .env edit and a restart is the lock-out recovery path.
    /// </summary>
    public static IReadOnlyList<string> BootstrapAdministrators(IConfiguration configuration)
    {
        var section = configuration.GetSection(BootstrapAdministratorsConfig);
        return section.GetChildren().Select(child => child.Value)
            .Prepend(section.Value)
            .SelectMany(value => (value ?? "").Split(
                ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();
    }

    private bool IsBootstrapAdministrator(string? subject, string? upn) =>
        BootstrapAdministrators(configuration)
            .Any(entry => Matches(entry, subject) || Matches(entry, upn));

    private static bool Matches(string entry, string? value) =>
        value is not null && entry.Equals(value, StringComparison.OrdinalIgnoreCase);

    private async Task AddAssignedRolesAsync(
        ClaimsPrincipal principal, string? subject, string? upn, Action<PlatformRole> grant)
    {
        var active = await assignments.GetActiveAsync();
        if (active.Count == 0) return;

        var normalisedUpn = upn?.Trim().ToLowerInvariant();
        var unbound = new List<Guid>();
        foreach (var row in active)
        {
            if (row.SubjectId is not null)
            {
                // Once bound, match by subject id only: an oid is stable, a UPN gets renamed.
                if (subject is not null && row.SubjectId.Equals(subject, StringComparison.OrdinalIgnoreCase))
                    grant(row.Role);
                continue;
            }
            if (normalisedUpn is null || !row.SubjectUpn.Equals(normalisedUpn, StringComparison.Ordinal))
                continue;
            grant(row.Role);
            if (subject is not null) unbound.Add(row.Id);
        }

        if (unbound.Count > 0)
            await assignments.BindAsync(unbound, subject!, normalisedUpn!, principal.GetDisplayName());
    }

    private void AddGroupRoles(ClaimsPrincipal principal, Action<PlatformRole> grant)
    {
        var mappings = configuration.GetSection(ConfigSection).Get<Dictionary<string, string>>();
        if (mappings is null || mappings.Count == 0) return;

        var groupsClaimType = configuration[GroupsClaimTypeConfig];
        if (string.IsNullOrWhiteSpace(groupsClaimType)) groupsClaimType = PlatformClaims.Groups;

        // Materialise before AddClaim mutates the identity's claim collection.
        var groupIds = principal.FindAll(groupsClaimType).Select(c => c.Value).ToList();
        foreach (var groupId in groupIds)
        {
            if (!mappings.TryGetValue(groupId, out var roleName))
            {
                // Otherwise a mis-cased group id is silent: the mapping keys match ordinally, and
                // the person just gets 403 on everything.
                logger.LogInformation(
                    "Group {GroupId} is not present in Identity:GroupToRoleMappings — no role granted. " +
                    "Check the mapping, including its exact casing.", groupId);
                continue;
            }
            if (!PlatformRoles.TryParse(roleName, out var role, out var viaAlias))
            {
                logger.LogWarning(
                    "Identity:GroupToRoleMappings maps group {GroupId} to unknown role '{Role}' — ignored.",
                    groupId, roleName);
                continue;
            }
            if (viaAlias)
                // Information, not Debug: silence here is the condition for removing the aliases.
                logger.LogInformation(
                    "Identity:GroupToRoleMappings maps group {GroupId} to retired role name '{Alias}' " +
                    "— resolved to {Role}. Update the mapping; the aliases are temporary.",
                    groupId, roleName, role);
            grant(role);
        }
    }
}
