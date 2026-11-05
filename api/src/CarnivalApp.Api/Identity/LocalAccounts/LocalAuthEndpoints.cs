using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Identity.LocalAccounts;

public enum SignInProvider
{
    Entra,
    Adfs,
}

/// <summary><c>Provider</c> is null when the school has no identity provider.</summary>
public sealed record AuthMethodsResponse(SignInProvider? Provider);

public sealed record IdentifyRequest(string? Username, string? ReturnUrl);

public enum IdentifyMethod
{
    [JsonStringEnumMemberName("password")] Password,
    [JsonStringEnumMemberName("provider")] Provider,
}

/// <summary><c>LoginUrl</c> is set for <c>provider</c>: navigate to it.</summary>
public sealed record IdentifyResponse(
    IdentifyMethod Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LoginUrl = null);

/// <summary>Stateless: every step sends the password again.</summary>
public sealed record LocalSignInRequest(
    string? Username, string? Password, string? Code, string? RecoveryCode, string? EnrolmentCode);

public enum LocalSignInStatus
{
    [JsonStringEnumMemberName("signedIn")] SignedIn,
    [JsonStringEnumMemberName("codeRequired")] CodeRequired,
    [JsonStringEnumMemberName("enrolmentRequired")] EnrolmentRequired,
}

/// <summary>
/// <c>Me</c> with <c>signedIn</c> (and <c>RecoveryCodes</c> when this sign-in enrolled an
/// authenticator, shown once); <c>Secret</c> and <c>OtpauthUri</c> with <c>enrolmentRequired</c>.
/// </summary>
public sealed record LocalSignInResponse(
    LocalSignInStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MeResponse? Me = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? RecoveryCodes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OtpauthUri = null);

public sealed record SetupTokenRequest(string? Token);

/// <summary><c>Username</c> and <c>DisplayName</c> are read for a first-administrator link only.</summary>
public sealed record SetupCompleteRequest(string? Token, string? Username, string? DisplayName, string? Password);

/// <summary>The anonymous sign-in surface for local accounts (ADR-0065).</summary>
public static class LocalAuthEndpoints
{
    public static void MapLocalAuthEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var auth = apiV1.MapGroup("/auth").AllowAnonymous();

        auth.MapGet("/methods", (IConfiguration configuration) =>
                Results.Ok(new AuthMethodsResponse(IdentitySetup.ResolveProvider(configuration) switch
                {
                    IdentitySetup.IdentityProvider.Entra => SignInProvider.Entra,
                    IdentitySetup.IdentityProvider.Adfs => SignInProvider.Adfs,
                    _ => null,
                })))
            .Produces<AuthMethodsResponse>();

        auth.MapPost("/identify", IdentifyAsync).Produces<IdentifyResponse>();

        auth.MapPost("/local", SignInAsync)
            .RequireRateLimiting(RateLimiting.SignInPolicy)
            .Produces<LocalSignInResponse>()
            .ProducesTooManyRequests();

        auth.MapPost("/setup/inspect", InspectAsync)
            .Produces<SetupLinkInspection>()
            .ProducesGone();

        auth.MapPost("/setup/complete", CompleteAsync)
            .RequireRateLimiting(RateLimiting.SignInPolicy)
            .Produces<LocalSignInResponse>()
            .ProducesGone()
            .ProducesTooManyRequests();
    }

    private static async Task<IResult> IdentifyAsync(
        IdentifyRequest request,
        [FromServices] CarnivalAppDbContext db,
        [FromServices] UserManager<LocalAccount> users,
        [FromServices] IConfiguration configuration,
        CancellationToken ct)
    {
        var username = LocalUsernames.Normalise(request.Username);
        if (username is null || username.Length > RoleAssignmentLimits.MaxUpnLength || username.Any(char.IsControl))
            return ApiError.Unprocessable(ErrorCodes.ValidationFailed, "Enter your username or school email.");

        if (!LocalAccountPolicy.ProviderConfigured(configuration))
            return Results.Ok(new IdentifyResponse(IdentifyMethod.Password));

        if (username.Length <= LocalUsernames.MaxLength && !username.Contains('@'))
        {
            var normalised = users.NormalizeName(username);
            if (await db.LocalAccounts.AsNoTracking().AnyAsync(a => a.NormalizedUserName == normalised, ct))
                return Results.Ok(new IdentifyResponse(IdentifyMethod.Password));
        }

        var loginUrl = "/api/v1/auth/login"
            + $"?returnUrl={Uri.EscapeDataString(AuthEndpoints.LocalReturnUrl(request.ReturnUrl))}"
            + $"&loginHint={Uri.EscapeDataString(username)}";
        return Results.Ok(new IdentifyResponse(IdentifyMethod.Provider, loginUrl));
    }

    private static async Task<IResult> SignInAsync(
        LocalSignInRequest request,
        HttpContext http,
        [FromServices] LocalSignInService signIn,
        [FromServices] IClaimsTransformation roles,
        CancellationToken ct)
    {
        var outcome = await signIn.SignInAsync(
            new LocalSignInInput(request.Username, request.Password, request.Code, request.RecoveryCode,
                request.EnrolmentCode),
            http.Connection.RemoteIpAddress, ct);
        return await AnswerAsync(outcome, http, roles);
    }

    private static async Task<IResult> InspectAsync(
        SetupTokenRequest request, [FromServices] SetupLinks links, CancellationToken ct) =>
        await links.InspectAsync(request.Token, ct) is { } inspection
            ? Results.Ok(inspection)
            : LocalAccountErrors.ToResult(ErrorCodes.SetupLinkInvalid);

    private static async Task<IResult> CompleteAsync(
        SetupCompleteRequest request,
        HttpContext http,
        [FromServices] SetupLinks links,
        [FromServices] IClaimsTransformation roles,
        CancellationToken ct)
    {
        var outcome = await links.CompleteAsync(
            new SetupCompletion(request.Token, request.Username, request.DisplayName, request.Password),
            http.Connection.RemoteIpAddress, ct);
        return await AnswerAsync(outcome, http, roles);
    }

    private static async Task<IResult> AnswerAsync(
        LocalSignInOutcome outcome, HttpContext http, IClaimsTransformation roles)
    {
        switch (outcome)
        {
            case LocalSignInOutcome.SignedIn signedIn:
                var sessionId = await LocalSessions.SignInAsync(http, signedIn.Account);
                var me = AuthEndpoints.Me(
                    await roles.TransformAsync(LocalSessions.PrincipalFor(signedIn.Account, sessionId)));
                return Results.Ok(new LocalSignInResponse(LocalSignInStatus.SignedIn, me, signedIn.RecoveryCodes));
            case LocalSignInOutcome.CodeRequired:
                return Results.Ok(new LocalSignInResponse(LocalSignInStatus.CodeRequired));
            case LocalSignInOutcome.EnrolmentRequired enrolment:
                return Results.Ok(new LocalSignInResponse(LocalSignInStatus.EnrolmentRequired,
                    Secret: enrolment.Secret, OtpauthUri: enrolment.OtpauthUri));
            case LocalSignInOutcome.Failed failed:
                return LocalAccountErrors.ToResult(failed.ErrorCode, signingIn: true);
            default:
                throw new InvalidOperationException($"Unexpected sign-in outcome {outcome.GetType().Name}.");
        }
    }

    internal static RouteHandlerBuilder ProducesGone(this RouteHandlerBuilder builder) =>
        builder.Produces<ApiErrorBody>(StatusCodes.Status410Gone, "application/json");

    internal static RouteHandlerBuilder ProducesTooManyRequests(this RouteHandlerBuilder builder) =>
        builder.Produces<ApiErrorBody>(StatusCodes.Status429TooManyRequests, "application/json");
}

/// <summary>A code from the local-account services, answered with its status and copy.</summary>
public static class LocalAccountErrors
{
    /// <summary><paramref name="signingIn"/>: a wrong code at sign-in is a failed sign-in, so 401.</summary>
    public static IResult ToResult(string code, bool signingIn = false)
    {
        var (status, detail) = code switch
        {
            ErrorCodes.SignInFailed => (StatusCodes.Status401Unauthorized,
                "The username or password is not right, or this account cannot sign in at the moment."),
            ErrorCodes.CodeInvalid => (signingIn ? StatusCodes.Status401Unauthorized : StatusCodes.Status422UnprocessableEntity,
                "That code is not right. Enter the newest code from the authenticator app, or a recovery code."),
            ErrorCodes.LocalSignInNotAllowed => (StatusCodes.Status403Forbidden,
                "This school signs in through its identity provider; local accounts are for System administrators only."),
            ErrorCodes.SetupLinkInvalid => (StatusCodes.Status410Gone,
                "This setup link has expired, been used or been replaced. Ask an administrator for a new one."),
            ErrorCodes.PasswordTooShort or ErrorCodes.PasswordTooLong or ErrorCodes.PasswordTooCommon
                or ErrorCodes.PasswordTooPredictable => (StatusCodes.Status422UnprocessableEntity,
                LocalPasswordValidator.Describe(code)),
            ErrorCodes.CurrentPasswordWrong => (StatusCodes.Status422UnprocessableEntity,
                "Your current password is not right."),
            ErrorCodes.AccountUsernameInvalid => (StatusCodes.Status422UnprocessableEntity,
                "Use 2 to 64 lower-case letters, digits, '.', '_' or '-', starting with a letter or digit."),
            ErrorCodes.AccountUsernameReserved => (StatusCodes.Status422UnprocessableEntity,
                "That name is a configured bootstrap administrator. Choose another username."),
            ErrorCodes.AccountDisplayNameInvalid => (StatusCodes.Status422UnprocessableEntity,
                $"Enter a display name of at most {LocalAccountLimits.MaxDisplayNameLength} characters."),
            ErrorCodes.AccountUsernameTaken => (StatusCodes.Status409Conflict,
                "That username is already in use."),
            ErrorCodes.AccountNotFound => (StatusCodes.Status404NotFound,
                "That account does not exist."),
            ErrorCodes.AccountDisabled => (StatusCodes.Status409Conflict,
                "This account is disabled. Enable it before resetting it."),
            ErrorCodes.AccountSelfChange => (StatusCodes.Status409Conflict,
                "You cannot do this to your own account. Ask another System administrator."),
            ErrorCodes.AuthenticatorAlreadyEnrolled => (StatusCodes.Status409Conflict,
                "An authenticator app is already set up for this account."),
            ErrorCodes.ValidationFailed => (StatusCodes.Status422UnprocessableEntity,
                "Enter a username and a password."),
            _ => throw new InvalidOperationException($"No answer is defined for the local-account code {code}."),
        };
        return ApiError.Status(status, code, detail);
    }
}
