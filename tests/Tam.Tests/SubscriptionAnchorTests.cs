using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// The anchor model (docs/24 hierarchy): a subscription row covers its subtree; the nearest
/// ancestor-or-self anchor governs; an anchor-less chain shares ONE free default anchored at
/// the ROOT (creating child nodes never mints fresh free seats).
/// </summary>
public class SubscriptionAnchorTests
{
    private static TenantEntity Node(string id, string path, string? parent = null) =>
        new() { Id = id, Path = path, ParentId = parent, DisplayName = id };

    private static SubscriptionEntity Anchor(string tenantId, int seats = 10) =>
        new() { TenantId = tenantId, Plan = "standard", Seats = seats };

    // group ── eu ── sales      (anchors: group; sub-anchor: eu in some tests)
    private static readonly TenantEntity Group = Node("group", "group");
    private static readonly TenantEntity Eu = Node("eu", "group.eu", "group");
    private static readonly TenantEntity Sales = Node("sales", "group.eu.sales", "eu");

    [Fact]
    public void The_nearest_ancestor_or_self_anchor_governs()
    {
        var chain = Subscriptions.Chain(Sales, "sales");
        var covering = Subscriptions.Pick(chain, [Anchor("group")]);
        Assert.Equal("group", covering.AnchorTenantId);
        Assert.Equal("standard", covering.Subscription.Plan);
    }

    [Fact]
    public void A_sub_anchor_shadows_the_root_anchor_entirely()
    {
        var chain = Subscriptions.Chain(Sales, "sales");
        var covering = Subscriptions.Pick(chain, [Anchor("group"), Anchor("eu", seats: 3)]);
        Assert.Equal("eu", covering.AnchorTenantId);
        Assert.Equal(3, covering.Subscription.Seats);   // nearest wins — no merging, no borrowing
    }

    [Fact]
    public void An_anchorless_chain_gets_one_free_default_anchored_at_the_root()
    {
        var covering = Subscriptions.Pick(Subscriptions.Chain(Sales, "sales"), []);
        Assert.Equal("group", covering.AnchorTenantId);   // the ROOT, not the asking node
        Assert.Equal("free", covering.Subscription.Plan);
    }

    [Fact]
    public void A_node_without_a_registry_row_degrades_to_itself()
    {
        var covering = Subscriptions.Pick(Subscriptions.Chain(null, "lonely"), []);
        Assert.Equal("lonely", covering.AnchorTenantId);
    }
}
