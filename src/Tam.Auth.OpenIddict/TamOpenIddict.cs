using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tam.Auth;

/// <summary>
/// The framework's own authentication (docs: self-sufficiency): an embedded OpenIddict server issuing
/// tokens against the platform-global account store (docs/26). Humans use Authorization Code + PKCE
/// through a framework-rendered login and tenant picker; machines (agents, integrations) use client
/// credentials. There is no password grant — the resource-owner flow is retired (OAuth 2.1). This is
/// ONE implementation behind the <see cref="IActorProvider"/> seam: any external IdP that produces
/// claims plugs in through <see cref="ClaimsActorProvider"/>, and anything else replaces the provider.
/// </summary>
public static class TamOpenIddict
{
    /// <summary>The interactive login cookie: proves who is signing in while they pick a tenant and
    /// the authorization endpoint issues the code. Separate from the API's bearer validation scheme.</summary>
    public const string LoginScheme = "tam-login";

    /// <summary>Registers the embedded token server + validation, the interactive-login cookie, and
    /// swaps the actor + tenant providers to claims-based resolution. Call after AddTam. The
    /// <paramref name="fallbackTenant"/> scopes requests that carry no active-tenant claim yet.</summary>
    public static IServiceCollection AddTamOpenIddict<TDbContext>(
        this IServiceCollection services, string fallbackTenant = "demo")
        where TDbContext : DbContext
    {
        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<TDbContext>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/connect/authorize")
                       .SetTokenEndpointUris("/connect/token")
                       .SetEndSessionEndpointUris("/connect/logout")
                       .SetRevocationEndpointUris("/connect/revocation");

                // Humans: Authorization Code + PKCE (PKCE required — the SPA is a public client with
                // no secret) + refresh so a short-lived access token renews silently. Machines: client
                // credentials. No password grant (OAuth 2.1).
                options.AllowAuthorizationCodeFlow()
                       .AllowRefreshTokenFlow()
                       .AllowClientCredentialsFlow();
                options.RequireProofKeyForCodeExchange();
                options.RegisterScopes(Scopes.OfflineAccess);

                // A short access-token lifetime makes the refresh path real (the client renews behind
                // the scenes); the refresh token carries the longer session. Production tunes both.
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(TimeSpan.FromDays(14));

                // No-BFF hardening (docs/26): the SPA holds the rotating refresh token itself, so
                // the server enforces one-time use — a redeemed token replayed after this leeway is
                // rejected AND its whole family (the shared authorization) is revoked, cutting off a
                // stolen-token session. The leeway only absorbs the client's own racing retries.
                options.SetRefreshTokenReuseLeeway(TimeSpan.FromSeconds(30));
                options.AddEventHandler(RefreshReuseGuard.Descriptor);

                // Development keys: ephemeral, tokens do not survive a restart. Production supplies
                // certificates here — the one deliberate configuration seam.
                options.AddEphemeralEncryptionKey()
                       .AddEphemeralSigningKey();
                options.DisableAccessTokenEncryption();

                // TLS is the host's concern (reverse proxy / production certs); the framework must
                // work on plain HTTP in development. Passthrough hands the interactive endpoints to
                // the minimal-API handlers below.
                options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableEndSessionEndpointPassthrough()
                       .DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
                // Check the token AND authorization entries on every API call (one indexed lookup
                // each), so revocation — sign-out, reuse-triggered family cut — takes effect
                // immediately instead of when the 10-minute access token happens to expire.
                options.EnableTokenEntryValidation();
                options.EnableAuthorizationEntryValidation();
            });

        // Bearer validation is the default scheme for API calls; the login cookie is a named scheme
        // used only inside the authorization flow.
        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddCookie(LoginScheme, options =>
            {
                options.Cookie.Name = "tam-login";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            });
        services.AddAuthorization();
        services.AddScoped<RefreshReuseGuard>();
        services.AddHostedService<TokenJanitor>();
        services.AddSingleton<IActorProvider, ClaimsActorProvider>();
        // The active tenant now comes from the token's claim (set at login when the account picks a
        // tenant); unauthenticated requests fall back to the configured default.
        services.AddSingleton<ITenantProvider>(new ClaimTenantProvider(fallbackTenant));
        return services;
    }

    /// <summary>Maps the auth endpoints and enables authentication. Call before MapTam.</summary>
    public static WebApplication MapTamAuth(this WebApplication app)
    {
        app.UseAuthentication();
        // Pin the request's active tenant AFTER authentication so claim-based resolution
        // (ClaimTenantProvider) can read the bearer token's active-tenant claim — the global query
        // filter and actor resolution both run off this. Before authn the principal is empty and the
        // tenant would always fall back. This is why the tenant scope lives here, not before MapTamAuth.
        app.UseTamTenantScope();
        app.UseAuthorization();
        app.MapMethods("/connect/authorize", ["GET", "POST"], Authorize);
        app.MapPost("/connect/authorize/login", LoginSubmit);
        app.MapPost("/connect/token", Exchange);
        app.MapGet("/connect/logout", Logout);
        app.MapGet("/connect/invite", InviteForm);
        app.MapPost("/connect/invite", InviteSubmit);
        return app;
    }

    // ── Authorization endpoint ────────────────────────────────────────────────────────────────
    // Drives the interactive flow: authenticate the login cookie (render the login page if absent),
    // resolve the account's tenant memberships, pick the active tenant (render the picker when there
    // is more than one), then issue the code for the chosen (account, tenant).
    private static async Task<IResult> Authorize(HttpContext http, ITamDb tam, TamModel model)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");
        var culture = TamAspNetCore.ResolveCulture(http, model);

        var login = await http.AuthenticateAsync(LoginScheme);
        if (login.Principal?.FindFirst(ClaimsActorProvider.AccountClaim)?.Value is not { } subject
            || !Guid.TryParse(subject, out var accountId))
        {
            var returnUrl = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
            return Html(AuthPages.LoginPage(model, culture, returnUrl, error: false));
        }

        // ONE standable-set load drives the whole decision (docs/26 D-H3): no access, auto-pick,
        // honor a requested node, or render the picker — the same TenantTree set the act-as
        // middleware validates against, so the two can never disagree. Note: an account whose single
        // membership carries a cascading role now gets the PICKER (its standable set is >1) instead
        // of silently auto-picking the membership node — deliberate, per D-H3.
        var standable = TenantTree.StandableNodes(tam.Db, accountId);
        if (standable.Count == 0)
            return Html(AuthPages.Message(model, culture, "auth.no-access"));

        var requested = request.GetParameter("tenant")?.Value?.ToString();
        var chosen = requested is { Length: > 0 } && standable.Any(n => n.Id == requested)
            ? requested
            : standable.Count == 1 ? standable[0].Id : null;

        if (chosen is null)
        {
            var options = standable.Select(n => (n.Id, n.Display)).ToList();
            return Html(AuthPages.TenantPicker(model, culture, http.Request.Query, options));
        }

        var account = await tam.Db.Set<AccountEntity>().FirstOrDefaultAsync(a => a.Id == accountId && a.Active);
        if (account is null) return Html(AuthPages.Message(model, culture, "auth.no-access"));

        // Always grant offline_access for an interactive human login so a refresh token is issued and
        // the client can renew the short-lived access token silently — the library handles it.
        return SignInAsAccount(account, chosen,
            request.GetScopes().Append(Scopes.OfflineAccess).Distinct());
    }

    /// <summary>The ONE token-principal shape (docs/26): subject = the global account, the active
    /// tenant claim selects context (access is still proven by membership per request), everything
    /// destined for the access token. Both the human login and machine clients issue through here.</summary>
    private static IResult SignInAsAccount(AccountEntity account, string? tenant, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            TokenValidationConstants.AuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString());
        identity.SetClaim(Claims.Name, account.DisplayName);
        identity.SetClaim(ClaimsActorProvider.AccountClaim, account.Id.ToString());
        if (tenant is not null)
            identity.SetClaim(ClaimsActorProvider.ActiveTenantClaim, tenant);
        identity.SetScopes(scopes);
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ── Login form POST ───────────────────────────────────────────────────────────────────────
    private static async Task<IResult> LoginSubmit(HttpContext http, ITamDb tam, TamModel model)
    {
        var form = await http.Request.ReadFormAsync();
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        // Only ever return inside the app — never to an attacker-supplied absolute URL.
        if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)) returnUrl = "/";
        var culture = TamAspNetCore.ResolveCulture(http, model);

        // The account is platform-global: authenticate the Email/handle across the platform. The
        // active tenant is chosen next, at the authorization endpoint, from its memberships.
        var account = await tam.Db.Set<AccountEntity>()
            .FirstOrDefaultAsync(a => a.Email == email && a.Active);
        if (account is null || !TamPasswords.Verify(password, account.PasswordHash))
            return Html(AuthPages.LoginPage(model, culture, returnUrl, error: true));

        var identity = new ClaimsIdentity(LoginScheme);
        identity.AddClaim(new Claim(ClaimsActorProvider.AccountClaim, account.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, account.DisplayName));
        await http.SignInAsync(LoginScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(returnUrl);
    }

    // ── Token endpoint ────────────────────────────────────────────────────────────────────────
    private static async Task<IResult> Exchange(HttpContext http, ITamDb tam)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // Both grants re-issue the SAME stored principal: the code (from the login flow) or the
            // refresh token both carry subject = account, active tenant, and scopes. OpenIddict has
            // already validated the PKCE verifier / the refresh token, so this just re-emits — a
            // refresh silently renews the short-lived access token without another login.
            var result = await http.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (result.Principal is null) return Deny();

            foreach (var claim in result.Principal.Claims)
                claim.SetDestinations(Destinations.AccessToken);
            return Results.SignIn(result.Principal, properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            // The client was already authenticated against the OpenIddict application store.
            // Convention: a machine client acts as the SAME-NAMED global account (by Email/handle),
            // so agents and integrations get memberships, roles and an audit identity like a human.
            var account = await tam.Db.Set<AccountEntity>()
                .FirstOrDefaultAsync(a => a.Email == request.ClientId && a.Active);
            if (account is null) return Deny();

            // A machine acts in the single tenant it is a member of; the membership check still guards.
            var membership = await tam.Db.Set<TenantMembershipEntity>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.AccountId == account.Id && m.Active);
            return SignInAsAccount(account, membership?.TenantId, request.GetScopes());
        }

        return Deny();
    }

    // ── Invite acceptance (docs/26) ───────────────────────────────────────────────────────────
    // Anonymous by design: the mailed token IS the credential. Lookups match the token's hash and
    // bypass the tenant filter — an invite is redeemed before any tenant context exists.
    private static async Task<InviteEntity?> FindPendingInviteAsync(ITamDb tam, string token)
    {
        if (token is not { Length: > 0 }) return null;
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        var invite = await tam.Db.Set<InviteEntity>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAtIso == null);
        if (invite is null) return null;
        return IsoTime.TryParse(invite.ExpiresAtIso, out var expires)
            && expires > DateTimeOffset.UtcNow ? invite : null;
    }

    private static async Task<IResult> InviteForm(HttpContext http, ITamDb tam, TamModel model)
    {
        var culture = TamAspNetCore.ResolveCulture(http, model);
        var token = http.Request.Query["token"].ToString();
        return await FindPendingInviteAsync(tam, token) is null
            ? Html(AuthPages.Message(model, culture, "auth.invite-invalid"))
            : Html(AuthPages.InvitePage(model, culture, token, error: false));
    }

    private static async Task<IResult> InviteSubmit(HttpContext http, ITamDb tam, TamModel model)
    {
        var culture = TamAspNetCore.ResolveCulture(http, model);
        var form = await http.Request.ReadFormAsync();
        var token = form["token"].ToString();
        var password = form["password"].ToString();

        var invite = await FindPendingInviteAsync(tam, token);
        if (invite is null) return Html(AuthPages.Message(model, culture, "auth.invite-invalid"));
        if (password.Length < 8) return Html(AuthPages.InvitePage(model, culture, token, error: true));

        var account = await tam.Db.Set<AccountEntity>()
            .FirstOrDefaultAsync(a => a.Id == invite.AccountId);
        if (account is null) return Html(AuthPages.Message(model, culture, "auth.invite-invalid"));

        account.PasswordHash = TamPasswords.Hash(password);
        account.Active = true;
        invite.AcceptedAtIso = IsoTime.Now();
        await tam.Db.SaveChangesAsync();
        return Results.Redirect("/");
    }

    private static async Task<IResult> Logout(HttpContext http)
    {
        await http.SignOutAsync(LoginScheme);
        return Results.Redirect("/");
    }

    private static IResult Deny() => Results.Forbid(
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
        }),
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static IResult Html(string html) => Results.Content(html, "text/html; charset=utf-8");


    /// <summary>Registers the OpenIddict tables on the application's DbContext.</summary>
    public static ModelBuilder UseTamOpenIddict(this ModelBuilder modelBuilder)
    {
        modelBuilder.UseOpenIddict();
        return modelBuilder;
    }

    /// <summary>Idempotently registers a machine client (agents, integrations). The client acts as
    /// the same-named global account — define that account with the roles it needs.</summary>
    public static async Task EnsureClientAsync(
        IServiceProvider services, string clientId, string clientSecret)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is not null) return;
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
            },
        });
    }

    /// <summary>Idempotently registers the interactive public client (the SPA): Authorization Code +
    /// PKCE, no secret. Redirect URIs are the app origins the code may be returned to.</summary>
    public static async Task EnsureSpaClientAsync(
        IServiceProvider services, string clientId, params string[] redirectUris)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is not null) return;
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var uri in redirectUris) descriptor.RedirectUris.Add(new Uri(uri));
        await manager.CreateAsync(descriptor);
    }

    private static class TokenValidationConstants
    {
        public const string AuthenticationType = "tam-openiddict";
    }
}
