using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

// Role membership is the only gate here; per-event official assignment is advisory data.
// Every policy needs a role: RolesAuthorizationRequirement rejects an empty list at startup.
public static class CarnivalAuthorization
{
    public const string AdministerCarnivalsPolicy = "carnivals:administer";

    // Excludes the category override: an eligibility control that stays with the administrator.
    public const string MarshalCarnivalsPolicy = "carnivals:marshal";

    public const string ViewCarnivalsPolicy = "carnivals:view";

    public const string SubmitResultsPolicy = "results:submit";

    public const string CorrectResultsPolicy = "results:correct";

    // Entry creation is marshalling, but a category override it carries is not, so the handler
    // refuses the override rather than the route.
    public static bool MayAdminister(System.Security.Claims.ClaimsPrincipal user) =>
        PolicyRoles[AdministerCarnivalsPolicy].Any(role => user.IsInRole(role.ToString()));

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<PlatformRole>> PolicyRoles =
        new Dictionary<string, IReadOnlyList<PlatformRole>>
        {
            [AdministerCarnivalsPolicy] =
            [
                PlatformRole.CarnivalAdministrator,
                PlatformRole.SystemAdministrator,
            ],
            [MarshalCarnivalsPolicy] =
            [
                PlatformRole.Recorder,
                PlatformRole.CarnivalAdministrator,
                PlatformRole.SystemAdministrator,
            ],
            [ViewCarnivalsPolicy] =
            [
                PlatformRole.CarnivalAdministrator,
                PlatformRole.Recorder,
                PlatformRole.Viewer,
                PlatformRole.SystemAdministrator,
            ],
            [SubmitResultsPolicy] =
            [
                PlatformRole.Recorder,
                PlatformRole.CarnivalAdministrator,
                PlatformRole.SystemAdministrator,
            ],
            [CorrectResultsPolicy] =
            [
                PlatformRole.Recorder,
                PlatformRole.CarnivalAdministrator,
                PlatformRole.SystemAdministrator,
            ],
        };
}
