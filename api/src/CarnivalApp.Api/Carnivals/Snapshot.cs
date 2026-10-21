using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

// Copied once onto an entry or relay leg at creation; a later roster import never rewrites it.
internal sealed record Snapshot(
    Guid RosterSnapshotId,
    string DisplayName,
    int YearLevel,
    string HouseCode,
    string AgeGroup,
    CompetitionCategory Category)
{
    public static string NameOf(Student student) =>
        NameOf(student.PreferredName, student.FirstName, student.Surname);

    // The eligible-student search projects loose columns; the name it offers must be the one
    // the entry then snapshots.
    public static string NameOf(string? preferredName, string firstName, string surname) =>
        $"{preferredName ?? firstName} {surname}";

    public static Snapshot From(Student student, RosterSnapshot rosterSnapshot) => new(
        rosterSnapshot.Id,
        NameOf(student),
        rosterSnapshot.YearLevel,
        rosterSnapshot.HouseCode,
        rosterSnapshot.CompetitionAgeGroup,
        rosterSnapshot.CompetitionCategory);
}
