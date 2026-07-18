namespace Tam;

// Navigation (docs/30): a DECLARED tree in the compiled model — mode → section → page — merged
// from three voices with strict precedence: the host declares layout (modes, sections, explicit
// placement); packages/plugins contribute CONTENT plus a suggested section slug (they cannot see
// the host's layout, D-N2); the tenant overlay (v2) gets the final word. The model carries pure
// depth + kind; renderers map depth to UI slots (D-N3). Nav is discoverability, never
// authorization: visibility derives from the bound surface's permission at render (D-N6).

public enum NavNodeKind { Mode, Section, Page }

/// <summary>A page's binding — a CLOSED union (D-N6: closed targets keep overrides validatable).
/// Grid: rendered generically. Page: an app-registered custom page key. Plugin: the generic
/// "every grid this plugin contributed" page (the mechanical fallback, docs/30 D-N1).</summary>
public sealed record NavTarget(string? Grid = null, string? Page = null, string? Plugin = null);

/// <summary>One node of the compiled tree. Ids are D4-permanent (retire, never remove — tenant
/// overrides address them); labels resolve by "nav.{id}" convention (D6).</summary>
public sealed record NavNode(
    string Id,
    NavNodeKind Kind,
    string LabelKey,
    string? Icon,
    int? Order,
    NavTarget? Target,
    string? Permission,
    string? Plugin,
    IReadOnlyList<NavNode> Children)
{
    /// <summary>The well-known fallback section id: undeclared/uncollected content lands here
    /// so nothing can be authored into invisibility (D-N1).</summary>
    public const string More = "more";
}

/// <summary>A package/plugin's declared page + the semantic section slug it SUGGESTS
/// ("administration", "work", host-defined). The host and tenant decide actual placement.</summary>
public sealed record NavContribution(string Plugin, string? Suggest, NavNode Page);

/// <summary>Host-side tree author: modes at the top (D-N4), sections/pages/placements below.</summary>
public sealed class NavTreeBuilder
{
    internal List<NavNode> Modes { get; } = [];

    public NavTreeBuilder Mode(string id, Action<NavNodeBuilder>? children = null,
        string? icon = null, int? order = null)
    {
        var builder = new NavNodeBuilder();
        children?.Invoke(builder);
        Modes.Add(new NavNode(id, NavNodeKind.Mode, LabelKeys.Nav(id), icon, order,
            null, null, null, builder.Nodes));
        return this;
    }
}

public sealed class NavNodeBuilder
{
    internal List<NavNode> Nodes { get; } = [];

    /// <summary>A grouping. A section whose id matches contributions' SUGGEST slugs collects
    /// them (declaration order) after any explicit children.</summary>
    public NavNodeBuilder Section(string id, Action<NavNodeBuilder>? children = null,
        string? icon = null, int? order = null)
    {
        var builder = new NavNodeBuilder();
        children?.Invoke(builder);
        Nodes.Add(new NavNode(id, NavNodeKind.Section, LabelKeys.Nav(id), icon, order,
            null, null, null, builder.Nodes));
        return this;
    }

    /// <summary>A navigable page bound to ONE target: a grid (rendered generically) or a
    /// registered custom page key (which requires an explicit permission from the existing
    /// catalogue — there is no manifest surface to derive it from).</summary>
    public NavNodeBuilder Page(string id, string? grid = null, string? page = null,
        string? permission = null, string? icon = null, int? order = null,
        Action<NavNodeBuilder>? children = null)
    {
        var builder = new NavNodeBuilder();
        children?.Invoke(builder);
        Nodes.Add(new NavNode(id, NavNodeKind.Page, LabelKeys.Nav(id), icon, order,
            new NavTarget(Grid: grid, Page: page), permission, null, builder.Nodes));
        return this;
    }

    /// <summary>Explicitly places a CONTRIBUTED node (by its id) here — the host overriding a
    /// suggestion. Position is the host's too: the contribution's order is replaced by
    /// <paramref name="order"/> (default declaration order). Unknown ids are NAV003.</summary>
    public NavNodeBuilder Place(string contributedId, int? order = null)
    {
        Nodes.Add(new NavNode(contributedId, NavNodeKind.Page, LabelKeys.Nav(contributedId),
            null, order, PlacementMarker, null, null, []));
        return this;
    }

    internal static readonly NavTarget PlacementMarker = new();
}

/// <summary>Contribution author for packages/plugins: content + suggestion, never placement.</summary>
public sealed class NavContributionBuilder
{
    private readonly string plugin;
    internal List<NavContribution> Contributions { get; } = [];

    internal NavContributionBuilder(string plugin) => this.plugin = plugin;

    public NavContributionBuilder Page(string id, string? grid = null, string? page = null,
        string? permission = null, string? icon = null, int? order = null,
        string? suggest = null)
    {
        Contributions.Add(new NavContribution(plugin, suggest,
            new NavNode(id, NavNodeKind.Page, LabelKeys.Nav(id), icon, order,
                new NavTarget(Grid: grid, Page: page), permission, plugin, [])));
        return this;
    }

    /// <summary>Deliberately NO nav: the plugin's surfaces are reached elsewhere (record tabs,
    /// a host-registered page). Declaring nav — even empty — graduates the plugin (docs/30
    /// D-N1), so the mechanical More-page safety net does not resurrect its grids.</summary>
    public NavContributionBuilder None() => this;
}
