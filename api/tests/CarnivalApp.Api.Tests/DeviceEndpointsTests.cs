using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Per-device queue visibility (ADR-0034): the upsert, its bounds, and the fact
/// that a device becomes known by reporting rather than by being registered.
/// </summary>
public sealed class DeviceEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public DeviceEndpointsTests(ApiTestFactory factory) => _factory = factory;

    private static string SyncStatusPath(Guid carnivalId, string deviceId) =>
        $"/api/v1/carnivals/{carnivalId}/devices/{deviceId}/sync-status";

    private static object Report(
        int pending, int parked = 0, string? oldest = null, string? appVersion = null) =>
        new { pending, parked, oldestPendingCapturedAt = oldest, appVersion };

    /// <summary>
    /// A report as the app sends one: the device id in the route AND in the
    /// caller's own X-Device-Id header, which the endpoint requires to agree.
    /// <paramref name="assertedDeviceId"/> is the forging seam.
    /// </summary>
    private static Task<HttpResponseMessage> ReportAsync(
        HttpClient client, Guid carnivalId, string deviceId, object body,
        string? assertedDeviceId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, SyncStatusPath(carnivalId, deviceId))
        {
            Content = JsonContent.Create(body),
        };
        var asserted = assertedDeviceId ?? Uri.UnescapeDataString(deviceId);
        if (asserted.Length > 0) request.Headers.Add("X-Device-Id", asserted);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> FleetAsync(HttpClient client, Guid carnivalId)
    {
        var response = await client.GetAsync($"/api/v1/carnivals/{carnivalId}/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement[]> DevicesAsync(HttpClient client, Guid carnivalId) =>
        (await FleetAsync(client, carnivalId)).GetProperty("devices").EnumerateArray().ToArray();

    [Fact]
    public async Task A_device_becomes_known_by_reporting_there_is_no_registration_step()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var admin = _factory.CreateClientAs("admin-known", "CarnivalAdministrator");
        Assert.Empty(await DevicesAsync(admin, seeded.CarnivalId));

        var marshal = _factory.CreateClientAs("marshal-known", "Recorder");
        // Relative, not a wall date: a fixed future timestamp would be clamped.
        var oldest = DateTimeOffset.UtcNow.AddMinutes(-20);
        var report = await ReportAsync(marshal, seeded.CarnivalId, "device-known-1",
            Report(pending: 3, parked: 1, oldest: oldest.ToString("O"), appVersion: "2027.1.4"));
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        var devices = await DevicesAsync(admin, seeded.CarnivalId);
        var device = Assert.Single(devices);
        Assert.Equal("device-known-1", device.GetProperty("deviceId").GetString());
        Assert.Equal(3, device.GetProperty("pendingCount").GetInt32());
        Assert.Equal(1, device.GetProperty("parkedCount").GetInt32());
        Assert.Equal("marshal-known", device.GetProperty("lastSubject").GetString());
        Assert.Equal("2027.1.4", device.GetProperty("appVersion").GetString());
        Assert.Equal(
            oldest, device.GetProperty("oldestPendingCapturedAtUtc").GetDateTimeOffset());
        // Nothing has drained yet, so nothing is claimed to have.
        Assert.Equal(JsonValueKind.Null, device.GetProperty("lastDrainUtc").ValueKind);
    }

    [Fact]
    public async Task A_second_report_overwrites_the_first_last_write_wins()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-lww", "Recorder");

        Assert.Equal(HttpStatusCode.OK,
            (await ReportAsync(marshal, seeded.CarnivalId, "device-lww", Report(6))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ReportAsync(marshal, seeded.CarnivalId, "device-lww", Report(0))).StatusCode);

        var admin = _factory.CreateClientAs("admin-lww", "CarnivalAdministrator");
        var device = Assert.Single(await DevicesAsync(admin, seeded.CarnivalId));
        Assert.Equal(0, device.GetProperty("pendingCount").GetInt32());
        // The count FELL, so a drain delivered something between the two reports.
        Assert.NotEqual(JsonValueKind.Null, device.GetProperty("lastDrainUtc").ValueKind);
    }

    [Fact]
    public async Task A_backlog_that_never_moves_never_claims_a_drain()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-stuck", "Recorder");

        await ReportAsync(marshal, seeded.CarnivalId, "device-stuck", Report(4));
        await ReportAsync(marshal, seeded.CarnivalId, "device-stuck", Report(4));
        await ReportAsync(marshal, seeded.CarnivalId, "device-stuck", Report(5));

        var admin = _factory.CreateClientAs("admin-stuck", "CarnivalAdministrator");
        var device = Assert.Single(await DevicesAsync(admin, seeded.CarnivalId));
        Assert.Equal(JsonValueKind.Null, device.GetProperty("lastDrainUtc").ValueKind);
    }

    [Fact]
    public async Task The_handover_case_the_desk_needs_the_row_names_whoever_reported_last()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ReportAsync(_factory.CreateClientAs("marshal-first", "Recorder"),
            seeded.CarnivalId, "device-handover", Report(2));
        await ReportAsync(_factory.CreateClientAs("marshal-second", "Recorder"),
            seeded.CarnivalId, "device-handover", Report(2));

        var admin = _factory.CreateClientAs("admin-handover", "CarnivalAdministrator");
        var device = Assert.Single(await DevicesAsync(admin, seeded.CarnivalId));
        Assert.Equal("marshal-second", device.GetProperty("lastSubject").GetString());
    }

    [Fact]
    public async Task Devices_carrying_work_sort_ahead_of_quiet_ones()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-sort", "Recorder");
        await ReportAsync(marshal, seeded.CarnivalId, "device-quiet", Report(0));
        await ReportAsync(marshal, seeded.CarnivalId, "device-loaded", Report(0, parked: 2));

        var admin = _factory.CreateClientAs("admin-sort", "CarnivalAdministrator");
        var devices = await DevicesAsync(admin, seeded.CarnivalId);
        Assert.Equal(2, devices.Length);
        Assert.Equal("device-loaded", devices[0].GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task One_carnivals_devices_never_appear_under_another()
    {
        var first = await CarnivalSeed.SeedAsync(_factory);
        var second = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-scope", "Recorder");
        await ReportAsync(marshal, first.CarnivalId, "device-scoped", Report(1));

        var admin = _factory.CreateClientAs("admin-scope", "CarnivalAdministrator");
        Assert.Single(await DevicesAsync(admin, first.CarnivalId));
        Assert.Empty(await DevicesAsync(admin, second.CarnivalId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("device id with spaces")]
    [InlineData("device/../escape")]
    [InlineData("device%0Aforged")]
    public async Task A_malformed_device_id_is_refused_in_the_contract_shape(string deviceId)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-badid", "Recorder");
        var response = await ReportAsync(
            marshal, seeded.CarnivalId, Uri.EscapeDataString(deviceId), Report(1));
        // An empty segment does not match the route at all; everything else is
        // the bounded refusal. Either way, no row is created.
        if (response.StatusCode == HttpStatusCode.NotFound && deviceId.Length == 0) return;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.DeviceIdInvalid);
    }

    [Fact]
    public async Task An_over_long_device_id_is_refused()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-longid", "Recorder");
        var tooLong = new string('a', DeviceEndpoints.MaxDeviceIdLength + 1);
        var response = await ReportAsync(marshal, seeded.CarnivalId, tooLong, Report(1));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.DeviceIdInvalid);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(DeviceEndpoints.MaxReportedCount + 1, 0)]
    public async Task An_impossible_count_is_refused(int pending, int parked)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-counts", "Recorder");
        var response = await ReportAsync(
            marshal, seeded.CarnivalId, "device-counts", Report(pending, parked));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task An_unknown_carnival_is_a_404_not_a_silently_orphaned_row()
    {
        var marshal = _factory.CreateClientAs("marshal-nocarnival", "Recorder");
        var response = await ReportAsync(marshal, Guid.NewGuid(), "device-orphan", Report(1));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotFound);

        var admin = _factory.CreateClientAs("admin-nocarnival", "CarnivalAdministrator");
        var listed = await admin.GetAsync($"/api/v1/carnivals/{Guid.NewGuid()}/devices");
        Assert.Equal(HttpStatusCode.NotFound, listed.StatusCode);
        await listed.AssertErrorAsync(ErrorCodes.CarnivalNotFound);
    }

    [Fact]
    public async Task An_over_long_app_version_is_truncated_rather_than_refusing_the_report()
    {
        // Cosmetic fields never cost visibility: a refused report is a device the
        // organiser cannot see at all, which is the failure this endpoint exists
        // to prevent (ADR-0034 §2).
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-version", "Recorder");
        var response = await ReportAsync(marshal, seeded.CarnivalId, "device-version",
            Report(1, appVersion: new string('v', DeviceEndpoints.MaxAppVersionLength + 40)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var admin = _factory.CreateClientAs("admin-version", "CarnivalAdministrator");
        var device = Assert.Single(await DevicesAsync(admin, seeded.CarnivalId));
        Assert.Equal(
            DeviceEndpoints.MaxAppVersionLength,
            device.GetProperty("appVersion").GetString()!.Length);
    }

    [Fact]
    public async Task Reporting_writes_no_audit_row_it_is_telemetry_not_a_record()
    {
        // One device reports for a whole day; the §9.3 table must not fill with it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-audit", "Recorder");
        var before = await CarnivalSeed.QueryAsync(_factory, db =>
            Task.FromResult(db.AuditEvents.Count()));

        for (var i = 0; i < 5; i++)
            await ReportAsync(marshal, seeded.CarnivalId, "device-audit", Report(i));

        var after = await CarnivalSeed.QueryAsync(_factory, db =>
            Task.FromResult(db.AuditEvents.Count()));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Finalisation_is_never_blocked_by_an_undrained_device()
    {
        // ADR-0034 §4: an offline device and a lost one look identical from here,
        // so the fleet informs a WARNING on the organiser's screen and never a
        // server refusal — ADR-0006's audited override has to keep working.
        var seeded = await CarnivalSeed.SeedAsync(_factory, carnivalStatus: CarnivalStatus.Open);
        var marshal = _factory.CreateClientAs("marshal-final", "Recorder");
        await ReportAsync(marshal, seeded.CarnivalId, "device-lost", Report(9));

        var admin = _factory.CreateClientAs("admin-final", "CarnivalAdministrator");
        var closed = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var finalised = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status",
            new { status = "Finalised", reason = "Published with one device unaccounted for" });
        Assert.Equal(HttpStatusCode.OK, finalised.StatusCode);
    }

    [Fact]
    public async Task One_device_cannot_report_a_queue_on_anothers_behalf()
    {
        // The spoof this closes: reporting is carnivals:view, so without the
        // header check ANY signed-in role could zero a stranded device's row and
        // erase the warning beside Finalise (ADR-0034 §2).
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-spoof", "Recorder");
        await ReportAsync(marshal, seeded.CarnivalId, "device-stranded", Report(7));

        var viewer = _factory.CreateClientAs("viewer-spoof", "Viewer");
        var forged = await ReportAsync(
            viewer, seeded.CarnivalId, "device-stranded", Report(0),
            assertedDeviceId: "device-of-the-forger");
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        await forged.AssertErrorAsync(ErrorCodes.DeviceIdMismatch);

        var admin = _factory.CreateClientAs("admin-spoof", "CarnivalAdministrator");
        var device = Assert.Single(await DevicesAsync(admin, seeded.CarnivalId));
        Assert.Equal(7, device.GetProperty("pendingCount").GetInt32());
    }

    [Fact]
    public async Task A_report_with_no_device_header_is_refused()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-noheader", "Recorder");
        var response = await marshal.PutAsJsonAsync(
            SyncStatusPath(seeded.CarnivalId, "device-noheader"), Report(1));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.DeviceIdMismatch);
    }

    [Fact]
    public async Task A_capture_stamped_in_the_future_is_clamped_to_now()
    {
        // A device with a fast clock would otherwise report an age the panel
        // renders as no age at all.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var marshal = _factory.CreateClientAs("marshal-clock", "Recorder");
        var future = DateTimeOffset.UtcNow.AddHours(3);
        await ReportAsync(marshal, seeded.CarnivalId, "device-clock",
            Report(2, oldest: future.ToString("O")));

        var admin = _factory.CreateClientAs("admin-clock", "CarnivalAdministrator");
        var device = Assert.Single(await DevicesAsync(admin, seeded.CarnivalId));
        Assert.True(
            device.GetProperty("oldestPendingCapturedAtUtc").GetDateTimeOffset() < future);
    }

    [Fact]
    public async Task The_fleet_carries_the_servers_own_clock_so_ages_survive_a_skewed_desk()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var admin = _factory.CreateClientAs("admin-clock-read", "CarnivalAdministrator");
        var fleet = await FleetAsync(admin, seeded.CarnivalId);
        var serverNow = fleet.GetProperty("serverNowUtc").GetDateTimeOffset();
        Assert.True((DateTimeOffset.UtcNow - serverNow).Duration() < TimeSpan.FromMinutes(1));
    }
}
