namespace Tam;

// The PLUGIN-only registration internals (docs/29): every method here requires an ambient
// currentPlugin (PLG005) and is reached exclusively through PluginBuilder — the host authors
// through the public fluent surface in TamModelBuilder.cs.
public sealed partial class TamModelBuilder
{
    internal void PackagedField(
        string entityKey, string key, string type, bool required, int? maxLength,
        IReadOnlyList<string>? options, bool readOnly)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: packaged fields can only be declared by a plugin.");
        packagedFields.Add((entityKey, key, type, required, maxLength, options, readOnly, currentPlugin));
    }

    internal void Gate(string operationId, Type handlerType, bool pure = false)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: gates can only be declared by a plugin.");
        gates.Add(new GateDefinition(operationId, currentPlugin, handlerType, pure));
    }

    /// <summary>Declares a framework-composed page (docs/32): a grid plus an optional record
    /// surface (detail + form + slots), rendered by the client library — the standard
    /// list-and-detail shape without hand-written React. Host-only, like nav layout.</summary>
    public TamModelBuilder Page(string id, Action<PageBuilder> configure)
    {
        if (currentPlugin is not null)
            throw new InvalidOperationException(
                "PLG005: pages are declared by the HOST — plugins contribute through slots and grid actions.");
        var builder = new PageBuilder();
        configure(builder);
        pages[id] = builder.Build(id);
        return this;
    }

    /// <summary>Declares a contribution point on one of the HOST's surfaces (docs/31 D-X4):
    /// a permanent slot id plus the record-context keys it provides. Layout stays the host's —
    /// a plugin cannot declare slots (PLG005), only fill them.</summary>
    public TamModelBuilder Slot(string slotId, Action<SlotContextBuilder>? context = null,
        bool external = false)
    {
        if (currentPlugin is not null)
            throw new InvalidOperationException(
                "PLG005: slots are declared by the HOST — plugins contribute panels into them.");
        var builder = new SlotContextBuilder();
        context?.Invoke(builder);
        slots[slotId] = new SlotDefinition(slotId, builder.Keys, external);
        return this;
    }

    /// <summary>Declares a domain event contract (docs/31 D-X5): the type and payload fields
    /// EventPublished carries. Host events are free-named; plugin events sit under the plugin
    /// prefix (PLG001). OnEffect / event triggers must target a declared event (PLG009).</summary>
    public TamModelBuilder PublishesEvent(string eventType, params string[] fields)
    {
        events[eventType] = new EventDeclaration(
            eventType, fields.Select(Naming.Camel).ToArray(), currentPlugin);
        return this;
    }

    internal void Panel(string slotId, string gridId,
        IReadOnlyList<(string QueryField, string ContextKey)> bind)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: panels can only be contributed by a plugin.");
        panels.Add(new PanelContribution(slotId, gridId, currentPlugin, bind));
    }

    internal void RequireEvent(string eventType, IReadOnlyList<string> fields)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: event requirements can only be declared by a plugin.");
        eventRequirements.Add(new EventRequirement(eventType, currentPlugin, fields));
    }

    internal void GridAction(string gridId, string operationId,
        IReadOnlyList<(string Input, string Column)> bind)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: grid actions can only be contributed by a plugin.");
        gridActions.Add(new GridActionContribution(gridId, operationId, currentPlugin, bind));
    }

    internal void RequireView(string viewId, IReadOnlyList<string> fields)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: view requirements can only be declared by a plugin.");
        viewRequirements.Add(new ViewRequirement(viewId, currentPlugin, fields));
    }

    internal void OnEffect(string eventType, Type handlerType)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: effect subscribers can only be declared by a plugin.");
        subscribers.Add(new SubscriberDefinition(eventType, currentPlugin, handlerType));
    }

    internal void Integration(
        string id, string operationId, IntegrationKeySelector key, IntegrationRowMapper map)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: integrations can only be declared by a plugin.");
        integrations.Add((id, operationId, key, map, currentPlugin));
    }

    internal void OutboundIntegration(string id, IntegrationTrigger trigger, OutboundIntegrationHandler handler)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: integrations can only be declared by a plugin.");
        outboundIntegrations.Add((id, trigger, handler, currentPlugin));
    }

    /// <summary>Binds a form. WITHOUT <paramref name="configure"/>, every operation input
    /// field appears in record declaration order — the record IS the form; configure only to
    /// subset, reorder, or attach renderers/visibility (docs/32: convention over enumeration).</summary>
}
