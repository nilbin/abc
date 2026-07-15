using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static class SubscriptionFindings
{
    public static readonly FindingFactory NotEntitled = Finding.Error("subscriptions.not-entitled");
    public static readonly FindingFactory SeatLimit = Finding.Error("subscriptions.seat-limit");
}

/// <summary>
/// Reads a tenant's subscription (docs/24). A missing row is the free default — the framework
/// works fully without a billing system, and enforcement degrades safely to that baseline.
/// </summary>
public static class Subscriptions
{
    public static async Task<SubscriptionEntity> ForAsync(DbContext db, string tenantId, CancellationToken ct)
        => await db.Set<SubscriptionEntity>().FindAsync([tenantId], ct)
            ?? new SubscriptionEntity { TenantId = tenantId };   // free plan default
}
