using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Identity;

namespace CarnivalApp.Api.Http;

/// <summary>
/// Per-subject limit on the bulk roster surfaces (import upload, CSV exports). Runs after
/// authorisation, so every partition key is a real subject — except a demo host's limits, which
/// are keyed on the client address (ADR-0061 §4), and the sign-in limiter, which is one for the
/// whole host and guards only anonymous or self-service password checks.
/// </summary>
public static class RateLimiting
{
    public const string BulkDataPolicy = "bulk-data";

    public const string DemoProvisionPolicy = "demo-provision";

    public const string SignInPolicy = "sign-in";

    /// <summary>Password hashes checked at once, host-wide; not a setting (ADR-0065 §6).</summary>
    public static int SignInPermits => Math.Max(1, Environment.ProcessorCount / 2);

    public const int SignInQueue = 16;

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

        // One partition for the whole host. Load-bearing for the lockout count too: the lockout check
        // and the attempt's record are not atomic, so this bounds how many guesses race past the tenth.
        options.AddPolicy(SignInPolicy, _ => RateLimitPartition.GetConcurrencyLimiter(
            SignInPolicy,
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = SignInPermits,
                QueueLimit = SignInQueue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()
                ?.PolicyName;
            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
                : policy == SignInPolicy ? 5 : 60;
            context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            await context.HttpContext.Response.WriteAsJsonAsync(
                ApiError.Body(ErrorCodes.TooManyRequests,
                    policy switch
                    {
                        SignInPolicy => "Too many sign-ins are being checked at once. Try again in a few seconds.",
                        DemoProvisionPolicy => "Too many demos started from this address. Try again later.",
                        BulkDataPolicy => "Too many report exports or roster uploads in a short time. "
                            + "Wait a minute and try again.",
                        _ => "Too many requests from this address. Wait a minute and try again.",
                    }),
                ct);
        };
    }
}
