using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

public static class DivisionNaming
{
    public static string Division(string ageGroup, CompetitionCategory? category) =>
        $"{AgeScopeName(ageGroup)} {Category(category)}";

    public static string AgeScopeName(string ageGroup) =>
        ageGroup == AgeScope.OpenKey ? "All ages" : ageGroup;

    public static string Category(CompetitionCategory? category) => category switch
    {
        CompetitionCategory.M => "Boys",
        CompetitionCategory.F => "Girls",
        CompetitionCategory.S => "Open",
        _ => "All categories",
    };
}
