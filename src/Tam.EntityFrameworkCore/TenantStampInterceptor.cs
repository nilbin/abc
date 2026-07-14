using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tam.EntityFrameworkCore;

/// <summary>
/// Stamps <see cref="ITenantScoped.TenantId"/> on inserted rows from the DbContext's ambient tenant
/// — the write-side mirror of the global read filter, so application code never hand-writes
/// <c>TenantId = currentTenant</c>. Only fills a blank value and only when a request tenant is in
/// scope, so a background or seed insert that sets TenantId explicitly (cross-tenant) is untouched.
/// </summary>
public sealed class TenantStampInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private static void Stamp(DbContext? context)
    {
        if (context is not ITenantScopeContext { CurrentTenantId: { Length: > 0 } tenant }) return;
        foreach (var entry in context.ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State != EntityState.Added) continue;
            var property = entry.Property(nameof(ITenantScoped.TenantId));
            if (property.CurrentValue is null or "")
                property.CurrentValue = tenant;
        }
    }
}
