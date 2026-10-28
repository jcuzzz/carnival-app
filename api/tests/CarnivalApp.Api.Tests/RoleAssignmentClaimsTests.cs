using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The union of the three role sources over HTTP (ADR-0048 §2, §3, §5, §6):
/// group mapping, in-app assignments and the bootstrap configuration list; the
/// UPN binding on first authenticated request; and "in effect on the NEXT
/// request", which is the whole reason the transformation is per-request.
/// </summary>
public sealed class RoleAssignmentClaimsTests
{
    private const string Path = "/api/v1/admin/role-assignments";

    /// <summary>A host whose only configured administrator is a bootstrap entry.</summary>
    private static ApiTestFactory Bootstrapped(params string[] entries)
    {
        var factory = new ApiTestFactory();
        for (var i = 0; i < entries.Length; i++)
            factory.With($"{GroupToRoleClaimsTransformation.BootstrapAdministratorsConfig}:{i}", entries[i]);
        return factory;
    }

    private static async Task<HashSet<string>> RolesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToHashSet();
    }

    private static async Task<Guid> GrantAsync(HttpClient admin, string upn, string role)
    {
        var response = await admin.PostAsJsonAsync(Path, new { upn, role, reason = "Carnival setup." });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
    }

    // ---- Bootstrap administrators (§3) ------------------------------------

    [Fact]
    public async Task A_bootstrap_upn_holds_system_administrator_with_no_row_and_no_group()
    {
        using var factory = Bootstrapped("principal@school.test");
        var client = factory.CreateClientAs("principal@school.test");
        Assert.Contains("SystemAdministrator", await RolesAsync(client));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task A_bootstrap_entry_may_be_a_raw_subject_id()
    {
        using var factory = Bootstrapped("00000000-oid-of-the-principal");
        var client = factory.CreateClientAs("00000000-oid-of-the-principal");
        Assert.Contains("SystemAdministrator", await RolesAsync(client));
    }

    [Fact]
    public async Task Anybody_else_is_unaffected_by_the_bootstrap_list()
    {
        using var factory = Bootstrapped("principal@school.test");
        Assert.Empty(await RolesAsync(factory.CreateClientAs("nobody@school.test")));
    }

    [Fact]
    public async Task Bootstrap_administrators_are_listed_read_only_and_cannot_be_revoked()
    {
        using var factory = Bootstrapped("principal@school.test");
        var client = factory.CreateClientAs("principal@school.test");
        var body = JsonDocument.Parse(await (await client.GetAsync(Path)).Content.ReadAsStringAsync())
            .RootElement;
        var row = body.EnumerateArray().Single();
        Assert.True(row.GetProperty("bootstrap").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("id").ValueKind);
        Assert.Equal("principal@school.test", row.GetProperty("upn").GetString());
        Assert.Equal("SystemAdministrator", row.GetProperty("role").GetString());
    }

    // ---- Union (§2) -------------------------------------------------------

    [Fact]
    public async Task A_group_derived_role_and_an_assigned_role_union()
    {
        using var factory = new ApiTestFactory()
            .With("Identity:GroupToRoleMappings:G-RECORDERS", "Recorder");
        var admin = factory.CreateClientAs("sys-1", "SystemAdministrator");
        await GrantAsync(admin, "both@school.test", "CarnivalAdministrator");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "both@school.test");
        client.DefaultRequestHeaders.Add(DevAuthentication.GroupsHeader, "G-RECORDERS");

        var roles = await RolesAsync(client);
        Assert.Contains("Recorder", roles);
        Assert.Contains("CarnivalAdministrator", roles);
    }

    [Fact]
    public async Task Assignments_alone_carry_a_deployment_with_no_group_mapping_at_all()
    {
        // The department-tenant case the ADR exists for: no groups claim, no
        // Identity:GroupToRoleMappings, roles from the table.
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        await GrantAsync(admin, "recorder@school.test", "Recorder");

        Assert.Equal(["Recorder"], await RolesAsync(factory.CreateClientAs("recorder@school.test")));
    }

    // ---- Binding (§5) -----------------------------------------------------

    [Fact]
    public async Task The_first_authenticated_request_binds_the_row_and_audits_it()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        var id = await GrantAsync(admin, "coach@school.test", "Recorder");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "coach-oid-1");
        client.DefaultRequestHeaders.Add(DevAuthentication.UpnHeader, "Coach@School.test");
        Assert.Contains("Recorder", await RolesAsync(client));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var row = await db.RoleAssignments.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal("coach-oid-1", row.SubjectId);

        var audit = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditActions.RoleAssignmentBind).ToListAsync();
        var bind = Assert.Single(audit);
        Assert.Equal("system", bind.ActorSubject);
        Assert.Equal(id.ToString(), bind.EntityId);
        Assert.Contains("coach-oid-1", bind.NewValuesJson);
    }

    [Fact]
    public async Task A_bound_row_is_never_re_matched_on_its_upn()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        await GrantAsync(admin, "coach@school.test", "Recorder");

        var first = factory.CreateClient();
        first.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "coach-oid-1");
        first.DefaultRequestHeaders.Add(DevAuthentication.UpnHeader, "coach@school.test");
        Assert.Contains("Recorder", await RolesAsync(first));

        // A UPN reassigned to a different person after binding grants nothing:
        // an oid is stable, a UPN is renamed (§5).
        var second = factory.CreateClient();
        second.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "someone-else-oid");
        second.DefaultRequestHeaders.Add(DevAuthentication.UpnHeader, "coach@school.test");
        Assert.Empty(await RolesAsync(second));

        // And the original keeps it by subject id even if the UPN moves on.
        Assert.Contains("Recorder", await RolesAsync(first));
    }

    [Fact]
    public async Task Binding_happens_once_and_leaves_one_audit_row()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        await GrantAsync(admin, "coach@school.test", "Recorder");

        var client = factory.CreateClientAs("coach@school.test");
        await RolesAsync(client);
        await RolesAsync(client);
        await RolesAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.RoleAssignmentBind));
    }

    [Fact]
    public async Task A_subject_with_no_upn_claim_binds_nothing()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        await GrantAsync(admin, "coach@school.test", "Recorder");

        // A bare opaque subject — no "@", so the dev scheme emits no UPN claim.
        Assert.Empty(await RolesAsync(factory.CreateClientAs("opaque-oid")));
    }

    // ---- Effect is immediate (§6) -----------------------------------------

    [Fact]
    public async Task A_grant_is_in_effect_on_the_targets_next_request()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        var coach = factory.CreateClientAs("coach@school.test");

        Assert.Empty(await RolesAsync(coach));
        await GrantAsync(admin, "coach@school.test", "Recorder");
        // Same client, same cookie-equivalent session: no re-sign-in.
        Assert.Contains("Recorder", await RolesAsync(coach));
    }

    [Fact]
    public async Task A_revocation_is_in_effect_on_the_targets_next_request()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        var id = await GrantAsync(admin, "deputy@school.test", "SystemAdministrator");

        var deputy = factory.CreateClientAs("deputy@school.test");
        Assert.Equal(HttpStatusCode.OK, (await deputy.GetAsync(Path)).StatusCode);

        var revoke = new HttpRequestMessage(HttpMethod.Delete, $"{Path}/{id}")
        {
            Content = JsonContent.Create(new { reason = "Carnival is over." }),
        };
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(revoke)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await deputy.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public async Task A_row_written_outside_the_endpoint_grants_once_the_cache_is_invalidated()
    {
        // The provider caches; "immediate" (§6) is only true because every write
        // path invalidates it synchronously.
        using var factory = Bootstrapped("principal@school.test");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(),
                SubjectUpn = "helper@school.test",
                Role = PlatformRole.Viewer,
                Reason = "Hand-written row.",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "dba",
            });
            await db.SaveChangesAsync();
        }
        factory.Services.GetRequiredService<RoleAssignmentProvider>().Invalidate();

        Assert.Equal(["Viewer"], await RolesAsync(factory.CreateClientAs("helper@school.test")));
    }

    // ---- A display name is not an account identifier (§5) -----------------

    private sealed class Rows(params ActiveAssignment[] rows) : IRoleAssignmentSource
    {
        public List<Guid> Bound { get; } = [];

        public Task<IReadOnlyList<ActiveAssignment>> GetActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveAssignment>>(rows);

        public Task BindAsync(
            IReadOnlyList<Guid> assignmentIds, string subjectId, string upn, string? displayName,
            CancellationToken ct = default)
        {
            Bound.AddRange(assignmentIds);
            return Task.CompletedTask;
        }
    }

    private static async Task<HashSet<string>> TransformAsync(
        IRoleAssignmentSource source, IConfiguration configuration, params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var transformation = new GroupToRoleClaimsTransformation(
            configuration, NullLogger<GroupToRoleClaimsTransformation>.Instance, source);
        return (await transformation.TransformAsync(principal))
            .FindAll(PlatformClaims.Role).Select(c => c.Value).ToHashSet();
    }

    private static IConfiguration NoConfiguration() => new ConfigurationBuilder().Build();

    [Fact]
    public async Task A_principal_with_no_stable_subject_is_granted_but_never_bound()
    {
        // The IdP emits a UPN and a display name and no oid/sub. The grant still
        // matches by UPN; writing the display name into SubjectId would make it
        // an account identifier, which it is not.
        var source = new Rows(
            new ActiveAssignment(Guid.NewGuid(), null, "jane@school.test", PlatformRole.Recorder));

        var roles = await TransformAsync(source, NoConfiguration(),
            new Claim("upn", "jane@school.test"), new Claim(ClaimTypes.Name, "Jane Smith"));

        Assert.Equal(["Recorder"], roles);
        Assert.Empty(source.Bound);
    }

    [Fact]
    public async Task A_display_name_never_matches_a_row_bound_to_that_name()
    {
        var source = new Rows(new ActiveAssignment(
            Guid.NewGuid(), "Jane Smith", "jane@school.test", PlatformRole.SystemAdministrator));

        Assert.Empty(await TransformAsync(
            source, NoConfiguration(), new Claim(ClaimTypes.Name, "Jane Smith")));
    }

    [Fact]
    public async Task A_bootstrap_entry_is_never_matched_against_a_display_name()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [GroupToRoleClaimsTransformation.BootstrapAdministratorsConfig] = "Jane Smith",
            }).Build();

        Assert.Empty(await TransformAsync(
            new Rows(), configuration, new Claim(ClaimTypes.Name, "Jane Smith")));
    }

    // ---- The bootstrap list in either configuration shape (§3) ------------

    [Fact]
    public async Task The_bootstrap_list_reads_a_single_comma_separated_value()
    {
        // What a plain `.env` line gives, as against the indexed __0/__1 form.
        using var factory = new ApiTestFactory().With(
            GroupToRoleClaimsTransformation.BootstrapAdministratorsConfig,
            " principal@school.test , deputy@school.test ,, ");

        var deputy = factory.CreateClientAs("deputy@school.test");
        Assert.Contains("SystemAdministrator", await RolesAsync(deputy));
        Assert.Empty(await RolesAsync(factory.CreateClientAs("nobody@school.test")));

        // And the same two entries are what the screen lists as read-only.
        var listed = JsonDocument.Parse(await (await deputy.GetAsync(Path)).Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().Select(row => row.GetProperty("upn").GetString()).ToList();
        Assert.Equal(["principal@school.test", "deputy@school.test"], listed);
    }

    [Fact]
    public async Task An_indexed_entry_holding_a_comma_separated_value_is_split()
    {
        // docker-compose.yml passes BOOTSTRAP_ADMIN_UPN as Identity__BootstrapAdministrators__0.
        using var factory = Bootstrapped(" principal@school.test , deputy@school.test ,, ");

        Assert.Contains("SystemAdministrator", await RolesAsync(factory.CreateClientAs("deputy@school.test")));
        Assert.Contains("SystemAdministrator", await RolesAsync(factory.CreateClientAs("principal@school.test")));
        Assert.Empty(await RolesAsync(factory.CreateClientAs("nobody@school.test")));
    }

    [Fact]
    public void Every_indexed_entry_is_split_trimmed_and_emptied_of_blanks()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{GroupToRoleClaimsTransformation.BootstrapAdministratorsConfig}:0"] = "a@school.test, b@school.test",
                [$"{GroupToRoleClaimsTransformation.BootstrapAdministratorsConfig}:1"] = "  ",
                [$"{GroupToRoleClaimsTransformation.BootstrapAdministratorsConfig}:2"] = " c@school.test ,",
            }).Build();

        Assert.Equal(
            ["a@school.test", "b@school.test", "c@school.test"],
            GroupToRoleClaimsTransformation.BootstrapAdministrators(configuration));
    }

    // ---- An unreadable table must not take the API down (§6) --------------

    private static async Task DropAssignmentsTableAsync(ApiTestFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"RoleAssignments\";");
    }

    [Fact]
    public async Task A_failing_read_serves_the_last_good_list_rather_than_500ing()
    {
        using var factory = Bootstrapped("principal@school.test");
        var admin = factory.CreateClientAs("principal@school.test");
        await GrantAsync(admin, "recorder@school.test", "Recorder");

        var recorder = factory.CreateClientAs("recorder@school.test");
        Assert.Equal(["Recorder"], await RolesAsync(recorder));

        await DropAssignmentsTableAsync(factory);
        factory.Services.GetRequiredService<RoleAssignmentProvider>().Invalidate();

        Assert.Equal(["Recorder"], await RolesAsync(recorder));
        Assert.Contains("SystemAdministrator", await RolesAsync(admin));
    }

    [Fact]
    public async Task A_read_that_never_succeeded_serves_no_assignments_and_still_authorises()
    {
        using var factory = Bootstrapped("principal@school.test");
        await DropAssignmentsTableAsync(factory);

        // Group-derived and bootstrap roles are the API's floor when the table
        // cannot be read at all.
        Assert.Equal(["SystemAdministrator"],
            await RolesAsync(factory.CreateClientAs("principal@school.test")));
    }
}
