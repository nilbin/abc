using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

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
        Input input, OperationContext context, ErpDbContext db, TamModel model, CancellationToken ct)
    {
        // Registry-time diagnostics: the runtime twin of the compiler's rule set.
        if (!SemanticTypes.ByKey.ContainsKey(input.Type))
            return ExtensionFindings.UnknownType.At(nameof(Input.Type));

        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Key, "^[a-z][a-zA-Z0-9]*$"))
            return ValidationFindings.InvalidValue.At(nameof(Input.Key));

        var collision = await db.Set<ExtensionFieldEntity>().AnyAsync(
            x => x.TenantId == context.TenantId.Value && x.Entity == input.Entity && x.Key == input.Key, ct);
        if (collision)
            return ExtensionFindings.KeyConflict.At(nameof(Input.Key));       // EXT005

        if (!input.Labels.ContainsKey(model.DefaultCulture))
            return ExtensionFindings.MissingLabel.At(nameof(Input.Labels));   // EXT006

        var entity = new ExtensionFieldEntity
        {
            Id = Guid.NewGuid(),
            TenantId = context.TenantId.Value,
            Entity = input.Entity,
            Key = input.Key,
            Type = input.Type,
            Required = input.Required,
            MaxLength = input.MaxLength,
            LabelsJson = System.Text.Json.JsonSerializer.Serialize(input.Labels),
            DescriptionsJson = input.Descriptions is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(input.Descriptions),
            OptionsJson = input.Options is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(input.Options),
            State = ExtensionFieldState.Active,
        };
        db.Add(entity);
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
        Input input, OperationContext context, ErpDbContext db, CancellationToken ct)
    {
        var field = await db.Set<ExtensionFieldEntity>().SingleOrDefaultAsync(
            x => x.Id == input.FieldId && x.TenantId == context.TenantId.Value, ct);
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

    public static IQueryable<Result> Execute(Query query, ErpDbContext db)
    {
        var fields = db.Set<ExtensionFieldEntity>().AsQueryable();
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
