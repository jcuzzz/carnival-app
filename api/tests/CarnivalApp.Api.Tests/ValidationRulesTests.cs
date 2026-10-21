using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0017 §2: the automatic validation rule set named by
/// <see cref="ValidationRules.Version"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class deliberately duplicates coverage that exists in
/// <c>ResultEndpointsTests</c> and <c>ResultValueParserTests</c>. Those describe
/// endpoint and parser BEHAVIOUR; this one describes an ADR RULE SET. The point of
/// the duplication is that relaxing a rule has to delete a test that says which ADR
/// clause is being relaxed — so it cannot happen as a quiet edit to a parser test.
/// </para>
/// <para>
/// Everything here is asserted through the HTTP surface, because ADR-0017 §2 is
/// about what the server refuses, not about what a helper returns.
/// </para>
/// <para>
/// <b>What is NOT pinned here, on purpose.</b> The <i>implausible</i> class warns and
/// never refuses (§2), and plausibility bands are deferred entirely until the pilot
/// generates the data (§3) — so there is no band to test. State-dependent refusals
/// (<c>CARNIVAL_NOT_OPEN</c>, <c>EVENT_NOT_OPEN</c>) are deliberately excluded: they
/// are "not valid yet" rather than "never valid", they stay retryable, and pinning
/// them here would invite someone to make them terminal.
/// </para>
/// </remarks>
public sealed class ValidationRulesTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _official;

    public ValidationRulesTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-1", "CarnivalAdministrator");
        _official = factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
    }

    private static string EventPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

    private async Task<Guid> EnterAsync(SeededCarnival seeded, Guid studentId)
    {
        var response = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/entries", new { studentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SubmitRawAsync(SeededCarnival seeded, Guid entryId, string rawBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        return _official.SendAsync(request);
    }

    /// <summary>
    /// Every ADR-0017 §2 "incoherent" rule that is buildable today refuses the
    /// capture, with the ADR-0011 machine code the client branches on.
    /// </summary>
    [Theory]
    // A measured value on a non-result status — §2 "a measured value on a DNS/DNF/DQ".
    [InlineData("""{"status":"DNS","value":"34.56"}""", ErrorCodes.ResultValueNotAllowed)]
    [InlineData("""{"status":"DNF","value":"34.56"}""", ErrorCodes.ResultValueNotAllowed)]
    [InlineData("""{"status":"DQ","value":"34.56"}""", ErrorCodes.ResultValueNotAllowed)]
    // A missing value on an Ok — §2 "a missing value on an Ok". Both shapes: an
    // explicit Ok with no value, and a body carrying neither.
    [InlineData("""{"status":"Ok"}""", ErrorCodes.ResultValueRequired)]
    [InlineData("{}", ErrorCodes.ResultValueRequired)]
    // Non-positive and unparseable measured values — §2 "a non-positive time".
    [InlineData("""{"value":"0"}""", ErrorCodes.ResultValueInvalid)]
    [InlineData("""{"value":"0:00.00"}""", ErrorCodes.ResultValueInvalid)]
    [InlineData("""{"value":"-34.56"}""", ErrorCodes.ResultValueInvalid)]
    [InlineData("""{"value":"not-a-time"}""", ErrorCodes.ResultValueInvalid)]
    // A status outside the modelled set is incoherent, not a typo to interpret.
    [InlineData("""{"status":"Retired"}""", ErrorCodes.UnknownResultStatus)]
    public async Task Incoherent_captures_are_refused_with_their_machine_code(string rawBody, string expectedCode)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await SubmitRawAsync(seeded, entryId, rawBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(expectedCode);
    }

    /// <summary>
    /// §2's "an entry that does not exist in this carnival". This is a real
    /// route-scoped rule, not a 404 that happens to look like one: the entry below
    /// exists and is perfectly valid — it simply belongs to another carnival.
    /// </summary>
    [Fact]
    public async Task An_entry_from_another_carnival_is_refused()
    {
        var mine = await CarnivalSeed.SeedAsync(_factory);
        var theirs = await CarnivalSeed.SeedAsync(_factory);
        var foreignEntryId = await EnterAsync(theirs, theirs.EligibleBoyId);

        // The foreign entry id, addressed through THIS carnival's event route.
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(mine)}/entries/{foreignEntryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.56" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        var response = await _official.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EntryNotFound);
    }

    /// <summary>
    /// ADR-0017 §1: a result is stamped with the version of the rule set that passed
    /// it. Until the state itself carries it, the audit event is where that lives —
    /// on the capture AND on a correction, which re-runs the same rules.
    /// </summary>
    [Fact]
    public async Task Capture_and_correction_both_record_the_validation_rule_version()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        var submit = new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.56" }),
            Headers = { { "Idempotency-Key", key.ToString() } },
        };
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(submit)).StatusCode);

        var correction = await _official.PostAsJsonAsync(
            $"/api/v1/results/{key}/correct",
            new { expectedRevision = 1, value = "35.00", reason = "Timekeeper misread the board." });
        Assert.Equal(HttpStatusCode.OK, correction.StatusCode);

        var audits = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .Where(a => a.EntityId == key.ToString())
            .ToListAsync());

        var captured = Assert.Single(audits, a => a.Action == AuditActions.ResultSubmit);
        var corrected = Assert.Single(audits, a => a.Action == AuditActions.ResultCorrect);
        Assert.Equal(ValidationRules.Version, captured.RuleVersion);
        Assert.Equal(ValidationRules.Version, corrected.RuleVersion);
    }
}
