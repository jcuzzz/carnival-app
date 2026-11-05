using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>Kept apart from <c>roles:administer</c> so either can change without widening the other.</summary>
public static class LocalAccountAuthorization
{
    public const string AdministerAccountsPolicy = "accounts:administer";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<PlatformRole>> PolicyRoles =
        new Dictionary<string, IReadOnlyList<PlatformRole>>
        {
            [AdministerAccountsPolicy] = [PlatformRole.SystemAdministrator],
        };
}
