using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.School;

public static class SchoolAuthorization
{
    public const string AdministerSchoolPolicy = "school:administer";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<PlatformRole>> PolicyRoles =
        new Dictionary<string, IReadOnlyList<PlatformRole>>
        {
            [AdministerSchoolPolicy] = [PlatformRole.SystemAdministrator],
        };
}
