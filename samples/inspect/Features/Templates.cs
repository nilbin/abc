using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Inspect;

public static class TemplateFindings
{
    public static readonly FindingFactory TemplateRetired =
        Finding.Error("inspect.template-retired");
    public static readonly FindingFactory ItemTextRequired =
        Finding.Error("inspect.item-text-required");
    public static readonly FindingFactory NameRequired =
        Finding.Error("inspect.name-required");
    public static readonly FindingFactory OrderTypeRequired =
        Finding.Error("inspect.order-type-required");
}

// ---------------------------------------------------------------------------------------
// Template administration (docs/34 M6): a tenant admin defines checklist templates keyed
// on the host's order type. Plain Tam modules under the plugin prefix (PLG001), ITamDb
// only — a plugin composes around the host, it does not reach into it.
// ---------------------------------------------------------------------------------------

[Operation("inspect.templates.define")]
[Authorize("inspect.templates.manage")]
public static class DefineTemplate
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.template-name")] string Name,
        [property: LabelKey("inspect.labels.order-type")] string OrderType,
        [property: LabelKey("inspect.labels.mandatory")] bool Mandatory = false);

    public sealed record Output(Guid TemplateId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            return Task.FromResult<Result<Output>>(
                TemplateFindings.NameRequired.At(nameof(Input.Name)));
        if (string.IsNullOrWhiteSpace(input.OrderType))
            return Task.FromResult<Result<Output>>(
                TemplateFindings.OrderTypeRequired.At(nameof(Input.OrderType)));

        var template = ChecklistTemplate.Create(
            context.TenantId.Value, input.Name.Trim(), input.OrderType, input.Mandatory);
        tam.Db.Add(template);
        return Task.FromResult<Result<Output>>(new Output(template.Id));
    }
}

[Operation("inspect.templates.add-item")]
[Authorize("inspect.templates.manage")]
public static class AddTemplateItem
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.template")] Guid TemplateId,
        [property: LabelKey("inspect.labels.item-text")] string Text);

    public sealed record Output(Guid ItemId, int Position);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var template = await tam.Db.Set<ChecklistTemplate>()
            .SingleOrDefaultAsync(x => x.Id == input.TemplateId, ct);
        if (template is null) return PipelineFindings.NotFound.Create();
        if (template.Retired)
            return TemplateFindings.TemplateRetired.At(nameof(Input.TemplateId));
        if (string.IsNullOrWhiteSpace(input.Text))
            return TemplateFindings.ItemTextRequired.At(nameof(Input.Text));

        var position = await tam.Db.Set<ChecklistTemplateItem>()
            .CountAsync(x => x.TemplateId == template.Id, ct) + 1;
        var item = ChecklistTemplateItem.Create(
            context.TenantId.Value, template.Id, position, input.Text.Trim());
        tam.Db.Add(item);
        return new Output(item.Id, item.Position);
    }
}

/// <summary>Retire, never delete (the framework-wide convention): a retired template stops
/// instantiating; checklists it already produced live on untouched.</summary>
[Operation("inspect.templates.retire")]
[Authorize("inspect.templates.manage")]
public static class RetireTemplate
{
    public sealed record Input(
        [property: LabelKey("inspect.labels.template")] Guid TemplateId);

    public sealed record Output(Guid TemplateId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var template = await tam.Db.Set<ChecklistTemplate>()
            .SingleOrDefaultAsync(x => x.Id == input.TemplateId, ct);
        if (template is null) return PipelineFindings.NotFound.Create();

        template.Retired = true;
        return new Output(template.Id);
    }
}

[View("inspect.templates.list")]
[Authorize("inspect.templates.read")]
public static class TemplateList
{
    public sealed record Query(string? Search = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("inspect.labels.template-name")]
        public string Name { get; init; } = "";
        [LabelKey("inspect.labels.order-type")]
        public string OrderType { get; init; } = "";
        [LabelKey("inspect.labels.mandatory")]
        public bool Mandatory { get; init; }
        [LabelKey("inspect.labels.item-count")]
        public int ItemCount { get; init; }
        [LabelKey("inspect.labels.retired")]
        public bool Retired { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var templates = tam.Db.Set<ChecklistTemplate>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Search))
            templates = templates.Where(x => x.Name.Contains(query.Search!));
        return templates.Select(x => new Result
        {
            Id = x.Id,
            Name = x.Name,
            OrderType = x.OrderType,
            Mandatory = x.Mandatory,
            ItemCount = tam.Db.Set<ChecklistTemplateItem>().Count(i => i.TemplateId == x.Id),
            Retired = x.Retired,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Name), nameof(Result.OrderType))
        .Filterable(nameof(Result.OrderType), nameof(Result.Mandatory), nameof(Result.Retired))
        .DefaultSort(nameof(Result.Name));
}

/// <summary>The template lines, admin-side: what a template will stamp onto new orders.</summary>
[View("inspect.templates.items")]
[Authorize("inspect.templates.read")]
public static class TemplateItemList
{
    public sealed record Query(Guid? TemplateId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("inspect.labels.template-name")]
        public string TemplateName { get; init; } = "";
        [LabelKey("inspect.labels.position")]
        public int Position { get; init; }
        [LabelKey("inspect.labels.item-text")]
        public string Text { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var items = tam.Db.Set<ChecklistTemplateItem>().AsQueryable();
        if (query.TemplateId is { } templateId)
            items = items.Where(x => x.TemplateId == templateId);
        return items.Join(tam.Db.Set<ChecklistTemplate>(),
            i => i.TemplateId, t => t.Id, (i, t) => new Result
        {
            Id = i.Id,
            TemplateName = t.Name,
            Position = i.Position,
            Text = i.Text,
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.TemplateName), nameof(Result.Position))
        .Filterable(nameof(Result.TemplateName))
        .DefaultSort(nameof(Result.Position));
}
