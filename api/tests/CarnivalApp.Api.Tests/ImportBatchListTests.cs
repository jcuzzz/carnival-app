using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>GET /api/v1/imports names who staged each batch.</summary>
public sealed class ImportBatchListTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static ImportBatch EarlierBatch(string subject, string fileName) => new()
    {
        Id = Guid.CreateVersion7(),
        SourceFileName = fileName,
        SourceFileHash = "hash",
        MappingVersion = "test",
        ImportedBySubject = subject,
        ImportedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        Status = ImportBatchStatus.Rejected,
    };

    private async Task<Dictionary<string, string?>> NamesByFileAsync()
    {
        var response = await _factory.CreateClientAs("list-admin", "SystemAdministrator")
            .GetAsync("/api/v1/imports");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(
                b => b.GetProperty("sourceFileName").GetString()!,
                b => b.GetProperty("importedByDisplayName").GetString());
    }

    [Fact]
    public async Task A_new_batch_records_the_importers_name()
    {
        using var form = new MultipartFormDataContent
        {
            {
                new ByteArrayContent(Encoding.UTF8.GetBytes(
                    "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
                    + "N1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n")),
                "file", "named.csv"
            },
        };
        // The dev header scheme's name claim is the subject itself.
        var staged = await _factory.CreateClientAs("stager", "SystemAdministrator")
            .PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.Created, staged.StatusCode);

        Assert.Equal("stager", (await NamesByFileAsync())["named.csv"]);
    }

    [Fact]
    public async Task An_earlier_batch_takes_the_name_of_the_account_or_role_holder_behind_it()
    {
        var accountId = Guid.CreateVersion7();
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.LocalAccounts.Add(new LocalAccount
            {
                Id = accountId,
                UserName = "jo",
                NormalizedUserName = "JO",
                DisplayName = "Jo Admin",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.CreateVersion7(),
                SubjectId = "entra-subject",
                SubjectUpn = "sam@example.org",
                SubjectDisplayName = "Sam Staff",
                Role = PlatformRole.SystemAdministrator,
                Reason = "test",
                CreatedBySubject = "test",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            db.ImportBatches.AddRange(
                EarlierBatch(LocalAccount.SubjectFor(accountId), "local.csv"),
                EarlierBatch("entra-subject", "assigned.csv"),
                EarlierBatch("someone-unknown", "unknown.csv"));
        });

        var names = await NamesByFileAsync();

        Assert.Equal("Jo Admin", names["local.csv"]);
        Assert.Equal("Sam Staff", names["assigned.csv"]);
        Assert.Null(names["unknown.csv"]);
    }

    [Fact]
    public async Task A_subject_with_several_grants_takes_the_newest_grants_name()
    {
        RoleAssignment Grant(string name, int daysAgo, PlatformRole role) => new()
        {
            Id = Guid.CreateVersion7(),
            SubjectId = "renamed-subject",
            SubjectUpn = "pat@example.org",
            SubjectDisplayName = name,
            Role = role,
            Reason = "test",
            CreatedBySubject = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        };
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            // Newest in the middle, so neither insertion order nor its reverse picks it by accident.
            db.RoleAssignments.AddRange(
                Grant("Pat Old", 30, PlatformRole.Viewer),
                Grant("Pat New", 1, PlatformRole.SystemAdministrator),
                Grant("Pat Older", 60, PlatformRole.Recorder));
            db.ImportBatches.Add(EarlierBatch("renamed-subject", "renamed.csv"));
        });

        Assert.Equal("Pat New", (await NamesByFileAsync())["renamed.csv"]);
    }
}
