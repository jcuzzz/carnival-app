using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Every mapped endpoint declares what it wants: a policy, or anonymity. The
/// per-endpoint matrices elsewhere are hand-maintained lists of routes somebody
/// remembered; this one enumerates the routing table itself, so a route added
/// tomorrow is covered without anybody adding a row.
/// </summary>
/// <remarks>
/// A fallback policy (IdentitySetup) already makes an undeclared endpoint
/// require an authenticated user, so this is not the gate — it is the check that
/// anonymity stays a DECISION. The anonymous set is spelled out below rather
/// than merely counted: adding to it must be a deliberate edit here.
/// </remarks>
public sealed class EndpointAuthorizationCoverageTests(ApiTestFactory factory)
    : IClassFixture<ApiTestFactory>
{
    /// <summary>The whole anonymous surface. Nothing else may opt out.</summary>
    internal static readonly string[] Anonymous =
    [
        "/health",
        "/api/v1/auth/login",
        "/api/v1/auth/logout",
        // ADR-0040 §3: school name + brand colour, read by the sign-in screen
        // before a session exists. Added deliberately — H1 closed the anonymous
        // hole on /version and /school/houses, and this is the one read allowed
        // back out, carrying nothing a sign-in page does not already show.
        "/api/v1/branding",
        // Development only: the document the PWA generates its wire types from.
        "/openapi/{documentName}.json",
    ];

    private IEnumerable<RouteEndpoint> Endpoints => factory.Services
        .GetRequiredService<EndpointDataSource>()
        .Endpoints
        .OfType<RouteEndpoint>();

    [Fact]
    public void Every_endpoint_either_carries_authorisation_or_says_it_is_anonymous()
    {
        var undeclared = Endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void Only_the_named_routes_are_anonymous()
    {
        // A subset, not an equality: /auth/login and /auth/logout are mapped only
        // when a real OIDC provider is configured, and this host runs dev-auth.
        // EntraEndpointAuthorizationCoverageTests holds the equality.
        var anonymous = Endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Distinct()
            .ToList();

        Assert.All(anonymous, path => Assert.Contains(path, Anonymous));
        Assert.Contains("/health", anonymous);
    }

    [Fact]
    public void Only_two_kinds_of_api_v1_route_are_anonymous()
    {
        // /auth/login and /auth/logout are the exception the rule is written
        // around — they exist to START a session, so they cannot need one — and
        // they only exist when a real OIDC provider is configured. /branding is
        // the second (ADR-0040 §3): the sign-in screen paints the school's name
        // and colour before there is a session to read them with. Everything
        // else under /api/v1 is authenticated surface.
        var anonymousApi = Endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Where(path => path.StartsWith("/api/v1", StringComparison.Ordinal))
            .ToList();

        Assert.All(anonymousApi, path => Assert.True(
            path.StartsWith("/api/v1/auth/", StringComparison.Ordinal) || path == "/api/v1/branding",
            $"{path} is anonymous under /api/v1 and is neither an auth route nor /branding."));
        Assert.Contains("/api/v1/branding", anonymousApi);
    }
}
