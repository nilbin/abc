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

/// <summary>
/// The inbound edge of billing (docs/24): a provider webhook maps to one call. Held by a
/// service actor (a billing integration), never tenant admins — a tenant activates plugins and
/// invites users, but does not edit its own plan.
/// </summary>
[Operation("subscriptions.set-plan")]
[Authorize("subscriptions.manage")]
public static class SetPlan
{
    public sealed record Input(
        [property: LabelKey("labels.plan")] string Plan,
        [property: LabelKey("labels.seats")] int Seats,
        [property: LabelKey("labels.entitlements")] List<string> Entitlements,
        [property: LabelKey("labels.status")] string Status = "active",
        [property: LabelKey("labels.renews-at")] string? RenewsAtIso = null);

    public sealed record Output(string Plan, int Seats);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var tenant = context.TenantId.Value;
        var subscription = await tam.Db.Set<SubscriptionEntity>().FindAsync([tenant], ct);
        if (subscription is null)
        {
            subscription = new SubscriptionEntity { TenantId = tenant };
            tam.Db.Add(subscription);
        }
        subscription.Plan = input.Plan;
        subscription.Seats = Math.Max(0, input.Seats);
        subscription.EntitlementsJson = System.Text.Json.JsonSerializer.Serialize(input.Entitlements);
        subscription.Status = input.Status;
        subscription.RenewsAtIso = input.RenewsAtIso;

        return new Output(subscription.Plan, subscription.Seats);
    }
}

/// <summary>The tenant's own plan/seats/usage — so the UI can show "4 of 5 seats, upgrade for more".</summary>
[View("subscriptions.current")]
[Authorize("subscriptions.read")]
public static class CurrentSubscription
{
    public sealed record Query();

    public sealed record Result
    {
        [LabelKey("labels.plan")]
        public string Plan { get; init; } = "";
        [LabelKey("labels.seats")]
        public int Seats { get; init; }
        [LabelKey("labels.seats-used")]
        public int SeatsUsed { get; init; }
        [LabelKey("labels.status")]
        public string Status { get; init; } = "";
        [LabelKey("labels.entitlements")]
        public string Entitlements { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var tenant = context.TenantId.Value;
        var subscription = tam.Db.Set<SubscriptionEntity>().Find(tenant)
            ?? new SubscriptionEntity { TenantId = tenant };
        var used = tam.Db.Set<TamUserEntity>().Count(x => x.Active);

        return new[]
        {
            new Result
            {
                Plan = subscription.Plan,
                Seats = subscription.Seats,
                SeatsUsed = used,
                Status = subscription.Status,
                Entitlements = subscription.EntitlementsJson,
            },
        }.AsQueryable();
    }
}
