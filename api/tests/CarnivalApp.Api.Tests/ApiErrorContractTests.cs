using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0011: every non-2xx body on /api/v1 is <c>{error: CODE, detail: text}</c>.
/// These cover the shapes the contract retired — bodyless 404s and the
/// authorisation middleware's bodyless 401/403 — plus the two codes that carry
/// structured context. Per-endpoint code choices are asserted in the slice tests.
/// </summary>
public sealed class ApiErrorContractTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly Guid Missing = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    // SystemAdministrator, so the probe reaches EVERY route below — including the
    // import ones, which ADR-0016 narrowed to that role alone. A carnival
    // administrator here would draw 403 and the 404-body contract would go
    // untested on exactly the slice that handles roster data.
    private HttpClient Admin => factory.CreateClientAs("contract-admin", "SystemAdministrator");

    [Theory]
    // Every one of these was a bare Results.NotFound() with no body at all.
    [InlineData("/api/v1/carnivals/{id}", ErrorCodes.CarnivalNotFound)]
    [InlineData("/api/v1/carnivals/{id}/events", ErrorCodes.CarnivalNotFound)]
    [InlineData("/api/v1/carnivals/{id}/events/{id}", ErrorCodes.EventNotFound)]
    [InlineData("/api/v1/carnivals/{id}/events/{id}/entries", ErrorCodes.EventNotFound)]
    [InlineData("/api/v1/carnivals/{id}/events/{id}/results", ErrorCodes.EventNotFound)]
    [InlineData("/api/v1/carnivals/{id}/standings", ErrorCodes.CarnivalNotFound)]
    [InlineData("/api/v1/carnivals/{id}/settings", ErrorCodes.CarnivalNotFound)]
    [InlineData("/api/v1/carnivals/{id}/adjustments", ErrorCodes.CarnivalNotFound)]
    [InlineData("/api/v1/imports/{id}/validation", ErrorCodes.ImportBatchNotFound)]
    [InlineData("/api/v1/event-definitions/{id}", ErrorCodes.EventDefinitionNotFound)]
    public async Task Missing_resources_answer_404_with_a_machine_code(string template, string expected)
    {
        var path = template.Replace("{id}", Missing.ToString());
        // The definition route only exists for PUT/DELETE; DELETE is the read-safe probe.
        var response = template.EndsWith("event-definitions/{id}")
            ? await Admin.DeleteAsync(path)
            : await Admin.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(expected);
    }

    [Fact]
    public async Task Unauthenticated_requests_carry_the_authentication_code()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/version");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task Policy_refusals_carry_the_forbidden_code()
    {
        var stranger = factory.CreateClientAs("contract-stranger", "Recorder");
        var response = await stranger.GetAsync($"/api/v1/imports/{Missing}/validation");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Validation_refusals_carry_a_code_and_keep_the_human_sentence()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/carnivals", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
        // detail is still the sentence the endpoint wrote — nothing was degraded.
        Assert.Contains("name is required", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A body that is not parseable JSON never reaches a handler: model binding
    /// throws BadHttpRequestException, which used to fall through the blanket
    /// exception handler as 500 INTERNAL_ERROR. That is not a cosmetic
    /// mis-labelling — the offline outbox classifies INTERNAL_ERROR as RETRYABLE
    /// (app/src/api/errorCodes.ts), so a queued write whose body can NEVER parse
    /// retried for ever instead of being rejected once. Found 2026-08-07 with a
    /// stray control character, which is the second case below.
    /// </summary>
    [Theory]
    // Syntactically malformed: the object is never closed.
    [InlineData("{\"name\": \"Swimming Carnival\"")]
    // The case actually observed: a raw control character inside a JSON string
    // (0x01 — System.Text.Json requires it escaped).
    [InlineData("{\"name\": \"Swimming\u0001Carnival\"}")]
    // Not JSON at all.
    [InlineData("name=Swimming Carnival")]
    public async Task An_unparseable_json_body_is_a_400_with_a_machine_code(string body)
    {
        var response = await Admin.PostAsync("/api/v1/carnivals",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RequestInvalid);
    }

    /// <summary>
    /// The request body is untrusted input and the framework's message quotes the
    /// parameter it failed to bind, so the detail is a fixed sentence: no
    /// exception text, no type names, no stack frames, and nothing echoed back.
    /// </summary>
    [Fact]
    public async Task An_unreadable_body_is_never_quoted_back_to_the_caller()
    {
        var response = await Admin.PostAsync("/api/v1/carnivals",
            new StringContent("{\"name\": \"SentinelValue\"", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sentinel", payload);                 // the untrusted body
        Assert.DoesNotContain("Failed to read parameter", payload); // the framework's message
        Assert.DoesNotContain("Exception", payload);                // type names
        Assert.DoesNotContain("CarnivalApp", payload);           // stack frames
    }

    /// <summary>
    /// The one that matters, because it is the one the rest of this file CANNOT
    /// see: RouteHandlerOptions.ThrowOnBadRequest defaults to IsDevelopment(), and
    /// ApiTestFactory pins Development, so every other test here would pass
    /// unchanged on a server that answers a malformed body with a BODYLESS 400.
    /// docker-compose runs ASPNETCORE_ENVIRONMENT=Production, which is what
    /// carnival-app-host and the pilot run. Program.cs therefore sets the option
    /// explicitly, and this asserts the ADR-0011 body in the environment the
    /// school actually deploys.
    ///
    /// The Production host has no dev-header scheme — that gate is structural, and
    /// EndpointAuthorizationHttpTests pins it — so the test registers one for
    /// itself: authorisation runs before model binding, and an unauthenticated 401
    /// would never reach the code under test.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_carries_the_contract_body_in_production_too()
    {
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureServices(services => services
                .AddAuthentication(DevAuthentication.Scheme)
                .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(
                    DevAuthentication.Scheme, null));
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "production-admin");
        client.DefaultRequestHeaders.Add(DevAuthentication.RolesHeader, "CarnivalAdministrator");

        var response = await client.PostAsync("/api/v1/carnivals",
            new StringContent("{\"name\": \"Swimming Carnival\"", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RequestInvalid);
    }

    /// <summary>
    /// BadHttpRequestException is thrown for far more than "the client sent bad
    /// bytes", and only the two positively identifiable cases may be terminal
    /// (unparseable bytes, and a 413 over the body-size limit). This case and the
    /// one below it are neither — verified 2026-08-10 by capturing what the
    /// pipeline actually logs:
    ///
    /// <list type="bullet">
    /// <item>an absent body is RequestDelegateFactory's ImplicitBodyNotProvided
    /// ("…but no body was provided. Did you mean to use a Service instead?"),
    /// status 400, NO inner exception — the message names a SERVER handler-signature
    /// or DI mistake, so answering it terminally would discard every queued write to
    /// the route until someone noticed;</item>
    /// <item>an absent content type is UnexpectedNonJsonContentType, status 415, no
    /// inner exception.</item>
    /// </list>
    ///
    /// Neither carries a parse failure, so both must come back RETRYABLE rather than
    /// putting a captured result on the rejected pile. The conditions that actually
    /// threaten a carnival — Kestrel's request-body timeout on saturated venue 4G, a
    /// truncated upload — wear this same shape and cannot be produced in-process;
    /// ApiFaultClassificationTests pins those.
    /// </summary>
    [Fact]
    public async Task A_body_the_framework_says_was_never_provided_stays_retryable()
    {
        var response = await Admin.PostAsync("/api/v1/carnivals",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.InternalError);
    }

    /// <inheritdoc cref="A_body_the_framework_says_was_never_provided_stays_retryable"/>
    [Fact]
    public async Task A_content_type_the_endpoint_cannot_read_stays_retryable()
    {
        var response = await Admin.PostAsync("/api/v1/carnivals",
            new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\": \"Swimming Carnival\"}")));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.InternalError);
    }

    /// <summary>
    /// The fallback that branch sits in front of: an ordinary unhandled fault still
    /// answers 500 INTERNAL_ERROR in the ADR-0011 shape, with nothing of the
    /// exception on the wire. The RaceInterceptor is the fault injector — it fires
    /// inside the handler, on a host of this test's own so nothing else can trip it.
    /// </summary>
    [Fact]
    public async Task An_unhandled_server_fault_is_still_a_500_with_the_internal_code()
    {
        using var isolated = new ApiTestFactory();
        var client = isolated.CreateClientAs("fault-admin", "CarnivalAdministrator");
        isolated.Race.ArmOnce("Carnivals", _ => throw new InvalidOperationException("SentinelFault"));

        var response = await client.GetAsync("/api/v1/carnivals");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.InternalError);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SentinelFault", payload);
        Assert.DoesNotContain("CarnivalApp", payload);
    }

    [Fact]
    public async Task A_postgres_serialisation_failure_is_a_409_concurrency_conflict()
    {
        using var isolated = new ApiTestFactory();
        var client = isolated.CreateClientAs("fault-admin", "CarnivalAdministrator");
        isolated.Race.ArmOnce("Carnivals", _ => throw new PostgresException(
            "could not serialize access due to read/write dependencies among transactions",
            "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure));

        var response = await client.GetAsync("/api/v1/carnivals");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);
        Assert.Contains("retry", await response.ErrorDetailAsync());
        Assert.DoesNotContain("serialize", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// The retry-vs-terminal classification behind the exception handler, unit-tested
/// because the conditions that make it load-bearing cannot be produced through the
/// in-process test host at all: Kestrel's request-body timeout and truncated-body
/// rejections need a real connection, and a handler-signature mistake needs a
/// broken endpoint. Getting one of those wrong in the TERMINAL direction destroys a
/// captured result, so they are pinned on the classifier directly.
/// </summary>
public sealed class ApiFaultClassificationTests
{
    /// <summary>
    /// The first of the two terminal classifications, and the discriminator that
    /// earns it: the parse failure travels as the inner exception, which only
    /// minimal-API JSON body binding ever does. Same bytes, same outcome, for
    /// ever — refuse once.
    /// </summary>
    [Fact]
    public void An_unparseable_body_is_refused_once_as_request_invalid()
    {
        var fault = ApiError.DescribeFault(new BadHttpRequestException(
            "Failed to read parameter \"CreateCarnivalRequest request\" from the request body as JSON.",
            StatusCodes.Status400BadRequest,
            new JsonException("'0x01' is invalid within a JSON string.")));

        Assert.Equal(StatusCodes.Status400BadRequest, fault.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, fault.Code);
        // ADR-0011: a sentence always, and never the exception's own.
        Assert.False(string.IsNullOrWhiteSpace(fault.Detail));
        Assert.DoesNotContain("CreateCarnivalRequest", fault.Detail);
        Assert.DoesNotContain("0x01", fault.Detail);
    }

    /// <summary>
    /// The second terminal classification. Kestrel sets 413 for the request
    /// body-size limit and for nothing else, so the status alone identifies it —
    /// no message matching, and no inner exception to read.
    ///
    /// Terminal FOR RETRY PURPOSES only, and that distinction is the whole reason
    /// this is not left on the retryable pile: an oversized body never shrinks, so
    /// the retry can never succeed, and a `retry` outcome STOPS the client's drain
    /// (drainOnce halts to preserve capture order) — one oversized capture would
    /// head-of-line block every later result for the rest of the carnival. The
    /// client parks it as a conflict with the payload intact instead, so raising
    /// the deployment's size limit lands that very item. See
    /// app/src/api/errorCodes.ts (CONFLICT_CODES).
    /// </summary>
    [Fact]
    public void An_oversized_body_is_413_with_its_own_code_not_a_retryable_500()
    {
        // No inner exception: the 400-vs-413 discriminator is the status Kestrel set.
        var fault = ApiError.DescribeFault(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, fault.StatusCode);
        Assert.Equal(ErrorCodes.RequestBodyTooLarge, fault.Code);
        // NOT the retryable answer — that is the bug this branch exists to prevent.
        Assert.NotEqual(ErrorCodes.InternalError, fault.Code);
        Assert.False(string.IsNullOrWhiteSpace(fault.Detail));
        Assert.DoesNotContain("Request body too large", fault.Detail); // ADR-0011: never the exception's own
    }

    /// <summary>
    /// Every other BadHttpRequestException shape the framework produces stays 500
    /// INTERNAL_ERROR, which the outbox classifies as RETRYABLE. The messages below
    /// are the real framework wording purely as documentation — nothing matches on
    /// them, deliberately: substring matching is locale- and version-fragile, so the
    /// classifier reads only the inner exception and the status code.
    /// </summary>
    [Fact]
    public void Everything_else_stays_a_retryable_500()
    {
        Exception?[] notUnparseableBytes =
        [
            // TRANSIENT, and thrown regardless of ThrowOnBadRequest — Kestrel's
            // MinRequestBodyDataRate, i.e. a device draining its outbox over
            // saturated venue 4G. The body is valid; it merely arrived slowly.
            new BadHttpRequestException(
                "Reading the request body timed out due to data arriving too slowly.",
                StatusCodes.Status408RequestTimeout),
            // TRANSIENT — Kestrel UnexpectedEndOfRequestContent: truncated upload.
            new BadHttpRequestException("Unexpected end of request content.",
                StatusCodes.Status400BadRequest),
            // SERVER FAULT wearing a 400 — RequestDelegateFactory's
            // ImplicitBodyNotProvided: a handler signature or DI registration
            // mistake. Terminal here would discard every queued write to that route,
            // unrecoverable by fixing the server and redeploying.
            new BadHttpRequestException(
                "Implicit body inferred for parameter \"service\" but no body was provided. "
                + "Did you mean to use a Service instead?"),
            // SERVER FAULT — RequiredParameterNotProvided (route-template mistake).
            new BadHttpRequestException("Required parameter \"Guid id\" was not provided from route."),
            // Client-caused but not unparseable bytes, and not distinguishable from a
            // server-side limit or header misconfiguration: takes the safe side too.
            new BadHttpRequestException("Expected a supported JSON media type but got \"text/plain\".",
                StatusCodes.Status415UnsupportedMediaType),
            // An ordinary unhandled fault, and the null IExceptionHandlerFeature
            // yields when there is somehow no exception to describe.
            new InvalidOperationException("a handler threw"),
            null,
        ];

        foreach (var thrown in notUnparseableBytes)
        {
            var fault = ApiError.DescribeFault(thrown);
            Assert.Equal(StatusCodes.Status500InternalServerError, fault.StatusCode);
            Assert.Equal(ErrorCodes.InternalError, fault.Code);
            Assert.False(string.IsNullOrWhiteSpace(fault.Detail));
        }
    }

    private static PostgresException Postgres(string sqlState) =>
        new("could not serialize access", "ERROR", "ERROR", sqlState);

    [Fact]
    public void A_bare_serialisation_failure_is_a_409_concurrency_conflict()
    {
        var fault = ApiError.DescribeFault(Postgres(PostgresErrorCodes.SerializationFailure));

        Assert.Equal(StatusCodes.Status409Conflict, fault.StatusCode);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, fault.Code);
        Assert.Contains("Re-read it and retry", fault.Detail);
        Assert.DoesNotContain("serialize", fault.Detail);
    }

    [Fact]
    public void A_serialisation_failure_wrapped_by_save_changes_is_a_409_concurrency_conflict()
    {
        var fault = ApiError.DescribeFault(new DbUpdateException(
            "An error occurred while saving the entity changes.",
            Postgres(PostgresErrorCodes.SerializationFailure)));

        Assert.Equal(StatusCodes.Status409Conflict, fault.StatusCode);
        Assert.Equal(ErrorCodes.ConcurrencyConflict, fault.Code);
    }

    [Fact]
    public void Any_other_postgres_fault_stays_a_retryable_500()
    {
        foreach (var thrown in new Exception[]
                 {
                     Postgres(PostgresErrorCodes.DeadlockDetected),
                     new DbUpdateException("save failed", Postgres(PostgresErrorCodes.UniqueViolation)),
                 })
        {
            var fault = ApiError.DescribeFault(thrown);
            Assert.Equal(StatusCodes.Status500InternalServerError, fault.StatusCode);
            Assert.Equal(ErrorCodes.InternalError, fault.Code);
        }
    }
}
