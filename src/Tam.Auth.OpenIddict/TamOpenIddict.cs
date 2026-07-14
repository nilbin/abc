using System.Security.Claims;
using Microsoft.AspNetCore;
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
/// The framework's own authentication (docs: self-sufficiency): an embedded OpenIddict server
/// issuing tokens against the framework user store — password grant for humans, client
/// credentials for agents and integrations. This is ONE implementation behind the
/// <see cref="IActorProvider"/> seam: any external IdP that produces claims plugs in through
/// <see cref="ClaimsActorProvider"/> instead, and anything else replaces the provider entirely.
/// </summary>
public static class TamOpenIddict
{
    /// <summary>Registers the embedded token server + validation and swaps the actor provider
    /// to claims-based resolution. Call after AddTam.</summary>
    public static IServiceCollection AddTamOpenIddict<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<TDbContext>())
            .AddServer(options =>
            {
                options.SetTokenEndpointUris("/connect/token");
                // Only the grants the token endpoint actually redeems (password + client
                // credentials). Refresh was advertised but the exchange never handled it, so a
                // client that took a refresh token got invalid_grant — don't claim what we can't do.
                options.AllowPasswordFlow()
                       .AllowClientCredentialsFlow();
                options.AcceptAnonymousClients();   // the SPA uses the password grant directly

                // Development keys: ephemeral, tokens do not survive a restart. Production
                // supplies certificates here — the one deliberate configuration seam.
                options.AddEphemeralEncryptionKey()
                       .AddEphemeralSigningKey();
                options.DisableAccessTokenEncryption();

                // TLS is the host's concern (reverse proxy / production certs); the framework
                // must work on plain HTTP in development.
                options.UseAspNetCore()
                       .EnableTokenEndpointPassthrough()
                       .DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddAuthorization();
        services.AddSingleton<IActorProvider, ClaimsActorProvider>();
        return services;
    }

    /// <summary>Maps the token endpoint and enables authentication. Call before MapTam.</summary>
    public static WebApplication MapTamAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/connect/token", Exchange);
        return app;
    }

    private static async Task<IResult> Exchange(HttpContext http, ITamDb tam)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        if (request.IsPasswordGrantType())
        {
            // The account is platform-global (docs/26): authenticate the Email/handle across the
            // whole platform, not within a tenant. The request's active tenant — and thus the
            // actor's grants — is resolved per request from the account's memberships.
            var account = await tam.Db.Set<AccountEntity>().SingleOrDefaultAsync(
                a => a.Email == request.Username && a.Active);
            if (account is null || request.Password is null
                || !TamPasswords.Verify(request.Password, account.PasswordHash))
                return Deny();

            return SignIn(account.Id.ToString(), account.DisplayName, request);
        }

        if (request.IsClientCredentialsGrantType())
        {
            // The client was already authenticated against the OpenIddict application store.
            // Convention: a machine client acts as the SAME-NAMED global account (by Email/handle),
            // so agents and integrations get memberships, roles and an audit identity like a human.
            var account = await tam.Db.Set<AccountEntity>().SingleOrDefaultAsync(
                a => a.Email == request.ClientId && a.Active);
            if (account is null) return Deny();

            return SignIn(account.Id.ToString(), account.DisplayName, request);
        }

        return Deny();
    }

    private static IResult SignIn(string accountId, string displayName, OpenIddictRequest request)
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationConstants.AuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        // Subject is the global account id; access to a given tenant is proven by the account's
        // membership in it, checked per request (ClaimsActorProvider) — so no tenant claim is bound
        // into the token. One account, many tenants (docs/26).
        identity.SetClaim(Claims.Subject, accountId);
        identity.SetClaim(Claims.Name, displayName);
        identity.SetClaim(ClaimsActorProvider.AccountClaim, accountId);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        return Results.SignIn(principal, properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Deny() => Results.Forbid(
        properties: new Microsoft.AspNetCore.Authentication.AuthenticationProperties(
            new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
            }),
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>Registers the OpenIddict tables on the application's DbContext.</summary>
    public static ModelBuilder UseTamOpenIddict(this ModelBuilder modelBuilder)
    {
        modelBuilder.UseOpenIddict();
        return modelBuilder;
    }

    /// <summary>Idempotently registers a machine client (agents, integrations). The client
    /// acts as the same-named framework user — define that user with the roles it needs.</summary>
    public static async Task EnsureClientAsync(
        IServiceProvider services, string clientId, string clientSecret)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId) is not null) return;
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
            },
        });
    }

    private static class TokenValidationConstants
    {
        public const string AuthenticationType = "tam-openiddict";
    }
}
