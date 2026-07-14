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
    string? Renderer = null);

public sealed record ManifestOperation(
    string Permission,
    string TitleKey,
    IReadOnlyList<ManifestField> Fields,
    string? ExtensibleEntity)
{
    public IReadOnlyList<ManifestField> OutputFields { get; init; } = [];
}

public sealed record ManifestView(
    string Permission,
    IReadOnlyList<ManifestField> QueryFields,
    IReadOnlyList<ManifestField> ResultFields,
    IReadOnlyList<string> Sortable,
    IReadOnlyList<string> Filterable,
    string? DefaultSort,
    bool DefaultSortDescending,
    string? ExtensibleEntity);

public sealed record ManifestForm(
    string Operation,
    IReadOnlyList<ManifestField> Fields,
    bool IncludeExtensions,
    IReadOnlyList<string> ServerDependencies);

public sealed record ManifestGrid(
    string View,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> RowActions,
    IReadOnlyList<string> ToolbarActions,
    bool IncludeExtensions);

public static class ManifestBuilder
{
    public static ManifestDto Build(
        TamModel model,
        IReadOnlyDictionary<string, IReadOnlyList<ExtensionFieldSpec>> extensionOverlay,
        long revision)
    {
        var operations = model.Operations.ToDictionary(
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
            });

        var views = model.Views.ToDictionary(
            kv => kv.Key,
            kv => new ManifestView(
                kv.Value.Permission,
                kv.Value.QueryFields.Select(ToField).ToList(),
                kv.Value.ResultFields.Select(ToField).ToList(),
                kv.Value.Capabilities.Sortable,
                kv.Value.Capabilities.Filterable,
                kv.Value.Capabilities.DefaultSort,
                kv.Value.Capabilities.DefaultSortDescending,
                kv.Value.ExtensibleEntity is { } e ? TamModel.EntityKey(e) : null));

        var forms = model.Forms.ToDictionary(
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

                return new ManifestForm(kv.Value.OperationId, fields, kv.Value.IncludeExtensions, serverDeps);
            });

        var grids = model.Grids.ToDictionary(
            kv => kv.Key,
            kv => new ManifestGrid(
                kv.Value.ViewId, kv.Value.Columns, kv.Value.RowActions,
                kv.Value.ToolbarActions, kv.Value.IncludeExtensions));

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
            extensions, model.Permissions, revision);
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
        f.IsChangeSet);

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
