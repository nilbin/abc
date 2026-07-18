using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.EntityFrameworkCore;

namespace Inspect;

// ---------------------------------------------------------------------------------------
// Template administration (docs/34 M6): a tenant admin defines checklist templates keyed
// on the host's order type. Plain Tam modules under the plugin prefix (PLG001), ITamDb
// only — a plugin composes around the host, it does not reach into it. Operations load
// the aggregate root and call its intent; the invariants live on the root.
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
        if (string.IsNullOrWhiteSpace(input.Text))
            return TemplateFindings.ItemTextRequired.At(nameof(Input.Text));

        var template = await tam.Db.Set<ChecklistTemplate>().Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == input.TemplateId, ct);
        if (template is null) return PipelineFindings.NotFound.Create();

        var added = template.AddItem(input.Text);
        if (added.IsError) return added.As<Output>();
        // The root is already tracked, so the new line must be marked Added explicitly —
        // change-tracker discovery would read its client-set key as an existing row.
        tam.Db.Add(added.Output!);
        return new Output(added.Output!.Id, added.Output.Position);
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

        template.Retire();
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
            ItemCount = x.Items.Count,
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
