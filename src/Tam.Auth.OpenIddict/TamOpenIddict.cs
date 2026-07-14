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
                       .SetEndSessionEndpointUris("/connect/logout");

                // Humans: Authorization Code + PKCE (PKCE required — the SPA is a public client with
                // no secret). Machines: client credentials. No password grant (OAuth 2.1).
                options.AllowAuthorizationCodeFlow()
                       .AllowClientCredentialsFlow();
                options.RequireProofKeyForCodeExchange();

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
        var culture = Culture(http, model);

        var login = await http.AuthenticateAsync(LoginScheme);
        if (login.Principal?.FindFirst(ClaimsActorProvider.AccountClaim)?.Value is not { } subject
            || !Guid.TryParse(subject, out var accountId))
        {
            var returnUrl = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
            return Html(AuthPages.LoginPage(model, culture, returnUrl, error: false));
        }

        // The account's tenants are cross-tenant by nature (that's the whole point of a global
        // account), so this lists memberships across every tenant — filter opt-out.
        var memberships = await tam.Db.Set<TenantMembershipEntity>().IgnoreQueryFilters()
            .Where(m => m.AccountId == accountId && m.Active)
            .ToListAsync();
        if (memberships.Count == 0)
            return Html(AuthPages.Message(model, culture, "auth.no-access"));

        var memberTenantIds = memberships.Select(m => m.TenantId).ToHashSet();
        var requested = request.GetParameter("tenant")?.Value?.ToString();
        var chosen = requested is { Length: > 0 } && memberTenantIds.Contains(requested)
            ? requested
            : memberships.Count == 1 ? memberships[0].TenantId : null;

        if (chosen is null)
        {
            var tenants = await tam.Db.Set<TenantEntity>()
                .Where(t => memberTenantIds.Contains(t.Id)).ToListAsync();
            var displayById = tenants.ToDictionary(t => t.Id, t => t.DisplayName);
            var options = memberships
                .Select(m => (m.TenantId, Display: displayById.GetValueOrDefault(m.TenantId, m.TenantId)))
                .ToList();
            return Html(AuthPages.TenantPicker(model, culture, http.Request.Query, options));
        }

        var account = await tam.Db.Set<AccountEntity>().FirstOrDefaultAsync(a => a.Id == accountId && a.Active);
        if (account is null) return Html(AuthPages.Message(model, culture, "auth.no-access"));

        var identity = new ClaimsIdentity(
            TokenValidationConstants.AuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, accountId.ToString());
        identity.SetClaim(Claims.Name, account.DisplayName);
        identity.SetClaim(ClaimsActorProvider.AccountClaim, accountId.ToString());
        identity.SetClaim(ClaimsActorProvider.ActiveTenantClaim, chosen);
        identity.SetScopes(request.GetScopes());
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
        var culture = Culture(http, model);

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

        if (request.IsAuthorizationCodeGrantType())
        {
            // The code carries the principal minted at the authorization endpoint (subject = account,
            // active tenant, scopes). OpenIddict has already validated the PKCE code_verifier.
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

            var identity = new ClaimsIdentity(
                TokenValidationConstants.AuthenticationType, Claims.Name, Claims.Role);
            identity.SetClaim(Claims.Subject, account.Id.ToString());
            identity.SetClaim(Claims.Name, account.DisplayName);
            identity.SetClaim(ClaimsActorProvider.AccountClaim, account.Id.ToString());
            // A machine acts in the single tenant it is a member of; the membership check still guards.
            var membership = await tam.Db.Set<TenantMembershipEntity>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.AccountId == account.Id && m.Active);
            if (membership is not null)
                identity.SetClaim(ClaimsActorProvider.ActiveTenantClaim, membership.TenantId);
            identity.SetScopes(request.GetScopes());
            identity.SetDestinations(_ => [Destinations.AccessToken]);
            return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Deny();
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

    private static string Culture(HttpContext http, TamModel model)
    {
        var header = http.Request.Headers.AcceptLanguage.ToString();
        var first = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?.Split(';')[0].Trim();
        return string.IsNullOrEmpty(first) ? model.DefaultCulture : first;
    }

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
                Permissions.GrantTypes.AuthorizationCode,
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

/// <summary>
/// The framework-owned interactive auth pages (docs/26): a minimal, self-contained, localized login
/// and tenant picker so every Tam app gets a sign-in surface for free without shipping display text
/// in code — every string comes from the locale catalog (L10N). Inline styles only; no assets.
/// </summary>
internal static class AuthPages
{
    private static string T(TamModel model, string key, string culture) =>
        model.Locales.Lookup(key, culture) ?? key;

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Style =
        "body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:#f6f7f9;" +
        "margin:0;display:flex;min-height:100vh;align-items:center;justify-content:center}" +
        ".card{background:#fff;border:1px solid #e6e8eb;border-radius:12px;padding:28px;width:320px;" +
        "box-shadow:0 1px 3px rgba(0,0,0,.06)}.brand{text-align:center;color:#4c5bd4;font-size:22px;" +
        "font-weight:700;margin-bottom:18px}label{display:block;font-size:13px;color:#40474f;margin:12px 0 4px}" +
        "input[type=email],input[type=password]{width:100%;box-sizing:border-box;padding:9px 10px;" +
        "border:1px solid #d3d7db;border-radius:8px;font-size:14px}button{width:100%;margin-top:18px;" +
        "padding:10px;background:#4c5bd4;color:#fff;border:0;border-radius:8px;font-size:14px;cursor:pointer}" +
        ".err{color:#c02b2b;font-size:13px;margin-top:12px}.opt{display:flex;align-items:center;gap:10px;" +
        "padding:11px;border:1px solid #d3d7db;border-radius:8px;margin-top:10px;cursor:pointer}" +
        ".opt:hover{border-color:#4c5bd4}";

    public static string LoginPage(TamModel model, string culture, string returnUrl, bool error)
    {
        var err = error
            ? $"<div class=err>{Enc(T(model, "auth.failed", culture))}</div>"
            : "";
        return $"""
        <!doctype html><html lang="{Enc(culture)}"><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{Enc(T(model, "auth.sign-in", culture))}</title><style>{Style}</style></head><body>
        <form class=card method=post action="/connect/authorize/login">
          <div class=brand>&#9670; {Enc(T(model, "app.title", culture))}</div>
          <input type=hidden name=returnUrl value="{Enc(returnUrl)}">
          <label>{Enc(T(model, "auth.email", culture))}</label>
          <input type=email name=email autofocus autocomplete=username>
          <label>{Enc(T(model, "labels.password", culture))}</label>
          <input type=password name=password autocomplete=current-password>
          {err}
          <button type=submit>{Enc(T(model, "auth.sign-in", culture))}</button>
        </form></body></html>
        """;
    }

    public static string TenantPicker(
        TamModel model, string culture, IQueryCollection query, IReadOnlyList<(string Id, string Display)> tenants)
    {
        // Carry every original OAuth parameter forward as a hidden field; the chosen tenant is added
        // as one more, so re-hitting the authorization endpoint completes with the account already
        // signed in via the cookie.
        var hidden = new StringBuilder();
        foreach (var (key, value) in query)
        {
            if (key == "tenant") continue;
            hidden.Append($"<input type=hidden name=\"{Enc(key)}\" value=\"{Enc(value.ToString())}\">");
        }
        var options = new StringBuilder();
        foreach (var (id, display) in tenants)
            options.Append(
                $"<label class=opt><input type=radio name=tenant value=\"{Enc(id)}\" required> {Enc(display)}</label>");
        return $"""
        <!doctype html><html lang="{Enc(culture)}"><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{Enc(T(model, "auth.pick-tenant", culture))}</title><style>{Style}</style></head><body>
        <form class=card method=get action="/connect/authorize">
          <div class=brand>&#9670; {Enc(T(model, "app.title", culture))}</div>
          <div style="font-size:14px;color:#40474f">{Enc(T(model, "auth.pick-tenant", culture))}</div>
          {hidden}{options}
          <button type=submit>{Enc(T(model, "auth.continue", culture))}</button>
        </form></body></html>
        """;
    }

    public static string Message(TamModel model, string culture, string key) => $"""
        <!doctype html><html lang="{Enc(culture)}"><head><meta charset=utf-8>
        <meta name=viewport content="width=device-width,initial-scale=1">
        <title>{Enc(T(model, "app.title", culture))}</title><style>{Style}</style></head><body>
        <div class=card><div class=brand>&#9670; {Enc(T(model, "app.title", culture))}</div>
        <div style="font-size:14px;color:#40474f">{Enc(T(model, key, culture))}</div></div></body></html>
        """;
}
