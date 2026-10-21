using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Provider-neutral group → platform role mapping (ADR-0007): the source claim
/// type is configurable (Entra "groups" object ids by default; ADFS a
/// school-defined claim carrying group names), and the value → role mapping is
/// the same either way.
/// </summary>
public sealed class GroupToRoleClaimsTransformationTests
{
    /// <summary>No in-app assignments: these cases are about the group mapping alone.</summary>
    private sealed class NoAssignments : IRoleAssignmentSource
    {
        public Task<IReadOnlyList<ActiveAssignment>> GetActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ActiveAssignment>>([]);

        public Task BindAsync(
            IReadOnlyList<Guid> assignmentIds, string subjectId, string upn, string? displayName,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private static GroupToRoleClaimsTransformation Transformation(IConfiguration config) =>
        new(config, NullLogger<GroupToRoleClaimsTransformation>.Instance, new NoAssignments());

    private static IConfiguration Config(string? groupsClaimType, params (string Group, string Role)[] mappings)
    {
        var values = new List<KeyValuePair<string, string?>>();
        if (groupsClaimType is not null)
            values.Add(new("Identity:GroupsClaimType", groupsClaimType));
        foreach (var (group, role) in mappings)
            values.Add(new($"Identity:GroupToRoleMappings:{group}", role));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<HashSet<string>> RolesAfterTransform(
        IConfiguration config, string claimType, params string[] groupValues)
    {
        var claims = groupValues.Select(g => new Claim(claimType, g));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var result = await Transformation(config).TransformAsync(principal);
        return result.FindAll(PlatformClaims.Role).Select(c => c.Value).ToHashSet();
    }

    [Fact]
    public async Task Entra_groups_claim_maps_object_ids_to_roles_by_default()
    {
        var config = Config(null, ("G-ADMINS", "CarnivalAdministrator"), ("G-OFFICIALS", "Recorder"));
        var roles = await RolesAfterTransform(config, "groups", "G-ADMINS", "G-UNMAPPED");
        Assert.Contains("CarnivalAdministrator", roles);
        Assert.DoesNotContain("Recorder", roles); // not a member of that group
        Assert.Single(roles);
    }

    [Fact]
    public async Task Adfs_style_custom_claim_type_maps_group_names_to_roles()
    {
        // ADFS relying-party rule emits AD group NAMES in a "group" claim.
        var config = Config("group", ("Staff-Recorders", "Recorder"));
        var roles = await RolesAfterTransform(config, "group", "Staff-Recorders");
        Assert.Contains("Recorder", roles);
    }

    /// <summary>
    /// ADR-0016: the six retired role names still resolve, so the code deploy and
    /// the group-mapping config change may land in EITHER order.
    /// </summary>
    /// <remarks>
    /// Without this, both orderings fail in the worst possible way: an unresolved
    /// role name is ignored per request, so everyone signs in successfully and
    /// then gets 403 on everything, with only a log line to explain it. This test
    /// is the canary for that whole class of deploy failure.
    /// </remarks>
    [Theory]
    [InlineData("EventAdministrator", "CarnivalAdministrator")]
    [InlineData("Verifier", "CarnivalAdministrator")]
    [InlineData("RecordsManager", "CarnivalAdministrator")]
    [InlineData("ResultsOfficial", "Recorder")]
    [InlineData("ReadOnlyViewer", "Viewer")]
    [InlineData("SystemAdministrator", "SystemAdministrator")] // unchanged, no alias
    public async Task A_retired_role_name_in_config_still_resolves(string configured, string expected)
    {
        var config = Config(null, ("G-LEGACY", configured));
        var roles = await RolesAfterTransform(config, "groups", "G-LEGACY");
        Assert.Equal([expected], roles);
    }

    [Fact]
    public async Task Only_the_configured_claim_type_is_read()
    {
        // GroupsClaimType is "group"; a stray "groups" claim must be ignored.
        var config = Config("group", ("Staff-Recorders", "Recorder"), ("G-ADMINS", "CarnivalAdministrator"));
        var claims = new[]
        {
            new Claim("group", "Staff-Recorders"),
            new Claim("groups", "G-ADMINS"), // wrong claim type for this config
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var roles = (await Transformation(config).TransformAsync(principal))
            .FindAll(PlatformClaims.Role).Select(c => c.Value).ToHashSet();
        Assert.Contains("Recorder", roles);
        Assert.DoesNotContain("CarnivalAdministrator", roles);
    }

    [Fact]
    public async Task An_unknown_role_name_in_config_is_ignored_not_thrown()
    {
        var config = Config(null, ("G-BOGUS", "NotARealRole"));
        var roles = await RolesAfterTransform(config, "groups", "G-BOGUS");
        Assert.Empty(roles);
    }

    [Fact]
    public async Task Unauthenticated_principal_is_returned_unchanged()
    {
        var config = Config(null, ("G-ADMINS", "CarnivalAdministrator"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // no auth type → not authenticated
        var result = await Transformation(config).TransformAsync(anonymous);
        Assert.Empty(result.FindAll(PlatformClaims.Role));
    }
}
