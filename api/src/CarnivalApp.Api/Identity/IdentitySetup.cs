using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CarnivalApp.Api.Identity.LocalAccounts;
using CarnivalApp.Api.Imports;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// Entra, ADFS/generic OIDC or a development header, chosen by configuration, and local accounts
/// beside any of them except the demo's. Every tenant-specific value comes from configuration.
/// </summary>
public static class IdentitySetup
{
    public const string AzureAdSection = "AzureAd";
    public const string AdfsSection = "Adfs";
    public const string ProviderConfig = "Identity:Provider";

    public enum IdentityProvider { Entra, Adfs, DevAuth, Demo }

    /// <summary>
    /// An explicit <c>Identity:Provider</c> wins; otherwise inferred from the populated section.
    /// Demo is never inferred.
    /// </summary>
    public static IdentityProvider ResolveProvider(IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config[ProviderConfig])
            && Enum.TryParse<IdentityProvider>(config[ProviderConfig], ignoreCase: true, out var chosen))
            return chosen;

        var azureAd = config.GetSection(AzureAdSection);
        if (!string.IsNullOrWhiteSpace(azureAd["TenantId"]) && !string.IsNullOrWhiteSpace(azureAd["ClientId"]))
            return IdentityProvider.Entra;

        var adfs = config.GetSection(AdfsSection);
        if (!string.IsNullOrWhiteSpace(adfs["Authority"]) && !string.IsNullOrWhiteSpace(adfs["ClientId"]))
            return IdentityProvider.Adfs;

        return IdentityProvider.DevAuth;
    }

    public static WebApplicationBuilder AddPlatformIdentity(this WebApplicationBuilder builder)
    {
        var provider = ResolveProvider(builder.Configuration);
        switch (provider)
        {
            case IdentityProvider.Entra:
                // Cookies authenticates, not the OIDC handler: that accepts only a ticket it issued
                // itself, and a local account's session is the same cookie.
                builder.Services
                    .AddAuthentication(options =>
                    {
                        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                    })
                    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection(AzureAdSection));
                // Post-configured so it chains Microsoft.Identity.Web's own redirect handler.
                builder.Services.Configure<OpenIdConnectOptions>(
                    OpenIdConnectDefaults.AuthenticationScheme, options =>
                    {
                        ApplyLoginHint(options);
                        ApiChallengeSuppression.Apply(options);
                        // Sign-out needs the id token as id_token_hint or Entra asks which account.
                        options.SaveTokens = true;
                    });
                // AddMicrosoftIdentityWebApp leaves the cookie at the 14-day sliding default.
                builder.Services.Configure<CookieAuthenticationOptions>(
                    CookieAuthenticationDefaults.AuthenticationScheme, ConfigureSessionCookie);
                break;

            case IdentityProvider.Adfs:
                AddAdfs(builder.Services, builder.Configuration.GetSection(AdfsSection));
                break;

            case IdentityProvider.Demo:
                Demo.DemoAuthentication.Register(builder.Services, builder.Configuration);
                break;

            default:
                // A deployment merely missing its IdP section must never reach the header handler,
                // whatever its configuration says: outside Development it signs in local accounts only.
                if (builder.Environment.IsDevelopment())
                {
                    builder.Services
                        .AddAuthentication(DevAuthentication.PolicyScheme)
                        .AddPolicyScheme(DevAuthentication.PolicyScheme, null, options =>
                            options.ForwardDefaultSelector = context =>
                                context.Request.Headers.ContainsKey(DevAuthentication.SubjectHeader)
                                    ? DevAuthentication.Scheme
                                    : CookieAuthenticationDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, DevAuthenticationHandler>(DevAuthentication.Scheme, null)
                        .AddCookie(ConfigureLocalOnlyCookie);
                    builder.Services.AddHostedService<DevAuthenticationBanner>();
                }
                else
                    builder.Services
                        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                        .AddCookie(ConfigureLocalOnlyCookie);
                break;
        }

        if (provider != IdentityProvider.Demo) builder.Services.AddLocalAccounts();

        // Scoped, not singleton: binding an assignment writes through a DbContext.
        builder.Services.AddScoped<IClaimsTransformation, GroupToRoleClaimsTransformation>();

        var authorization = builder.Services.AddAuthorizationBuilder();
        // Fail secure: a new endpoint cannot be anonymous by omission.
        authorization.SetFallbackPolicy(
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var (policy, roles) in ImportAuthorization.PolicyRoles)
            authorization.AddPolicy(policy, p => p.RequireRole(roles.Select(r => r.ToString())));
        foreach (var (policy, roles) in Carnivals.CarnivalAuthorization.PolicyRoles)
            authorization.AddPolicy(policy, p => p.RequireRole(roles.Select(r => r.ToString())));
        foreach (var (policy, roles) in School.SchoolAuthorization.PolicyRoles)
            authorization.AddPolicy(policy, p => p.RequireRole(roles.Select(r => r.ToString())));
        foreach (var (policy, roles) in RoleAuthorization.PolicyRoles)
            authorization.AddPolicy(policy, p => p.RequireRole(roles.Select(r => r.ToString())));
        foreach (var (policy, roles) in LocalAccountAuthorization.PolicyRoles)
            authorization.AddPolicy(policy, p => p.RequireRole(roles.Select(r => r.ToString())));

        return builder;
    }

    /// <summary>
    /// 12h fixed covers a carnival day; not sliding, since a self-renewing session defeats a shared
    /// iPad handover. <c>Lax</c> because the OIDC callback returns by top-level GET.
    /// </summary>
    /// <summary><c>__Host-</c>: the browser accepts it only Secure, on path /, with no Domain.</summary>
    public const string SessionCookieName = "__Host-carnival_session";

    public static void ConfigureSessionCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = SessionCookieName;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = false;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        ApiChallengeSuppression.Apply(options);
        LocalSessions.Apply(options);
    }

    /// <summary>
    /// With no provider the sign-in screen is the app's, so a challenge is a status, never a
    /// redirect to a server sign-in page that does not exist.
    /// </summary>
    private static void ConfigureLocalOnlyCookie(CookieAuthenticationOptions options)
    {
        ConfigureSessionCookie(options);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    // The OIDC handler has no hint of its own; Microsoft.Identity.Web reads the same parameter.
    private static void ApplyLoginHint(OpenIdConnectOptions options)
    {
        var inner = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            if (context.Properties.GetParameter<string>(OpenIdConnectParameterNames.LoginHint) is { Length: > 0 } hint)
                context.ProtocolMessage.LoginHint = hint;
            if (inner is not null) await inner(context);
        };
    }

    // MapInboundClaims=false keeps claim types verbatim, so Identity:GroupsClaimType matches what
    // the relying-party claim rule emits.
    private static void AddAdfs(IServiceCollection services, IConfigurationSection adfs)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(ConfigureSessionCookie)
            .AddOpenIdConnect(options =>
            {
                options.Authority = adfs["Authority"];
                if (!string.IsNullOrWhiteSpace(adfs["MetadataAddress"]))
                    options.MetadataAddress = adfs["MetadataAddress"];
                options.ClientId = adfs["ClientId"];
                options.ClientSecret = adfs["ClientSecret"];
                options.CallbackPath = adfs["CallbackPath"] ?? "/signin-oidc";
                options.SignedOutCallbackPath = adfs["SignedOutCallbackPath"] ?? "/signout-callback-oidc";
                options.ResponseType = adfs["ResponseType"] ?? "code";
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SaveTokens = true;
                options.MapInboundClaims = false;

                var scopes = adfs.GetSection("Scopes").Get<string[]>();
                if (scopes is { Length: > 0 })
                {
                    options.Scope.Clear();
                    foreach (var scope in scopes) options.Scope.Add(scope);
                }

                if (!string.IsNullOrWhiteSpace(adfs["NameClaimType"]))
                    options.TokenValidationParameters.NameClaimType = adfs["NameClaimType"];

                ApplyLoginHint(options);
                ApiChallengeSuppression.Apply(options);
            });
    }
}
