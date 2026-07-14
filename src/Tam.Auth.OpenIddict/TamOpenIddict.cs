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
                options.AllowPasswordFlow()
                       .AllowClientCredentialsFlow()
                       .AllowRefreshTokenFlow();
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

    private static async Task<IResult> Exchange(HttpContext http, ITamDb tam, ITenantProvider tenants)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");
        var tenant = tenants.GetTenant(http);

        if (request.IsPasswordGrantType())
        {
            var user = await tam.Db.Set<TamUserEntity>().SingleOrDefaultAsync(
                x => x.TenantId == tenant.Value && x.UserName == request.Username && x.Active);
            if (user is null || request.Password is null
                || !TamPasswords.Verify(request.Password, user.PasswordHash))
                return Deny();

            return SignIn(user.UserName, user.DisplayName, request);
        }

        if (request.IsClientCredentialsGrantType())
        {
            // The client was already authenticated against the OpenIddict application store.
            // Convention: a machine client acts as the SAME-NAMED framework user, so agents
            // and integrations get roles, permissions and an audit identity like any human.
            var user = await tam.Db.Set<TamUserEntity>().SingleOrDefaultAsync(
                x => x.TenantId == tenant.Value && x.UserName == request.ClientId && x.Active);
            if (user is null) return Deny();

            return SignIn(user.UserName, user.DisplayName, request);
        }

        return Deny();
    }

    private static IResult SignIn(string userName, string displayName, OpenIddictRequest request)
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationConstants.AuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, userName);
        identity.SetClaim(Claims.Name, displayName);
        identity.SetClaim(ClaimsActorProvider.UserClaim, userName);
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
