using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// Kept apart from <c>school:administer</c> so it cannot
/// widen with a later change to what school settings means.
/// </summary>
public static class RoleAuthorization
{
    public const string AdministerRolesPolicy = "roles:administer";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<PlatformRole>> PolicyRoles =
        new Dictionary<string, IReadOnlyList<PlatformRole>>
        {
            [AdministerRolesPolicy] = [PlatformRole.SystemAdministrator],
        };
}
