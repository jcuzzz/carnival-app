using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// GET/POST/DELETE /api/v1/admin/role-assignments (ADR-0048 §7): who may
/// administer roles, what each write refuses, and that a grant or revocation is
/// in effect on the target's NEXT request rather than their next sign-in.
/// </summary>
public sealed class RoleAssignmentEndpointTests : IDisposable
{
    private const string Path = "/api/v1/admin/role-assignments";

    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public RoleAssignmentEndpointTests() =>
        _admin = _factory.CreateClientAs("sys-1", "SystemAdministrator");

    public void Dispose() => _factory.Dispose();

    private static object Grant(
        string? upn = "coach@school.test",
        string? role = "Recorder",
        string? reason = "Timing desk for the swimming carnival.") => new { upn, role, reason };

    private async Task<JsonElement> ListAsync()
    {
        var response = await _admin.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<Guid> GrantedAsync(string upn = "coach@school.test", string role = "Recorder")
    {
        var response = await _admin.PostAsJsonAsync(Path, Grant(upn, role));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> RevokeAsync(
        HttpClient client, Guid id, string? reason = "Left the school.")
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{Path}/{id}")
        {
            Content = JsonContent.Create(new { reason }),
        };
        return await client.SendAsync(request);
    }

    private async Task<List<AuditEvent>> AuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        return await db.AuditEvents.AsNoTracking().Where(e => e.Action == action).ToListAsync();
    }

    private async Task<RoleAssignment> RowAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        return await db.RoleAssignments.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private static RoleAssignment Row(string upn, PlatformRole role, string? subjectId = null) => new()
    {
        Id = Guid.NewGuid(),
        SubjectId = subjectId,
        SubjectUpn = upn,
        Role = role,
        Reason = "Seeded directly.",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        CreatedBySubject = "dba",
    };

    /// <summary>Rows written past the endpoint, for the states it cannot itself create.</summary>
    private async Task SeedAsync(params RoleAssignment[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        db.RoleAssignments.AddRange(rows);
        await db.SaveChangesAsync();
        _factory.Services.GetRequiredService<RoleAssignmentProvider>().Invalidate();
    }

    // ---- The policy -------------------------------------------------------

    [Fact]
    public async Task A_carnival_administrator_cannot_administer_roles()
    {
        var client = _factory.CreateClientAs("carnival-admin", "CarnivalAdministrator");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public void Roles_administer_is_its_own_policy_over_school_administer()
    {
        // Both are SystemAdministrator-only today; the point is that they are two
        // registered policies, so widening one cannot widen the other (§7).
        Assert.NotEqual(
            CarnivalApp.Api.School.SchoolAuthorization.AdministerSchoolPolicy,
            RoleAuthorization.AdministerRolesPolicy);
        Assert.Equal([PlatformRole.SystemAdministrator],
            RoleAuthorization.PolicyRoles[RoleAuthorization.AdministerRolesPolicy]);
    }

    // ---- GET --------------------------------------------------------------

    [Fact]
    public async Task The_list_is_empty_before_anything_is_granted()
    {
        Assert.Equal(0, (await ListAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_granted_row_is_listed_unbound_until_the_person_signs_in()
    {
        await GrantedAsync();
        var row = (await ListAsync()).EnumerateArray().Single();
        Assert.Equal("coach@school.test", row.GetProperty("upn").GetString());
        Assert.Equal("Recorder", row.GetProperty("role").GetString());
        Assert.False(row.GetProperty("boundToSubject").GetBoolean());
        Assert.False(row.GetProperty("bootstrap").GetBoolean());
        Assert.Equal("sys-1", row.GetProperty("assignedBySubject").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("revokedAtUtc").ValueKind);
    }

    [Fact]
    public async Task A_revoked_row_stays_in_the_list_as_its_own_history()
    {
        var id = await GrantedAsync();
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, id)).StatusCode);

        var row = (await ListAsync()).EnumerateArray().Single();
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("revokedAtUtc").ValueKind);
        Assert.Equal("sys-1", row.GetProperty("revokedBySubject").GetString());
        Assert.Equal("Left the school.", row.GetProperty("revocationReason").GetString());
    }

    // ---- POST -------------------------------------------------------------

    [Fact]
    public async Task A_grant_is_audited_with_target_role_and_reason()
    {
        var id = await GrantedAsync();
        var audit = Assert.Single(await AuditAsync(AuditActions.RoleAssignmentGrant));
        Assert.Equal("sys-1", audit.ActorSubject);
        Assert.Equal("SystemAdministrator", audit.ActorRole);
        Assert.Equal(id.ToString(), audit.EntityId);
        Assert.Contains("coach@school.test", audit.NewValuesJson);
        Assert.Contains("Recorder", audit.NewValuesJson);
    }

    [Fact]
    public async Task The_upn_is_stored_lower_case_so_a_case_only_retry_is_a_duplicate()
    {
        await GrantedAsync("Coach@School.test");
        Assert.Equal("coach@school.test", (await ListAsync()).EnumerateArray().Single()
            .GetProperty("upn").GetString());

        var again = await _admin.PostAsJsonAsync(Path, Grant("COACH@SCHOOL.TEST"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.RoleAssignmentDuplicate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@school.test")]
    [InlineData("coach@")]
    [InlineData("two@at@school.test")]
    [InlineData("has space@school.test")]
    public async Task An_unusable_upn_is_refused(string? upn)
    {
        var response = await _admin.PostAsJsonAsync(Path, Grant(upn));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleUpnInvalid);
    }

    [Theory]
    [InlineData("NotARole")]
    [InlineData("")]
    [InlineData(null)]
    // Enum.TryParse accepts the numeric form and the numbers MOVED in the
    // four-role rename — the guard matters more now a role name can arrive from
    // a table (ADR-0048 consequences).
    [InlineData("3")]
    [InlineData("-1")]
    public async Task An_unknown_role_is_refused(string? role)
    {
        var response = await _admin.PostAsJsonAsync(Path, Grant(role: role));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleUnknown);
    }

    [Fact]
    public async Task A_retired_role_name_resolves_through_the_same_alias_table()
    {
        var response = await _admin.PostAsJsonAsync(Path, Grant(role: "ResultsOfficial"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Recorder", body.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_grant_without_a_reason_is_refused(string? reason)
    {
        var response = await _admin.PostAsJsonAsync(Path, Grant(reason: reason));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleReasonRequired);
    }

    [Fact]
    public async Task A_reason_past_the_column_bound_is_refused_rather_than_truncated()
    {
        var response = await _admin.PostAsJsonAsync(
            Path, Grant(reason: new string('x', RoleAssignmentLimits.MaxReasonLength + 1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleReasonRequired);
    }

    [Fact]
    public async Task A_second_active_grant_of_the_same_role_is_refused()
    {
        await GrantedAsync();
        var again = await _admin.PostAsJsonAsync(Path, Grant());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.RoleAssignmentDuplicate);
    }

    [Fact]
    public async Task A_second_grant_of_a_DIFFERENT_role_to_the_same_person_is_allowed()
    {
        await GrantedAsync();
        Assert.Equal(HttpStatusCode.Created,
            (await _admin.PostAsJsonAsync(Path, Grant(role: "Viewer"))).StatusCode);
        Assert.Equal(2, (await ListAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_role_may_be_granted_again_after_it_was_revoked()
    {
        var id = await GrantedAsync();
        await RevokeAsync(_admin, id);
        // The unique index the initial schema carried would have refused this;
        // rows are never deleted, so the constraint could not stay (§2).
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(Path, Grant())).StatusCode);
    }

    // ---- DELETE -----------------------------------------------------------

    [Fact]
    public async Task Revoking_an_assignment_that_does_not_exist_is_404()
    {
        var response = await RevokeAsync(_admin, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleAssignmentNotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Revoking_without_a_reason_is_refused(string? reason)
    {
        var id = await GrantedAsync();
        var response = await RevokeAsync(_admin, id, reason);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleReasonRequired);
        Assert.Null((await RowAsync(id)).RevokedAtUtc);
    }

    [Fact]
    public async Task A_revocation_stamps_the_row_and_is_audited()
    {
        var id = await GrantedAsync();
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, id)).StatusCode);

        var row = await RowAsync(id);
        Assert.NotNull(row.RevokedAtUtc);
        Assert.Equal("sys-1", row.RevokedBySubject);
        Assert.Equal("Left the school.", row.RevocationReason);

        var audit = Assert.Single(await AuditAsync(AuditActions.RoleAssignmentRevoke));
        Assert.Equal(id.ToString(), audit.EntityId);
        Assert.Contains("Left the school.", audit.NewValuesJson);
    }

    [Fact]
    public async Task Revoking_twice_is_idempotent_and_records_one_revocation()
    {
        var id = await GrantedAsync();
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, id)).StatusCode);
        var first = (await RowAsync(id)).RevokedAtUtc;

        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, id, "Again.")).StatusCode);
        Assert.Equal(first, (await RowAsync(id)).RevokedAtUtc);
        Assert.Single(await AuditAsync(AuditActions.RoleAssignmentRevoke));
    }

    [Fact]
    public async Task You_cannot_revoke_your_own_system_administrator()
    {
        var id = await GrantedAsync("sys-boss@school.test", "SystemAdministrator");
        var self = _factory.CreateClientAs("sys-boss@school.test", "SystemAdministrator");
        var response = await RevokeAsync(self, id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleSelfRevoke);
        Assert.Null((await RowAsync(id)).RevokedAtUtc);
    }

    [Fact]
    public async Task Another_system_administrator_may_revoke_yours()
    {
        var id = await GrantedAsync("sys-boss@school.test", "SystemAdministrator");
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, id)).StatusCode);
    }

    [Fact]
    public async Task You_may_revoke_your_own_NON_administrator_role()
    {
        // The rule is about locking the deployment out, not about self-service.
        var id = await GrantedAsync("sys-1@school.test", "Recorder");
        var self = _factory.CreateClientAs("sys-1@school.test", "SystemAdministrator");
        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(self, id)).StatusCode);
    }

    // ---- Revocation takes the whole person, not one row -------------------

    [Fact]
    public async Task Revoking_one_row_revokes_every_active_row_for_the_same_subject_and_role()
    {
        // A UPN rename between grants: two active rows, one subject, one role.
        // Revoking either has to take the role away, or it takes nothing away.
        var before = Row("old@school.test", PlatformRole.Recorder, "oid-1");
        var after = Row("new@school.test", PlatformRole.Recorder, "oid-1");
        await SeedAsync(before, after);

        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, after.Id)).StatusCode);

        Assert.NotNull((await RowAsync(before.Id)).RevokedAtUtc);
        Assert.NotNull((await RowAsync(after.Id)).RevokedAtUtc);
        Assert.Equal(2, (await AuditAsync(AuditActions.RoleAssignmentRevoke)).Count);
    }

    [Fact]
    public async Task Revoking_leaves_a_DIFFERENT_role_for_the_same_person_alone()
    {
        var recorder = Row("coach@school.test", PlatformRole.Recorder, "oid-1");
        var viewer = Row("coach@school.test", PlatformRole.Viewer, "oid-1");
        await SeedAsync(recorder, viewer);

        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, recorder.Id)).StatusCode);

        Assert.NotNull((await RowAsync(recorder.Id)).RevokedAtUtc);
        Assert.Null((await RowAsync(viewer.Id)).RevokedAtUtc);
    }

    [Fact]
    public async Task A_rename_pair_loses_the_role_outright_on_the_targets_next_request()
    {
        var before = Row("old@school.test", PlatformRole.Recorder, "coach-oid");
        var after = Row("new@school.test", PlatformRole.Recorder, "coach-oid");
        await SeedAsync(before, after);

        Assert.Equal(HttpStatusCode.OK, (await RevokeAsync(_admin, before.Id)).StatusCode);

        var coach = _factory.CreateClientAs("coach-oid");
        var me = await coach.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Empty(JsonDocument.Parse(await me.Content.ReadAsStringAsync())
            .RootElement.GetProperty("roles").EnumerateArray());
    }

    // ---- The active-row uniqueness index ----------------------------------

    [Fact]
    public void Uniqueness_over_the_ACTIVE_rows_is_a_filtered_index_on_upn_and_role()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var index = Assert.Single(
            db.Model.FindEntityType(typeof(RoleAssignment))!.GetIndexes(), i => i.IsUnique);
        Assert.Equal(["SubjectUpn", "Role"], index.Properties.Select(p => p.Name));
        Assert.Equal("\"RevokedAtUtc\" IS NULL", index.GetFilter());
    }

    [Fact]
    public async Task The_database_refuses_a_second_ACTIVE_row_for_the_same_upn_and_role()
    {
        // The endpoint's duplicate check races with itself; this is the guard the
        // GrantAsync DbUpdateException catch turns back into a 409.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        db.RoleAssignments.AddRange(
            Row("clash@school.test", PlatformRole.Recorder),
            Row("clash@school.test", PlatformRole.Recorder));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_grant_over_a_row_written_outside_the_endpoint_is_a_duplicate()
    {
        await SeedAsync(Row("coach@school.test", PlatformRole.Recorder));
        var response = await _admin.PostAsJsonAsync(Path, Grant());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RoleAssignmentDuplicate);
    }
}
