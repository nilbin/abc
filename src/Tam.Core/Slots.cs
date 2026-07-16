namespace Tam;

// Detail slots (docs/31 D-X4): a HOST-declared contribution point on one of its own surfaces,
// carrying record context as a wire contract — the first brick of framework-composed pages.
// The host declares the slot ONCE (id + context keys); every current and future plugin lands
// panels there without the host naming any of them. Plugins bind their own grid's query fields
// to the slot's context keys — wire names both sides, validated at Build (PLG007).

/// <summary>A host contribution point: permanent wire id (D4) + the context keys it provides
/// (e.g. "web.orders.detail" providing "orderId"). Slot ids are host wire names.</summary>
public sealed record SlotDefinition(string Id, IReadOnlyList<string> ContextKeys);

/// <summary>A plugin panel in a host slot: the plugin's OWN grid, its query fields bound to
/// the slot's context keys.</summary>
public sealed record PanelContribution(
    string SlotId, string GridId, string PluginId,
    IReadOnlyList<(string QueryField, string ContextKey)> Bind);

public sealed class SlotContextBuilder
{
    internal List<string> Keys { get; } = [];

    public SlotContextBuilder Key(string key)
    {
        Keys.Add(Naming.Camel(key));
        return this;
    }
}

public sealed class PanelBindBuilder
{
    internal List<(string QueryField, string ContextKey)> Binds { get; } = [];

    public PanelBindBuilder Query(string queryField, string fromContext)
    {
        Binds.Add((Naming.Camel(queryField), Naming.Camel(fromContext)));
        return this;
    }
}
