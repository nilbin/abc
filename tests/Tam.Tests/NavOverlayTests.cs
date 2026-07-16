using Tam;
using Tam.AspNetCore.SystemOps;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// The docs/30 v2 tenant overlay: hide, reorder, relabel, move — and the dormancy rule (an
/// override whose node or move target is absent changes nothing and breaks nothing).
/// </summary>
public class NavOverlayTests
{
    private static ManifestNavNode Page(string id, int? order = null) =>
        new(id, "page", $"nav.{id}", null, order, new NavTarget(Grid: $"web.{id}"), null, null, []);

    private static ManifestNavNode Section(string id, params ManifestNavNode[] children) =>
        new(id, "section", $"nav.{id}", null, null, null, null, null, children);

    private static ManifestNavNode Mode(string id, params ManifestNavNode[] children) =>
        new(id, "mode", $"nav.{id}", null, null, null, null, null, children);

    /// <summary>web: mode "work" → [section "sales" → [orders, customers], section "admin" → [users]]</summary>
    private static ManifestDto Manifest() => new(
        "1", "en",
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["nav.orders"] = "Orders" },
            ["sv"] = new Dictionary<string, string> { ["nav.orders"] = "Ordrar" },
        },
        new Dictionary<string, ManifestOperation>(), new Dictionary<string, ManifestView>(),
        new Dictionary<string, ManifestForm>(), new Dictionary<string, ManifestGrid>(),
        new Dictionary<string, IReadOnlyList<ManifestField>>(), [], 1)
    {
        Nav = new Dictionary<string, IReadOnlyList<ManifestNavNode>>
        {
            ["web"] =
            [
                Mode("work",
                    Section("sales", Page("orders", order: 10), Page("customers", order: 20)),
                    Section("admin", Page("users"))),
            ],
        },
    };

    private static NavOverrideEntity Override(string nodeId, bool hidden = false,
        int? order = null, string? parent = null, string labelsJson = "{}") => new()
    {
        Id = Guid.NewGuid(), TenantId = "t1", NodeId = nodeId,
        Hidden = hidden, Order = order, Parent = parent, LabelsJson = labelsJson,
    };

    private static ManifestNavNode Sales(ManifestDto dto) => dto.Nav["web"][0].Children[0];

    [Fact]
    public void Hidden_removes_the_node_and_its_subtree()
    {
        var result = NavOverlay.Apply(Manifest(), [Override("customers", hidden: true)]);
        Assert.Equal(["orders"], Sales(result).Children.Select(n => n.Id));

        var section = NavOverlay.Apply(Manifest(), [Override("sales", hidden: true)]);
        Assert.Equal(["admin"], section.Nav["web"][0].Children.Select(n => n.Id));
    }

    [Fact]
    public void Order_replaces_the_declared_order()
    {
        var result = NavOverlay.Apply(Manifest(), [Override("orders", order: 99)]);
        Assert.Equal(99, Sales(result).Children.Single(n => n.Id == "orders").Order);
    }

    [Fact]
    public void Parent_moves_the_page_under_the_named_section()
    {
        var result = NavOverlay.Apply(Manifest(), [Override("orders", parent: "admin")]);
        Assert.Equal(["customers"], Sales(result).Children.Select(n => n.Id));
        Assert.Equal(["users", "orders"],
            result.Nav["web"][0].Children[1].Children.Select(n => n.Id));
    }

    [Fact]
    public void Labels_merge_into_the_matching_culture_catalogs()
    {
        var result = NavOverlay.Apply(Manifest(),
            [Override("orders", labelsJson: """{"sv":"Beställningar","de":"ignored"}""")]);
        Assert.Equal("Beställningar", result.Catalogs["sv"]["nav.orders"]);
        Assert.Equal("Orders", result.Catalogs["en"]["nav.orders"]);   // untouched fallback
        Assert.False(result.Catalogs.ContainsKey("de"));               // unknown culture skipped
    }

    [Fact]
    public void An_override_for_an_absent_node_is_dormant()
    {
        // The plugin that contributed this node is deactivated — its node never reached the
        // manifest. Nothing changes; reactivation brings the tenant's placement back (docs/30).
        var before = Manifest();
        var result = NavOverlay.Apply(before,
            [Override("inspect.checklists", hidden: true, order: 5, parent: "sales")]);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(before.Nav),
            System.Text.Json.JsonSerializer.Serialize(result.Nav));
    }

    [Fact]
    public void A_move_into_a_hidden_section_is_dormant()
    {
        var result = NavOverlay.Apply(Manifest(),
            [Override("admin", hidden: true), Override("orders", parent: "admin")]);
        Assert.Equal(["orders", "customers"], Sales(result).Children.Select(n => n.Id));
    }

    [Fact]
    public void The_fingerprint_moves_with_any_override_change()
    {
        var one = NavOverlay.Fingerprint([Override("orders", hidden: true)]);
        Assert.NotEqual(one, NavOverlay.Fingerprint([Override("orders", hidden: false)]));
        Assert.NotEqual(one, NavOverlay.Fingerprint([Override("orders", hidden: true, order: 3)]));
        Assert.Equal(one, NavOverlay.Fingerprint([Override("orders", hidden: true)]));
    }
}
