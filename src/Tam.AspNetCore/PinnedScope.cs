using Microsoft.Extensions.DependencyInjection;

namespace Tam.AspNetCore;

/// <summary>
/// Runs work in a FRESH service scope pinned to one tenant — a fresh DbContext, the ambient
/// <see cref="TenantScope"/> set before anything touches it, so the global query filter and the
/// tenant stamp behave exactly as in a request. The isolation primitive behind parked gate work
/// (docs/28 approvals seam 2) and envelope replay (seam 3): both must commit independently of
/// whatever transaction the caller is inside.
/// </summary>
internal static class PinnedScope
{
    public static async Task RunAsync(
        IServiceProvider services, string tenantId,
        Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct)
    {
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Current = tenantId;
        await work(scope.ServiceProvider, ct);
    }

    public static async Task<T> RunAsync<T>(
        IServiceProvider services, string tenantId,
        Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Current = tenantId;
        return await work(scope.ServiceProvider, ct);
    }
}
