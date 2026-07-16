using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.SystemOps;

/// <summary>Tenant custom fields (docs/15): registry operations + the fields grid.</summary>
[TamPackage("tam.extensions", "extensions", "web.extensions")]
public sealed class TamExtensionsPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("extensions", grid: "web.extensions.fields", suggest: "administration", order: 30));
        plugin
            .AddOperationType(typeof(DefineExtensionField))
            .AddOperationType(typeof(RetireExtensionField))
            .AddViewType(typeof(ExtensionFieldList))
            .Form<DefineExtensionField.Input>("web.extensions.define", "extensions.define-field", form =>
            {
                form.Field(x => x.Entity);
                form.Field(x => x.Key);
                form.Field(x => x.Type);
                form.Field(x => x.Labels).Renderer("culture-text");
                form.Field(x => x.Required);
                form.Field(x => x.MaxLength);
            })
            .Grid<ExtensionFieldList.Result>("web.extensions.fields", "extensions.fields", grid =>
            {
                grid.Column(x => x.Entity);
                grid.Column(x => x.Key);
                grid.Column(x => x.Type);
                grid.Column(x => x.Required);
                grid.Column(x => x.State);
                grid.ToolbarAction("extensions.define-field");
            });
    }
}

/// <summary>
/// The tenant field registry is managed through ordinary operations — authorized, audited,
/// registry-checked at definition time (docs/15). No deploys involved.
/// </summary>
[Operation("extensions.define-field")]
[Authorize("extensions.manage")]
public static class DefineExtensionField
{
    public sealed record Input(
        string Entity,
        string Key,
        string Type,
        Dictionary<string, string> Labels,
        bool Required = false,
        int? MaxLength = null,
        Dictionary<string, string>? Descriptions = null,
        List<string>? Options = null);

    public sealed record Output(Guid FieldId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, TamModel model, CancellationToken ct)
    {
        // Registry-time diagnostics: the runtime twin of the compiler's rule set.
        if (!model.ExtensibleEntityKeys.Contains(input.Entity))
            return ExtensionFindings.UnknownEntity.With(("entity", input.Entity)).At(nameof(Input.Entity));   // EXT007
        if (!SemanticTypes.ByKey.ContainsKey(input.Type))
            return ExtensionFindings.UnknownType.At(nameof(Input.Type));

        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Key, "^[a-z][a-zA-Z0-9]*$"))
            return ValidationFindings.InvalidValue.At(nameof(Input.Key));

        var collision = await tam.Db.Set<ExtensionFieldEntity>().AnyAsync(
            x => x.Entity == input.Entity && x.Key == input.Key, ct);
        if (collision)
            return ExtensionFindings.KeyConflict.At(nameof(Input.Key));       // EXT005

        if (!input.Labels.ContainsKey(model.DefaultCulture))
            return ExtensionFindings.MissingLabel.At(nameof(Input.Labels));   // EXT006

        var entity = new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
            Entity = input.Entity,
            Key = input.Key,
            Type = input.Type,
            Required = input.Required,
            MaxLength = input.MaxLength,
            LabelsJson = JsonSerializer.Serialize(input.Labels),
            DescriptionsJson = input.Descriptions is null
                ? null
                : JsonSerializer.Serialize(input.Descriptions),
            OptionsJson = input.Options is null ? null : JsonSerializer.Serialize(input.Options),
            State = ExtensionFieldState.Active,
        };
        tam.Db.Add(entity);
        return new Output(entity.Id);
    }
}

[Operation("extensions.retire-field")]
[Authorize("extensions.manage")]
public static class RetireExtensionField
{
    public sealed record Input(Guid FieldId);

    public sealed record Output(Guid FieldId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var field = await tam.Db.Set<ExtensionFieldEntity>().SingleOrDefaultAsync(
            x => x.Id == input.FieldId, ct);
        if (field is null) return PipelineFindings.NotFound.Create();

        field.State = ExtensionFieldState.Retired;   // data preserved, key reserved forever
        return new Output(field.Id);
    }
}

[View("extensions.fields")]
[Authorize("extensions.manage")]
public static class ExtensionFieldList
{
    public sealed record Query(string? Entity = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        public string Entity { get; init; } = "";
        public string Key { get; init; } = "";
        public string Type { get; init; } = "";
        public bool Required { get; init; }
        public ExtensionFieldState State { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var fields = tam.Db.Set<ExtensionFieldEntity>().AsQueryable();
        if (query.Entity is { Length: > 0 }) fields = fields.Where(x => x.Entity == query.Entity);
        return fields.Select(x => new Result
        {
            Id = x.Id, Entity = x.Entity, Key = x.Key, Type = x.Type,
            Required = x.Required, State = x.State,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Key)).DefaultSort(nameof(Result.Key));
}

// ---------------------------------------------------------------- roles (decision D1)
