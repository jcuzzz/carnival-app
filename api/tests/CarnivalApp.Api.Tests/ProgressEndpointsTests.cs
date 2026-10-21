using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The Dashboard progress aggregate (ADR-0043 §2): one read that says what is
/// recorded against what is expected, per event and in total, who is competing
/// per house, and which swims would set a record.
/// </summary>
/// <remarks>
/// Two properties carry the file. Progress is about RECORDING, not points, so a
/// no-time outcome (DNS/DNF/DQ) is recorded work and an event excluded from
/// scoring still appears. And "records today" is a READ: it compares against the
/// standing book with the same comparison finalisation mints with, and writes
/// nothing — ADR-0028 mints records at finalisation and nowhere else.
/// </remarks>
public sealed class ProgressEndpointsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _official;

    public ProgressEndpointsTests()
    {
        _admin = _factory.CreateClientAs("progress-admin", "CarnivalAdministrator");
        _official = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
    }

    public void Dispose() => _factory.Dispose();

    // ---- Helpers ---------------------------------------------------------

    private static string ProgressPath(Guid carnivalId) => $"/api/v1/carnivals/{carnivalId}/progress";

    private async Task<JsonElement> ProgressAsync(Guid carnivalId, HttpClient? client = null)
    {
        var response = await (client ?? _admin).GetAsync(ProgressPath(carnivalId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Rows(JsonElement progress, string property) =>
        [.. progress.GetProperty(property).EnumerateArray()];

    private async Task<Guid> EnterAsync(Guid carnivalId, Guid eventId, Guid studentId)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/{eventId}/entries", new { studentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task CaptureAsync(Guid carnivalId, Guid eventId, Guid entryId, object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/carnivals/{carnivalId}/events/{eventId}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        var response = await _official.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<Guid> StreamAsync(Guid carnivalId, string name)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/schedule/streams", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SlotAsync(Guid carnivalId, Guid streamId, string label, string start)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/schedule/streams/{streamId}/slots",
            new { label, startTime = start });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task PlaceAsync(Guid carnivalId, Guid slotId, Guid eventId)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/schedule/slots/{slotId}/events", new { eventId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Totals and status derivation ------------------------------------

    [Fact]
    public async Task Status_and_totals_come_from_entries_and_results_with_a_dns_counting_as_recorded()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var first = await EnterAsync(seeded.CarnivalId, seeded.EventId, seeded.EligibleBoyId);
        var second = await EnterAsync(seeded.CarnivalId, seeded.EventId, seeded.SecondEligibleBoyId);

        var progress = await ProgressAsync(seeded.CarnivalId);
        Assert.Equal(seeded.CarnivalId, progress.GetProperty("carnivalId").GetGuid());
        Assert.True(progress.GetProperty("provisional").GetBoolean());
        var totals = progress.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("events").GetInt32());
        Assert.Equal(1, totals.GetProperty("eventsNotStarted").GetInt32());
        Assert.Equal(2, totals.GetProperty("expected").GetInt32());
        Assert.Equal(0, totals.GetProperty("recorded").GetInt32());
        Assert.Equal("notStarted", Assert.Single(Rows(progress, "events")).GetProperty("status").GetString());

        await CaptureAsync(seeded.CarnivalId, seeded.EventId, first, new { value = "34.56" });
        progress = await ProgressAsync(seeded.CarnivalId);
        Assert.Equal(1, progress.GetProperty("totals").GetProperty("eventsInProgress").GetInt32());
        var half = Assert.Single(Rows(progress, "events"));
        Assert.Equal("inProgress", half.GetProperty("status").GetString());
        Assert.Equal(2, half.GetProperty("expected").GetInt32());
        Assert.Equal(1, half.GetProperty("recorded").GetInt32());

        // A no-time outcome IS recorded: the official has nothing left to do for
        // that entry, so the event is finished rather than half-done.
        await CaptureAsync(seeded.CarnivalId, seeded.EventId, second, new { status = "DNS" });
        progress = await ProgressAsync(seeded.CarnivalId);
        Assert.Equal(1, progress.GetProperty("totals").GetProperty("eventsComplete").GetInt32());
        Assert.Equal(2, progress.GetProperty("totals").GetProperty("recorded").GetInt32());
        Assert.Equal("complete", Assert.Single(Rows(progress, "events")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_event_nobody_entered_is_not_started_rather_than_complete()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var row = Assert.Single(Rows(await ProgressAsync(seeded.CarnivalId), "events"));
        Assert.Equal(0, row.GetProperty("expected").GetInt32());
        Assert.Equal("notStarted", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_excluded_event_still_appears_because_progress_is_about_recording_not_points()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).ScoresPoints = false);
        await EnterAsync(seeded.CarnivalId, seeded.EventId, seeded.EligibleBoyId);

        var row = Assert.Single(Rows(await ProgressAsync(seeded.CarnivalId), "events"));
        Assert.Equal(seeded.EventId, row.GetProperty("carnivalEventId").GetGuid());
        Assert.Equal(1, row.GetProperty("expected").GetInt32());
    }

    /// <summary>
    /// ADR-0014 §4: a Social-scored event owes no results at all, so its entries
    /// alone complete it — the same rule <c>CarnivalEventDto.Complete</c> applies,
    /// so the Dashboard and the deck cannot disagree about one event.
    /// </summary>
    [Fact]
    public async Task A_social_scored_event_reads_complete_on_its_entries()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var social = await CarnivalSeed.AddDivisionAsync(
            _factory, seeded, CompetitionCategory.F, EventTier.Social);
        await EnterAsync(seeded.CarnivalId, social, seeded.GirlId);

        var row = Assert.Single(Rows(await ProgressAsync(seeded.CarnivalId), "events"),
            e => e.GetProperty("carnivalEventId").GetGuid() == social);
        Assert.Equal(1, row.GetProperty("expected").GetInt32());
        Assert.Equal(0, row.GetProperty("recorded").GetInt32());
        Assert.Equal("complete", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_draft_result_is_not_recorded()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await EnterAsync(seeded.CarnivalId, seeded.EventId, seeded.EligibleBoyId);
        // Drafts are client-side and never reach the server through the API, so
        // the state has to be seeded to pin that the filter holds if one ever does.
        await CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(new Result
        {
            Id = Guid.CreateVersion7(),
            EntryId = entry,
            Status = ResultStatus.Ok,
            MeasuredValue = 33.0m,
            LifecycleState = ResultLifecycleState.Draft,
            CurrentRevision = 1,
            RecordedBySubject = "seeder",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));

        var progress = await ProgressAsync(seeded.CarnivalId);
        var row = Assert.Single(Rows(progress, "events"));
        Assert.Equal(1, row.GetProperty("expected").GetInt32());
        Assert.Equal(0, row.GetProperty("recorded").GetInt32());
        Assert.Equal("notStarted", row.GetProperty("status").GetString());
        Assert.Equal(0, progress.GetProperty("totals").GetProperty("recorded").GetInt32());
    }

    // ---- Program order ---------------------------------------------------

    [Fact]
    public async Task Events_are_listed_in_program_slot_order_and_carry_their_slot()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var second = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var stream = await StreamAsync(seeded.CarnivalId, "Pool");
        var first = await SlotAsync(seeded.CarnivalId, stream, "FREESTYLE EVENTS", "09:45");
        var later = await SlotAsync(seeded.CarnivalId, stream, "BACKSTROKE EVENTS", "10:30");
        // The later slot holds the event that would otherwise sort first.
        await PlaceAsync(seeded.CarnivalId, later, seeded.EventId);
        await PlaceAsync(seeded.CarnivalId, first, second);

        var events = Rows(await ProgressAsync(seeded.CarnivalId), "events");
        Assert.Equal([second, seeded.EventId], events.Select(e => e.GetProperty("carnivalEventId").GetGuid()));
        Assert.Equal(first, events[0].GetProperty("slotId").GetGuid());
        Assert.Equal("FREESTYLE EVENTS", events[0].GetProperty("slotLabel").GetString());
        Assert.Equal("Pool", events[0].GetProperty("stream").GetString());
        Assert.StartsWith("09:45", events[0].GetProperty("startTime").GetString(), StringComparison.Ordinal);
        Assert.Equal("Freestyle 50m", events[0].GetProperty("definitionName").GetString());
        Assert.Equal($"{seeded.PrimaryAgeGroup} Girls", events[0].GetProperty("divisionLabel").GetString());
    }

    [Fact]
    public async Task An_unscheduled_event_sorts_last_and_carries_no_slot()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var unscheduled = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var stream = await StreamAsync(seeded.CarnivalId, "Pool");
        var slot = await SlotAsync(seeded.CarnivalId, stream, "FREESTYLE EVENTS", "09:45");
        await PlaceAsync(seeded.CarnivalId, slot, seeded.EventId);

        var events = Rows(await ProgressAsync(seeded.CarnivalId), "events");
        Assert.Equal([seeded.EventId, unscheduled],
            events.Select(e => e.GetProperty("carnivalEventId").GetGuid()));
        var last = events[1];
        Assert.Equal(JsonValueKind.Null, last.GetProperty("slotId").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("slotLabel").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("stream").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("startTime").ValueKind);
        Assert.Equal(0, last.GetProperty("sortOrder").GetInt32());
    }

    // ---- Participation ---------------------------------------------------

    /// <summary>
    /// Counts of STUDENTS, distinct, and nothing about any one of them
    /// (ADR-0026). Its own host: <c>competitors</c> is the school's current
    /// roster, which every seed in a shared database adds to.
    /// </summary>
    [Fact]
    public async Task Participation_counts_distinct_students_per_house()
    {
        using var factory = new ApiTestFactory();
        var admin = factory.CreateClientAs("participation-admin", "CarnivalAdministrator");
        var official = factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        var seeded = await CarnivalSeed.SeedAsync(factory);

        async Task<Guid> Post(string path, object body)
        {
            var created = await admin.PostAsJsonAsync(path, body);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        // A second EVENT, not a sibling division: ADR-0023 §4 refuses the same
        // student twice within one definition, so the swimmer needs two swims.
        var backstroke = await Post("/api/v1/event-definitions", new
        {
            code = $"SW-50-BACK-{seeded.PrimaryAgeGroup}",
            name = "Backstroke 50m",
            sport = "Swimming",
            measureType = "Time",
            units = "s",
        });
        var second = await Post($"/api/v1/carnivals/{seeded.CarnivalId}/events", new
        {
            eventDefinitionId = backstroke,
            ageGroup = seeded.PrimaryAgeGroup,
            category = "M",
        });

        Task<Guid> Enter(Guid eventId, Guid studentId) =>
            Post($"/api/v1/carnivals/{seeded.CarnivalId}/events/{eventId}/entries", new { studentId });

        // Two swims for one swimmer: entered counts the STUDENT once.
        var swum = await Enter(seeded.EventId, seeded.EligibleBoyId);
        await Enter(second, seeded.EligibleBoyId);
        await Enter(seeded.EventId, seeded.SecondEligibleBoyId);

        var capture = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{swum}/results")
        {
            Content = JsonContent.Create(new { value = "30.10" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        Assert.Equal(HttpStatusCode.Created, (await official.SendAsync(capture)).StatusCode);

        var response = await admin.GetAsync(ProgressPath(seeded.CarnivalId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var participation = Rows(await response.Content.ReadFromJsonAsync<JsonElement>(), "participation");

        Assert.Equal(["BANKSIA", "GREVILLEA", "MOUNTAIN ASH", "WATTLE"],
            participation.Select(p => p.GetProperty("houseCode").GetString()));
        void House(string code, int competitors, int entered, int recorded)
        {
            var row = Assert.Single(participation, p => p.GetProperty("houseCode").GetString() == code);
            Assert.Equal(competitors, row.GetProperty("competitors").GetInt32());
            Assert.Equal(entered, row.GetProperty("entered").GetInt32());
            Assert.Equal(recorded, row.GetProperty("recorded").GetInt32());
        }
        // The seed's roster: one active student per house but WATTLE, which has two.
        // The inactive student and the one with no snapshot are not competitors.
        House("BANKSIA", 1, 1, 1);
        House("WATTLE", 2, 1, 0);
        House("MOUNTAIN ASH", 1, 0, 0);
        House("GREVILLEA", 1, 0, 0);
    }

    /// <summary>
    /// A relay is ONE entry carrying four swimmers (ADR-0008): the spine counts
    /// the team it owes a time for, participation counts the people who swam.
    /// </summary>
    [Fact]
    public async Task A_relay_expects_one_team_and_participates_all_four_legs()
    {
        using var factory = new ApiTestFactory();
        var admin = factory.CreateClientAs("relay-admin", "CarnivalAdministrator");
        var seeded = await CarnivalSeed.SeedAsync(factory);

        var relayEventId = Guid.CreateVersion7();
        var mates = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var names = new[] { "Ivy", "Jai", "Kit" };
        await CarnivalSeed.MutateAsync(factory, db =>
        {
            var batchId = db.ImportBatches.First().Id;
            for (var i = 0; i < mates.Length; i++)
            {
                db.Students.Add(new Student
                {
                    Id = mates[i],
                    ExternalStudentId = $"RELAY-{names[i]}",
                    Surname = "Swift",
                    FirstName = names[i],
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                db.RosterSnapshots.Add(new RosterSnapshot
                {
                    Id = Guid.CreateVersion7(),
                    StudentId = mates[i],
                    SchoolYear = 2027,
                    YearLevel = 7,
                    HouseCode = "BANKSIA",
                    CompetitionAgeGroup = seeded.PrimaryAgeGroup,
                    CompetitionCategory = CompetitionCategory.M,
                    ImportBatchId = batchId,
                    EffectiveDate = new DateOnly(2027, 1, 30),
                    IsCurrent = true,
                });
            }

            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = "SW-4X50-FREE-RELAY-PROG",
                Name = "Freestyle Relay 4x50m",
                Sport = "Swimming",
                MeasureType = MeasureType.Time,
                Units = "s",
                Format = EventFormat.Team,
                TeamSize = 4,
                LegsJson = """["Freestyle","Freestyle","Freestyle","Freestyle"]""",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.EventDefinitions.Add(definition);
            var relay = new CarnivalEvent
            {
                Id = relayEventId,
                CarnivalId = seeded.CarnivalId,
                EventDefinitionId = definition.Id,
                Category = CompetitionCategory.M,
                Status = CarnivalEventStatus.Open,
            };
            relay.AgeGroup = seeded.PrimaryAgeGroup;
            db.CarnivalEvents.Add(relay);
        });

        var created = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{relayEventId}/team-entries",
            new { houseCode = "BANKSIA", studentIds = mates.Prepend(seeded.EligibleBoyId) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var progress = await ProgressAsync(seeded.CarnivalId, admin);
        var row = Assert.Single(Rows(progress, "events"),
            e => e.GetProperty("carnivalEventId").GetGuid() == relayEventId);
        Assert.Equal(1, row.GetProperty("expected").GetInt32());

        var banksia = Assert.Single(Rows(progress, "participation"),
            p => p.GetProperty("houseCode").GetString() == "BANKSIA");
        Assert.Equal(4, banksia.GetProperty("competitors").GetInt32());
        Assert.Equal(4, banksia.GetProperty("entered").GetInt32());
    }

    /// <summary>
    /// House codes fold to ONE row whatever casing the roster carries: the
    /// database groups by its own collation, and two rows for one house is a
    /// duplicate-key throw rather than a display defect.
    /// </summary>
    [Fact]
    public async Task Roster_house_codes_differing_only_in_case_are_one_house()
    {
        using var factory = new ApiTestFactory();
        var admin = factory.CreateClientAs("case-admin", "CarnivalAdministrator");
        var seeded = await CarnivalSeed.SeedAsync(factory);
        await CarnivalSeed.MutateAsync(factory, db =>
        {
            var batchId = db.ImportBatches.First().Id;
            var student = new Student
            {
                Id = Guid.CreateVersion7(),
                ExternalStudentId = "CASE-Nina",
                Surname = "Olsen",
                FirstName = "Nina",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Students.Add(student);
            db.RosterSnapshots.Add(new RosterSnapshot
            {
                Id = Guid.CreateVersion7(),
                StudentId = student.Id,
                SchoolYear = 2027,
                YearLevel = 7,
                HouseCode = "Banksia",
                CompetitionAgeGroup = seeded.PrimaryAgeGroup,
                CompetitionCategory = CompetitionCategory.F,
                ImportBatchId = batchId,
                EffectiveDate = new DateOnly(2027, 1, 30),
                IsCurrent = true,
            });
        });

        var participation = Rows(await ProgressAsync(seeded.CarnivalId, admin), "participation");
        var banksia = Assert.Single(participation, p => string.Equals(
            p.GetProperty("houseCode").GetString(), "BANKSIA", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, banksia.GetProperty("competitors").GetInt32());
    }

    /// <summary>
    /// Nothing is clamped: a swimmer who entered and then left the roster is one
    /// more entrant than the house has competitors, and the count says so.
    /// </summary>
    [Fact]
    public async Task Entered_may_exceed_competitors_when_a_swimmer_leaves_the_roster()
    {
        using var factory = new ApiTestFactory();
        var admin = factory.CreateClientAs("left-admin", "CarnivalAdministrator");
        var seeded = await CarnivalSeed.SeedAsync(factory);
        var created = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await CarnivalSeed.MutateAsync(factory, db =>
            db.Students.Single(s => s.Id == seeded.EligibleBoyId).IsActive = false);

        var participation = Rows(await ProgressAsync(seeded.CarnivalId, admin), "participation");
        var banksia = Assert.Single(participation, p => p.GetProperty("houseCode").GetString() == "BANKSIA");
        Assert.Equal(0, banksia.GetProperty("competitors").GetInt32());
        Assert.Equal(1, banksia.GetProperty("entered").GetInt32());
    }

    [Fact]
    public async Task A_configured_house_with_no_students_is_on_both_cards_at_zero()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            var school = db.SchoolConfigurations.Single();
            school.HousesJson = SchoolSettings.SerialiseHouses(
                [new("BANKSIA", "Banksia", null), new("KOALA", "Koala", null)]);
        });

        var koala = Assert.Single(Rows(await ProgressAsync(seeded.CarnivalId), "participation"),
            p => p.GetProperty("houseCode").GetString() == "KOALA");
        Assert.Equal(0, koala.GetProperty("competitors").GetInt32());
        Assert.Equal(0, koala.GetProperty("entered").GetInt32());

        var standings = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/carnivals/{seeded.CarnivalId}/standings");
        var board = Assert.Single(standings.GetProperty("houses").EnumerateArray(),
            h => h.GetProperty("houseCode").GetString() == "KOALA");
        Assert.Equal(0, board.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_finalised_carnival_lists_the_same_houses_on_both_cards_without_one_configured_since()
    {
        var seeded = await World("SW-PROG-HOUSES", 31.2m).BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK,
            (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            var school = db.SchoolConfigurations.Single();
            school.HousesJson = SchoolSettings.SerialiseHouses(
                [.. Fixtures.Houses.Select(h => new HouseMark(h, h, null)), new("KOALA", "Koala", null)]);
        });

        var participation = Rows(await ProgressAsync(seeded.CarnivalId), "participation")
            .Select(p => p.GetProperty("houseCode").GetString()).Order().ToList();
        var standings = (await _admin.GetFromJsonAsync<JsonElement>(
                $"/api/v1/carnivals/{seeded.CarnivalId}/standings"))
            .GetProperty("houses").EnumerateArray()
            .Select(h => h.GetProperty("houseCode").GetString()).Order().ToList();

        Assert.Equal(standings, participation);
        Assert.Contains("BANKSIA", participation);
        Assert.DoesNotContain("KOALA", participation);
    }

    // ---- Records today ---------------------------------------------------

    private static string BoysAndGirls(params string[] bands) =>
        RecordsSeed.Categories(bands, CompetitionCategory.M, CompetitionCategory.F);

    private static RecordsSeed World(string key, decimal value) => new RecordsSeed()
        .Definition(key, BoysAndGirls("13"))
        .Division(key, "13", CompetitionCategory.M,
            new RecordsSeed.Swim("Aiden Brook", "BANKSIA", value));

    /// <summary>
    /// ADR-0043 §1 counts what beats or EQUALS the standing book, and ADR-0028
    /// seeds that book at the first finalisation — so an empty category has
    /// nothing to stand above and the whole first carnival is not a candidate.
    /// </summary>
    [Fact]
    public async Task A_swim_in_a_category_with_no_record_is_not_a_candidate()
    {
        var seeded = await World("SW-PROG-SEED", 31.2m).BuildAsync(_factory);
        Assert.Empty(Rows(await ProgressAsync(seeded.CarnivalId), "recordsToday"));
    }

    [Fact]
    public async Task An_open_or_category_less_division_is_never_a_candidate()
    {
        var first = await World("SW-PROG-CAT", 31.2m).BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        // ADR-0014 §9 / ADR-0028 ruling 2: the S and category-less divisions hold
        // no records, so their far faster swims stand beside the book, not above it.
        var later = await new RecordsSeed()
            .Definition("SW-PROG-CAT", BoysAndGirls("13"))
            .Division("SW-PROG-CAT", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 30.0m))
            .Division("SW-PROG-CAT", "13", CompetitionCategory.S,
                new RecordsSeed.Swim("Emery Frost", "GREVILLEA", 10.0m))
            .Division("SW-PROG-CAT", "13", (CompetitionCategory?)null,
                new RecordsSeed.Swim("Dale Egan", "MOUNTAIN ASH", 11.0m))
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));

        var candidate = Assert.Single(Rows(await ProgressAsync(later.CarnivalId), "recordsToday"));
        Assert.Equal("13 Boys", candidate.GetProperty("divisionLabel").GetString());
        Assert.Equal("30.00", candidate.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_faster_swim_is_new_and_an_equal_one_is_equalled_against_the_standing_book()
    {
        var first = await World("SW-PROG-BOOK", 31.2m).BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        var faster = await World("SW-PROG-BOOK", 30.0m)
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));
        var beat = Assert.Single(Rows(await ProgressAsync(faster.CarnivalId), "recordsToday"));
        Assert.Equal("new", beat.GetProperty("kind").GetString());
        Assert.Equal("31.20", beat.GetProperty("standing").GetString());
        Assert.Equal("30.00", beat.GetProperty("value").GetString());

        var tied = await World("SW-PROG-BOOK", 31.2m)
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2029, 3, 4));
        var equalled = Assert.Single(Rows(await ProgressAsync(tied.CarnivalId), "recordsToday"));
        Assert.Equal("equalled", equalled.GetProperty("kind").GetString());
        Assert.Equal("31.20", equalled.GetProperty("standing").GetString());

        // Nothing was written: only finalisation mints (ADR-0028 §1).
        var book = await _admin.GetAsync("/api/v1/records");
        var rows = (await book.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Where(r => r.GetProperty("eventCode").GetString() == first.DefinitionCodes["SW-PROG-BOOK"]);
        Assert.Equal(31.2m, Assert.Single(rows).GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task A_slower_swim_and_a_social_division_are_not_record_candidates()
    {
        var first = await World("SW-PROG-SLOW", 30.0m).BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        // ADR-0014 §9 / ADR-0028 ruling 2: Social never sets a record, and neither
        // does a slower swim in a category that already has one.
        var slower = await new RecordsSeed()
            .Definition("SW-PROG-SLOW", BoysAndGirls("13"))
            .Division("SW-PROG-SLOW", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 33.0m))
            .Division("SW-PROG-SLOW", "13", CompetitionCategory.F, EventTier.Social,
                new RecordsSeed.Swim("Cora Diaz", "WATTLE", 20.0m))
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));

        Assert.Empty(Rows(await ProgressAsync(slower.CarnivalId), "recordsToday"));
    }

    /// <summary>
    /// After finalisation the book holds this carnival's own records, so comparing against it
    /// would call every new record "equalled". The carnival reports what it set.
    /// </summary>
    [Fact]
    public async Task After_finalisation_a_record_this_carnival_set_still_reads_new()
    {
        var first = await World("SW-PROG-FINAL", 31.2m).BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);
        // The seeding carnival set nothing it stood above, before or after.
        Assert.Empty(Rows(await ProgressAsync(first.CarnivalId), "recordsToday"));

        var faster = await World("SW-PROG-FINAL", 30.0m)
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, faster.CarnivalId)).StatusCode);

        var progress = await ProgressAsync(faster.CarnivalId);
        Assert.False(progress.GetProperty("provisional").GetBoolean());
        var beat = Assert.Single(Rows(progress, "recordsToday"));
        Assert.Equal("new", beat.GetProperty("kind").GetString());
        Assert.Equal("31.20", beat.GetProperty("standing").GetString());
        Assert.Equal("30.00", beat.GetProperty("value").GetString());

        var tied = await World("SW-PROG-FINAL", 30.0m)
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2029, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, tied.CarnivalId)).StatusCode);
        var equalled = Assert.Single(Rows(await ProgressAsync(tied.CarnivalId), "recordsToday"));
        Assert.Equal("equalled", equalled.GetProperty("kind").GetString());
        Assert.Equal("30.00", equalled.GetProperty("standing").GetString());

        // A later carnival superseding it does not rewrite what this one set.
        var later = await World("SW-PROG-FINAL", 29.0m)
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2030, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, later.CarnivalId)).StatusCode);
        var kept = Assert.Single(Rows(await ProgressAsync(faster.CarnivalId), "recordsToday"));
        Assert.Equal("new", kept.GetProperty("kind").GetString());
        Assert.Equal("31.20", kept.GetProperty("standing").GetString());
    }

    // ---- Lifecycle and refusals ------------------------------------------

    [Fact]
    public async Task A_finalised_carnival_is_no_longer_provisional()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, CarnivalStatus.Finalised);
        Assert.False((await ProgressAsync(seeded.CarnivalId)).GetProperty("provisional").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_carnival_is_a_404()
    {
        var response = await _admin.GetAsync(ProgressPath(Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotFound);
    }
}
