using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Asserts the per-endpoint authorisation placeholders (proposal §8). When the
/// OIDC task replaces the placeholders with real policies, these tests pin the
/// policy each endpoint must carry.
/// </summary>
public class ImportAuthorizationTests
{
    [Fact]
    public void Every_import_endpoint_declares_a_policy()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        ((IEndpointRouteBuilder)app).MapGroup("/api/v1").MapImportEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
        Assert.Equal(7, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<ImportPolicyMetadata>()));
    }

    [Fact]
    public void Mutation_endpoints_require_the_manage_imports_policy()
    {
        var policies = ImportAuthorization.EndpointPolicies;
        Assert.Equal(ImportAuthorization.ManageImportsPolicy, policies["POST /api/v1/imports"]);
        Assert.Equal(ImportAuthorization.ManageImportsPolicy, policies["POST /api/v1/imports/{id}/approve"]);
        Assert.Equal(ImportAuthorization.ManageImportsPolicy, policies["POST /api/v1/imports/{id}/promote"]);
        Assert.Equal(ImportAuthorization.ViewImportsPolicy, policies["GET /api/v1/imports"]);
        Assert.Equal(ImportAuthorization.ViewImportsPolicy, policies["GET /api/v1/imports/defaults"]);
        Assert.Equal(ImportAuthorization.ViewImportsPolicy, policies["GET /api/v1/imports/{id}/validation"]);
        Assert.Equal(ImportAuthorization.ViewImportsPolicy, policies["GET /api/v1/imports/{id}/reconciliation"]);
    }

    [Fact]
    public void Manage_imports_is_limited_to_system_administrator()
    {
        var role = Assert.Single(ImportAuthorization.ManageImportsRoles);
        Assert.Equal(PlatformRole.SystemAdministrator, role);
    }

    [Fact]
    public void Mapped_endpoint_policies_match_the_declared_table()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        ((IEndpointRouteBuilder)app).MapGroup("/api/v1").MapImportEndpoints();

        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources
                     .SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>())
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            var pattern = endpoint.RoutePattern.RawText!
                .Replace("{id:guid}", "{id}")
                .TrimEnd('/');
            var key = $"{method} {pattern}";
            Assert.True(ImportAuthorization.EndpointPolicies.TryGetValue(key, out var expected),
                $"Endpoint '{key}' is not in ImportAuthorization.EndpointPolicies.");
            Assert.Equal(expected, endpoint.Metadata.GetMetadata<ImportPolicyMetadata>()!.PolicyName);
        }
    }
}
