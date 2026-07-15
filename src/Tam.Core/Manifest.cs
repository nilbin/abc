namespace Tam;

/// <summary>
/// The effective manifest served to clients (docs/12): model + per-tenant extension overlay + catalogs.
/// Field descriptors are shape-identical for compiled and tenant-defined fields — consumers cannot
/// tell the authoring channel apart (docs/15).
/// </summary>
public sealed record ManifestDto(
    string Version,
    string DefaultCulture,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs,
    IReadOnlyDictionary<string, ManifestOperation> Operations,
    IReadOnlyDictionary<string, ManifestView> Views,
    IReadOnlyDictionary<string, ManifestForm> Forms,
    IReadOnlyDictionary<string, ManifestGrid> Grids,
    IReadOnlyDictionary<string, IReadOnlyList<ManifestField>> Extensions,
    IReadOnlyList<string> Permissions,
    long Revision)
{
    /// <summary>The requesting actor's effective permissions — the user overlay of docs/12.</summary>
    public IReadOnlyList<string> ActorPermissions { get; init; } = [];

    /// <summary>Plugins ACTIVE for this tenant (docs/22). Inactive plugins' contributions are
    /// omitted from every collection above — for that tenant they do not exist.</summary>
    public IReadOnlyList<string> Plugins { get; init; } = [];
}

public sealed record ManifestField(
    string Name,
    string LabelKey,
    string Type,
    string WireKind,
    string? Format,
    bool Required,
    int? MaxLength,
    IReadOnlyList<string>? Options,
    bool ChangeSet,
    bool Extension = false,
    Px? VisibleWhen = null,
    Px? RequiredWhen = null,
    string? Renderer = null,
    string? Sensitive = null);   // docs/27 D-A3: present only for actors holding this atom

public sealed record ManifestOperation(
    string Permission,
    string TitleKey,
    IReadOnlyList<ManifestField> Fields,
    string? ExtensibleEntity)
{
    public IReadOnlyList<ManifestField> OutputFields { get; init; } = [];

    public string? Plugin { get; init; }

    /// <summary>Plugins that gate this operation with declared preconditions (docs/22 P2) —
    /// the coupling is visible here and in impact reports, never magic.</summary>
    public IReadOnlyList<string> GatedBy { get; init; } = [];
}

public sealed record ManifestView(
    string Permission,
    IReadOnlyList<ManifestField> QueryFields,
    IReadOnlyList<ManifestField> ResultFields,
    IReadOnlyList<string> Sortable,
    IReadOnlyList<string> Filterable,
    string? DefaultSort,
    bool DefaultSortDescending,
    string? ExtensibleEntity)
{
    public string? Plugin { get; init; }
}

public sealed record ManifestForm(
    string Operation,
    IReadOnlyList<ManifestField> Fields,
    bool IncludeExtensions,
    IReadOnlyList<string> ServerDependencies)
{
    public string? Plugin { get; init; }
}

public sealed record ManifestGrid(
    string View,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> RowActions,
    IReadOnlyList<string> ToolbarActions,
    bool IncludeExtensions)
{
    public string? Plugin { get; init; }
}

public static class ManifestBuilder
{
    /// <param name="activePlugins">Plugins active for the requesting tenant; null means include
    /// everything (model export, baseline check). Inactive plugins' contributions are omitted —
    /// for that tenant they do not exist (docs/22).</param>
    public static ManifestDto Build(
        TamModel model,
        IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>> extensionOverlay,
        long revision,
        IReadOnlySet<string>? activePlugins = null)
    {
        bool Included(string? plugin) =>
            plugin is null || activePlugins is null || activePlugins.Contains(plugin);

        var operations = model.Operations
            .Where(kv => Included(kv.Value.Plugin))
            .ToDictionary(
            kv => kv.Key,
            kv => new ManifestOperation(
                kv.Value.Permission,
                kv.Value.TitleKey,
                kv.Value.InputFields.Select(ToField).ToList(),
                kv.Value.ExtensibleEntity is { } e ? TamModel.EntityKey(e) : null)
            {
                OutputFields = kv.Value.OutputType is { } output
                    ? FieldModel.FromRecord(output).Select(ToField).ToList()
                    : [],
                Plugin = kv.Value.Plugin,
                GatedBy = model.Gates.TryGetValue(kv.Key, out var opGates)
                    ? opGates.Where(g => Included(g.PluginId))
                        .Select(g => g.PluginId).Distinct().Order().ToList()
                    : [],
            });

        var views = model.Views
            .Where(kv => Included(kv.Value.Plugin))
            .ToDictionary(
            kv => kv.Key,
            kv => new ManifestView(
                kv.Value.Permission,
                kv.Value.QueryFields.Select(ToField).ToList(),
                kv.Value.ResultFields.Select(ToField).ToList(),
                kv.Value.Capabilities.Sortable,
                kv.Value.Capabilities.Filterable,
                kv.Value.Capabilities.DefaultSort,
                kv.Value.Capabilities.DefaultSortDescending,
                kv.Value.ExtensibleEntity is { } e ? TamModel.EntityKey(e) : null)
            {
                Plugin = kv.Value.Plugin,
            });

        var forms = model.Forms
            .Where(kv => Included(kv.Value.Plugin))
            .ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var operation = model.Operations[kv.Value.OperationId];
                var byName = operation.InputFields.ToDictionary(f => f.WireName);
                var fields = kv.Value.Fields.Select(config =>
                {
                    var field = ToField(byName[config.WireName]);
                    return field with
                    {
                        Renderer = config.Renderer,
                        VisibleWhen = config.VisibleWhen,
                        RequiredWhen = config.RequiredWhen,
                    };
                }).ToList();

                var serverDeps = model.DerivationsFor(operation.InputType)
                    .SelectMany(d => d.DependsOn).Distinct().ToList();

                return new ManifestForm(kv.Value.OperationId, fields, kv.Value.IncludeExtensions, serverDeps)
                {
                    Plugin = kv.Value.Plugin,
                };
            });

        var grids = model.Grids
            .Where(kv => Included(kv.Value.Plugin))
            .ToDictionary(
            kv => kv.Key,
            kv => new ManifestGrid(
                kv.Value.ViewId, kv.Value.Columns, kv.Value.RowActions,
                kv.Value.ToolbarActions, kv.Value.IncludeExtensions)
            {
                Plugin = kv.Value.Plugin,
            });

        var extensions = extensionOverlay.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ManifestField>)kv.Value
                .Where(f => f.State is ExtensionFieldState.Active or ExtensionFieldState.Deprecated)
                .Select(ToField).ToList());

        var catalogs = model.Locales.Cultures.ToDictionary(
            c => c,
            c => model.Locales.Catalog(c));

        return new ManifestDto(
            "1", model.DefaultCulture, catalogs, operations, views, forms, grids,
            extensions, model.Permissions, revision)
        {
            Plugins = model.Plugins.Keys
                .Where(id => activePlugins is null || activePlugins.Contains(id))
                .Order().ToList(),
        };
    }

    /// <summary>
    /// Read masking for the manifest (docs/27 D-A3): every field carrying a Sensitive atom the actor
    /// does NOT hold is removed from operations, views and forms — for that actor the field does not
    /// exist (no column, no form control, no filter). Deterministic in the actor's flat permission
    /// set, so the manifest ETag (keyed on that set) stays coherent.
    /// </summary>
    public static ManifestDto MaskSensitive(ManifestDto dto, Actor actor)
    {
        bool Visible(ManifestField f) => f.Sensitive is null || actor.Can(f.Sensitive);
        IReadOnlyList<ManifestField> Keep(IReadOnlyList<ManifestField> fields) =>
            fields.All(Visible) ? fields : fields.Where(Visible).ToList();

        return dto with
        {
            Operations = dto.Operations.ToDictionary(kv => kv.Key, kv => kv.Value with
            {
                Fields = Keep(kv.Value.Fields),
                OutputFields = Keep(kv.Value.OutputFields),
            }),
            Views = dto.Views.ToDictionary(kv => kv.Key, kv => kv.Value with
            {
                QueryFields = Keep(kv.Value.QueryFields),
                ResultFields = Keep(kv.Value.ResultFields),
            }),
            Forms = dto.Forms.ToDictionary(kv => kv.Key, kv => kv.Value with
            {
                Fields = Keep(kv.Value.Fields),
            }),
        };
    }

    public static ManifestField ToField(FieldModel f) => new(
        f.WireName,
        f.LabelKey,
        f.Semantic.Key,
        f.Semantic.WireKind,
        f.Semantic.Format,
        f.Required,
        f.Semantic.MaxLength,
        f.EnumOptions,
        f.IsChangeSet)
    {
        Sensitive = f.SensitivePermission,
    };

    public static ManifestField ToField(ExtensionFieldSpec spec) => new(
        spec.Key,
        $"ext.{spec.Key}",           // resolved from the spec's own Labels, merged into catalogs per tenant
        spec.Semantic.Key,
        spec.Semantic.WireKind,
        spec.Semantic.Format,
        spec.Required,
        spec.MaxLength,
        spec.Options,
        ChangeSet: false,
        Extension: true);
}
