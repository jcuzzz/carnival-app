using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Identity;

namespace CarnivalApp.Api.Http;

/// <summary>
/// Per-subject limit on the bulk roster surfaces (import upload, CSV exports). Runs after
/// authorisation, so every partition key is a real subject — except a demo host's limits, which
/// are keyed on the client address (ADR-0061 §4).
/// </summary>
public static class RateLimiting
{
    public const string BulkDataPolicy = "bulk-data";

    public const string DemoProvisionPolicy = "demo-provision";

    public const string PermitsPerMinuteKey = "RateLimiting:BulkDataPerMinute";

    // Read per request: host-builder configuration is not readable while services register.
    public static void Configure(RateLimiterOptions options, bool demoMode)
    {
        // A public demo host only: every request, keyed on the forwarded client address.
        if (demoMode)
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, context.RequestServices
                            .GetRequiredService<IConfiguration>()
                            .GetValue(Demo.DemoMode.RequestsPerIpPerMinuteKey, 1200)),
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

        options.AddPolicy(BulkDataPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.GetSubject() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, context.RequestServices
                    .GetRequiredService<IConfiguration>()
                    .GetValue(PermitsPerMinuteKey, 10)),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // The address is the forwarded client's: UseForwardedHeaders trusts the proxy only.
        options.AddPolicy(DemoProvisionPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, context.RequestServices
                    .GetRequiredService<IConfiguration>()
                    .GetValue(Demo.DemoMode.ProvisionsPerIpPerHourKey, 10)),
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
            }));

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
                : 60;
            context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            await context.HttpContext.Response.WriteAsJsonAsync(
                ApiError.Body(ErrorCodes.TooManyRequests,
                    (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()
                        ?.PolicyName) switch
                    {
                        DemoProvisionPolicy => "Too many demos started from this address. Try again later.",
                        BulkDataPolicy => "Too many report exports or roster uploads in a short time. "
                            + "Wait a minute and try again.",
                        _ => "Too many requests from this address. Wait a minute and try again.",
                    }),
                ct);
        };
    }
}
