using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>
/// The request's ambient tenant, feeding the EF global query filter (see <see cref="ITenantScoped"/>).
/// Scoped: set once per request from <see cref="ITenantProvider"/>, read by the DbContext's filter.
/// Null outside a request (background jobs, startup seed), where callers use IgnoreQueryFilters.
/// </summary>
public sealed class TenantScope
{
    public string? Current { get; set; }
}

public static class TenantScopeMiddleware
{
    /// <summary>
    /// Resolves the tenant for each request and pins it into the ambient <see cref="TenantScope"/>
    /// BEFORE anything touches the database — actor/role resolution already runs tenant-filtered,
    /// so this must sit ahead of it in the pipeline.
    /// </summary>
    public static IApplicationBuilder UseTamTenantScope(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var scope = http.RequestServices.GetRequiredService<TenantScope>();
            scope.Current = http.RequestServices.GetRequiredService<ITenantProvider>().GetTenant(http).Value;
            await next(http);
        });
}
