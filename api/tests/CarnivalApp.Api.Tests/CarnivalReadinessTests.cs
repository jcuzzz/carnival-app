using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0066 §5: Draft → Open needs a student list and at least one division; nothing else Manage
/// lists before opening blocks it. Each test owns its factory: the roster is school-wide.
/// </summary>
public sealed class CarnivalReadinessTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public CarnivalReadinessTests() =>
        _admin = _factory.CreateClientAs("readiness-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> CreateCarnivalAsync() =>
        await DivisionFixture.CreateCarnivalAsync(_admin, "Readiness Carnival");

    private Task<HttpResponseMessage> ChangeStatusAsync(Guid id, string status) =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/status", new { status });

    private Task<CarnivalStatus> StatusOfAsync(Guid id) =>
        CarnivalSeed.QueryAsync(_factory, db => db.Carnivals.Where(c => c.Id == id)
            .Select(c => c.Status).SingleAsync());

    private Task<int> StatusAuditsAsync(Guid id) =>
        CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.CountAsync(a =>
            a.EntityId == id.ToString() && a.Action == AuditActions.CarnivalStatus));

    private static async Task AssertNotReadyAsync(HttpResponseMessage response, params string[] missing)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotReady);
        Assert.Equal(missing, (await response.ErrorContextAsync("missing"))
            .EnumerateArray().Select(m => m.GetString()));
    }

    /// <summary>A Scheduled division with no student list behind it.</summary>
    private async Task<Guid> AddDivisionAsync(Guid carnivalId) =>
        await CarnivalSeed.QueryAsync(_factory, async db =>
        {
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"RD-{Guid.CreateVersion7():N}"[..20],
                Name = "Freestyle 50m",
                Sport = "Swimming",
                MeasureType = MeasureType.Time,
                Units = "s",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            var division = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnivalId,
                EventDefinitionId = definition.Id,
                AgeGroup = "12",
                Category = CompetitionCategory.M,
                Status = CarnivalEventStatus.Scheduled,
            };
            db.EventDefinitions.Add(definition);
            db.CarnivalEvents.Add(division);
            await db.SaveChangesAsync();
            return division.Id;
        });

    [Fact]
    public async Task Opening_with_no_student_list_is_refused_and_changes_nothing()
    {
        var id = await CreateCarnivalAsync();
        await AddDivisionAsync(id);

        await AssertNotReadyAsync(await ChangeStatusAsync(id, "Open"), "studentList");
        Assert.Equal(CarnivalStatus.Draft, await StatusOfAsync(id));
        Assert.Equal(0, await StatusAuditsAsync(id));
    }

    [Fact]
    public async Task Opening_with_no_divisions_is_refused()
    {
        var id = await CreateCarnivalAsync();
        await CarnivalSeed.SeedStudentAsync(_factory);

        await AssertNotReadyAsync(await ChangeStatusAsync(id, "Open"), "events");
        Assert.Equal(CarnivalStatus.Draft, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Opening_an_empty_carnival_names_both()
    {
        var id = await CreateCarnivalAsync();

        await AssertNotReadyAsync(await ChangeStatusAsync(id, "Open"), "studentList", "events");
    }

    /// <summary>The Students step's facts: only an active student's current snapshot counts.</summary>
    [Fact]
    public async Task Inactive_or_superseded_students_are_no_student_list()
    {
        var id = await CreateCarnivalAsync();
        await AddDivisionAsync(id);
        await CarnivalSeed.SeedStudentAsync(_factory);
        await CarnivalSeed.MutateAsync(_factory, db => db.Students.Single().IsActive = false);
        await AssertNotReadyAsync(await ChangeStatusAsync(id, "Open"), "studentList");

        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.Students.Single().IsActive = true;
            db.RosterSnapshots.Single().IsCurrent = false;
        });
        await AssertNotReadyAsync(await ChangeStatusAsync(id, "Open"), "studentList");
    }

    [Fact]
    public async Task A_carnival_with_students_and_a_division_opens()
    {
        var id = await CreateCarnivalAsync();
        await CarnivalSeed.MakeReadyToOpenAsync(_factory, id);

        var response = await ChangeStatusAsync(id, "Open");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CarnivalStatus.Open, await StatusOfAsync(id));
    }

    /// <summary>Manage's primary sends the status change, then open-scheduled only if it succeeded.</summary>
    [Fact]
    public async Task Opening_the_carnival_and_its_divisions_is_refused_at_the_first_step()
    {
        var id = await CreateCarnivalAsync();
        var division = await AddDivisionAsync(id);

        await AssertNotReadyAsync(await ChangeStatusAsync(id, "Open"), "studentList");
        Assert.Equal(CarnivalStatus.Draft, await StatusOfAsync(id));
        Assert.Equal(CarnivalEventStatus.Scheduled, await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.Where(e => e.Id == division).Select(e => e.Status).SingleAsync()));
    }

    /// <summary>Only the first open is checked: a carnival already run is never trapped.</summary>
    [Fact]
    public async Task Closing_and_reopening_are_not_checked()
    {
        var id = await CreateCarnivalAsync();
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == id).Status = CarnivalStatus.Open);

        Assert.Equal(HttpStatusCode.OK, (await ChangeStatusAsync(id, "Closed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeStatusAsync(id, "Open")).StatusCode);
        Assert.Equal(CarnivalStatus.Open, await StatusOfAsync(id));
    }

    /// <summary>The lifecycle refusal comes first: a wrong target is not a readiness problem.</summary>
    [Fact]
    public async Task An_illegal_transition_is_refused_as_such_on_an_empty_carnival()
    {
        var id = await CreateCarnivalAsync();

        var response = await ChangeStatusAsync(id, "Closed");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.IllegalCarnivalTransition);
    }
}
