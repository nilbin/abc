using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Subscription admin (docs/24). The ENFORCEMENT (entitlement gate, seat lease) is
/// core — a billing check can't live behind the activation it gates; this is only the surface
/// the billing provider drives.</summary>
[TamPackage("tam.subscriptions", "subscriptions")]
public sealed class TamSubscriptionsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddOperationType(typeof(SetPlan))
            .AddViewType(typeof(CurrentSubscription));
    }
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
        [LabelKey("labels.anchor-tenant")]
        public string AnchorTenantId { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        // The COVERING subscription (docs/24 hierarchy): possibly anchored on an ancestor. Seat
        // usage is the anchor's whole pool — active memberships across every covered node.
        var covering = Subscriptions.Covering(tam.Db, context.TenantId.Value);
        var covered = Subscriptions.CoveredTenants(tam.Db, covering.AnchorTenantId);
        var used = tam.Db.Set<TenantMembershipEntity>().IgnoreQueryFilters()
            .Count(m => m.Active && covered.Contains(m.TenantId));

        return new[]
        {
            new Result
            {
                Plan = covering.Subscription.Plan,
                Seats = covering.Subscription.Seats,
                SeatsUsed = used,
                Status = covering.Subscription.Status,
                Entitlements = covering.Subscription.EntitlementsJson,
                AnchorTenantId = covering.AnchorTenantId,
            },
        }.AsQueryable();
    }
}
