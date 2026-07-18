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
    /// list-and-detail shape without hand-written React. Plugins may declare pages for THEIR
    /// OWN aggregates (review round 4; PLG001 namespaces the id, activation filters the
    /// manifest) — existence is the declarer's, PLACEMENT stays the host's and tenant's.</summary>
    public TamModelBuilder Page(string id, Action<PageBuilder> configure)
    {
        var builder = new PageBuilder();
        configure(builder);
        pages[id] = builder.Build(id) with { Plugin = currentPlugin };
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

    /// <summary>Registers a reach kind (docs/35): the provider class answering containment and
    /// search for people-set references of this kind. Host/framework kinds are bare
    /// (<c>user</c>, <c>role</c>, <c>tenant</c>); a plugin's kinds sit under its id prefix and
    /// are activation-gated at resolution (D-R3). Grammar, uniqueness and the prefix rule are
    /// REACH001, at declaration.</summary>
    public TamModelBuilder ReachProvider<TProvider>(string kind)
        where TProvider : class, IReachProvider
    {
        if (!ReachRef.IsKind(kind))
            throw new InvalidOperationException(
                $"REACH001: reach kind '{kind}' must be one or more dot-separated slugs.");
        if (currentPlugin is not null && !kind.StartsWith(currentPlugin + ".", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"REACH001: plugin '{currentPlugin}' must declare reach kinds under its prefix ('{currentPlugin}.…'), got '{kind}'.");
        if (!reaches.TryAdd(kind, new ReachDefinition(kind, currentPlugin, typeof(TProvider))))
            throw new InvalidOperationException($"REACH001: reach kind '{kind}' is declared twice.");
        return this;
    }

    /// <summary>Declares a MAGIC FOLDER (docs/35): when the named event commits, the documents
    /// package materializes the folder rendered from the template — "{field}" placeholders bind
    /// to the event's declared payload fields (DOC001 verifies both at Build). The tree layout
    /// stays the declarer's; no handler learns about documents.</summary>
    public TamModelBuilder DocumentFolder(string eventType, string pathTemplate)
    {
        documentFolders.Add(new DocumentFolderBinding(eventType, pathTemplate));
        return this;
    }

    /// <summary>Declares a domain event contract (docs/31 D-X5): the type and payload fields
    /// EventPublished carries, each optionally kinded ("orderId:guid") — the publisher OWNS
    /// the shape, consumers' declared kinds are checked against it (PLG009), and the manifest
    /// carries it as the machine-readable contract artifact. Host events are free-named;
    /// plugin events sit under the plugin prefix (PLG001).</summary>
    public TamModelBuilder PublishesEvent(string eventType, params string[] fields)
    {
        var (bare, kinds) = ContractKinds.Parse(fields, $"PublishesEvent('{eventType}')");
        events[eventType] = new EventDeclaration(eventType, bare, currentPlugin, kinds);
        return this;
    }

    internal void Panel(string slotId, string gridId, string? headingKey, int? order,
        IReadOnlyList<(string QueryField, string ContextKey)> bind)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: panels can only be contributed by a plugin.");
        panels.Add(new PanelContribution(slotId, gridId, currentPlugin, bind, headingKey, order));
    }

    internal void RequireEvent(string eventType, IReadOnlyList<string> declaredFields)
    {
        if (currentPlugin is null)
            throw new InvalidOperationException("PLG005: event requirements can only be declared by a plugin.");
        var (bare, kinds) = ContractKinds.Parse(declaredFields, $"RequiresEvent('{eventType}')");
        eventRequirements.Add(new EventRequirement(eventType, currentPlugin, bare, kinds));
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

}
