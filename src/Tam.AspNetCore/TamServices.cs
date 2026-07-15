using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static partial class TamAspNetCore
{
    public static IServiceCollection AddTam<TDbContext>(
        this IServiceCollection services, TamModel model,
        Action<TamIntegrationOptions>? configureIntegrations = null)
        where TDbContext : DbContext
    {
        services.AddSingleton(model);
        services.AddScoped(sp => new OperationExecutor(model, sp, s => s.GetRequiredService<TDbContext>()));
        services.AddScoped(sp => new ViewExecutor(model, sp));
        services.AddScoped(sp => new ResolveExecutor(model, sp.GetRequiredService<OperationExecutor>(), sp));
        services.AddScoped<IExtensionRegistry>(sp => new PluginAwareExtensionRegistry(
            new EfExtensionRegistry(sp.GetRequiredService<TDbContext>()), model, sp.GetRequiredService<TDbContext>(), sp));
        services.AddScoped<ITamDb>(sp => new TamDb(sp.GetRequiredService<TDbContext>()));
        // Plugin handler construction (gates, effect handlers, parked work): ctor injection from
        // the resolving scope — request scope for gates, tenant-pinned scopes for the rest.
        services.AddScoped<ITamActivator, TamActivator>();
        // People lookups for plugins (assign/notify): the sanctioned seam over identity tables.
        services.AddScoped<ITamDirectory, TamDirectory>();
        // Sanctioned envelope replay (docs/28 approvals seam 3): singleton because it always
        // executes in a fresh pinned scope of its own — never the caller's (whose transaction
        // may be the very one the parked envelope must be independent of).
        services.AddSingleton(sp => new EnvelopeReplay(model, sp, s => s.GetRequiredService<TDbContext>()));
        // Ambient tenant for the EF global query filter (docs: tenant isolation is enforced once at
        // the model, not re-filtered at every call site). Set per request by UseTamTenantScope.
        services.AddScoped<TenantScope>();
        // Request-scoped memoization of plugin activation (read 3-4× per request across existence,
        // gate, overlay and manifest) — collapses those to one query and keeps them coherent.
        services.AddScoped<ActivationCache>();

        // Outbound email seam (invites): TryAdd so a deployment's real transport wins when
        // registered first; the default logs the message — the dev inbox.
        services.TryAddSingleton<ITamEmail, LogTamEmail>();
        services.AddHttpContextAccessor();   // invite links derive their origin from the request

        // Secrets vault (docs/25): ASP.NET Data Protection encrypts at rest. The key ring is
        // persisted in the shared database (via the app's DbContext when it implements
        // IDataProtectionKeyContext) so it survives restarts and is shared across instances —
        // otherwise the default ephemeral ring orphans every stored secret on redeploy. A stable
        // application name keys the ring; production may wrap it with Azure KV / AWS KMS.
        var dp = services.AddDataProtection().SetApplicationName("Tam");
        // AddTam is unconstrained, so persist-to-DbContext (which needs TContext :
        // IDataProtectionKeyContext) is invoked reflectively when the app opts in by
        // implementing the interface. Apps that don't get the platform default and a warning.
        if (typeof(Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext)
            .IsAssignableFrom(typeof(TDbContext)))
        {
            typeof(Microsoft.AspNetCore.DataProtection.EntityFrameworkCoreDataProtectionExtensions)
                .GetMethod(nameof(Microsoft.AspNetCore.DataProtection
                    .EntityFrameworkCoreDataProtectionExtensions.PersistKeysToDbContext))!
                .MakeGenericMethod(typeof(TDbContext))
                .Invoke(null, [dp]);
        }
        services.AddScoped(sp => new SecretVault(
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            sp.GetRequiredService<ITamDb>()));

        // The outbound client's destination is tenant-controlled, so it is SSRF-guarded (blocks
        // private/link-local egress) and never follows redirects — a 302 could bounce a secret-bearing
        // request to an attacker host. A deployment reaching real internal targets opts in explicitly.
        var integrationOptions = new TamIntegrationOptions();
        configureIntegrations?.Invoke(integrationOptions);
        services.AddSingleton(integrationOptions);
        services.AddHttpClient("tam-integrations", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = IntegrationEgress.Guard(integrationOptions),
            });

        // One retry policy shared by the inbound inbox and the outbound queue (docs/25): same
        // backoff, same dead-letter cap. The outbound retry driver drains failed pushes on its own
        // cadence; the inbox drains on the next inbound call but honours the same backoff gate.
        var retryPolicy = new RetryPolicy(
            integrationOptions.RetryBaseDelay, integrationOptions.RetryMaxDelay, integrationOptions.MaxAttempts);
        services.AddSingleton(retryPolicy);
        services.AddHostedService(sp => new IntegrationRetryDriver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            model, retryPolicy, integrationOptions.RetryDriverInterval));

        // Housekeeping: trim completed transient history so the hot loops don't scan unbounded tables.
        services.AddHostedService(sp => new RetentionJanitor(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            integrationOptions));

        // Scheduler for outbound integrations (docs/25): one lightweight loop, no external deps.
        services.AddHostedService(sp => new IntegrationScheduler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            model));

        services.AddSingleton<IActorProvider, DevActorProvider>();
        services.AddSingleton<ITenantProvider>(new FixedTenantProvider("demo"));
        services.AddSingleton<EffectBroadcaster>();
        // Live-refresh backplane: in-process by default. A Postgres deployment registers the NOTIFY
        // adapter after AddTam (last registration wins) for cross-instance SSE (docs/12).
        services.AddSingleton<IEffectBackplane, LocalEffectBackplane>();
        services.AddHostedService(sp => new OutboxDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            s => s.GetRequiredService<TDbContext>(),
            model));
        return services;
    }
}
