using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests.Framework;

// A FRAMEWORK-owned pipeline fixture (the user's round-13 ask): a minimal extensible entity + operations,
// so the tenant extension channel — required-on-create, prefill create semantics, fail-closed targeting,
// boundary validation — is verified through the REAL pipeline WITHOUT leaning on the ERP sample's
// operation choices. Nothing here is discovered by the source generator; operations register imperatively
// through the same public TamModelBuilder.AddOperationType the generator would call.

/// <summary>The extensible aggregate under test. A plain tenant-scoped POCO — the framework needs only
/// its key, its <see cref="ITenantScoped"/> stamp, and its <see cref="IExtensible"/> channel.</summary>
public sealed class Widget : IExtensible, ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public ExtensionData Extensions { get; set; } = new();
}

public sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options, TenantScope tenantScope)
    : DbContext(options), ITenantScopeContext
{
    public string? CurrentTenantId => tenantScope.Current;
    public IReadOnlyList<string> TenantReadSet => tenantScope.ReadSet;
    public bool DerivationReadOnly => tenantScope.DerivationReadOnly;

    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.UseTam(Database.ProviderName);   // framework tables: extension registry, audit, outbox…
        modelBuilder.ApplyTenantFilter(this);
    }
}

/// <summary>The extensible create — the target is a single Added Widget.</summary>
[Operation("widgets.create")]
[Authorize("widgets.create")]
[AcceptsExtensions(typeof(Widget))]
public static class CreateWidget
{
    public sealed record Input(string Name);
    public sealed record Output(Guid WidgetId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = new Widget { Id = Guid.NewGuid(), TenantId = context.TenantId.Value, Name = input.Name };
        db.Widgets.Add(widget);
        return Task.FromResult<Result<Output>>(new Output(widget.Id));
    }
}

/// <summary>The extensible edit — the target is a single Modified Widget.</summary>
[Operation("widgets.edit")]
[Authorize("widgets.edit")]
[AcceptsExtensions(typeof(Widget))]
public static class EditWidget
{
    public sealed record Input(Guid WidgetId, Change<string>? Name = null);
    public sealed record Output(Guid WidgetId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(x => x.Id == input.WidgetId, ct);
        if (widget is null) return PipelineFindings.NotFound.Create();
        var merge = TamMerge.Apply(widget, input);
        if (merge.HasConflicts) return merge.ToConflictResult<Output>();
        return new Output(widget.Id);
    }
}

/// <summary>A NON-extensible create (no <c>AcceptsExtensions</c>) — the vehicle for "an extensions channel
/// on an operation that does not accept extensions is rejected". It still creates a Widget, so a test can
/// prove the handler never ran when the boundary rejects the channel.</summary>
[Operation("widgets.create-plain")]
[Authorize("widgets.create")]
public static class CreateWidgetPlain
{
    public sealed record Input(string Name);
    public sealed record Output(Guid WidgetId);

    public static Task<Result<Output>> Execute(
        Input input, OperationContext context, WidgetDbContext db, CancellationToken ct)
    {
        var widget = new Widget { Id = Guid.NewGuid(), TenantId = context.TenantId.Value, Name = input.Name };
        db.Widgets.Add(widget);
        return Task.FromResult<Result<Output>>(new Output(widget.Id));
    }
}

public static class WidgetModel
{
    /// <summary>The composed framework model: the always-active system packages (which own
    /// <c>extensions.define-field</c> and the extension registry, and self-supply their locales from
    /// embedded resources) plus the three Widget operations registered imperatively.</summary>
    public static TamModel Build() => new TamModelBuilder()
        .DefaultCulture("en")
        .AddTamSystem()
        .AddOperationType(typeof(CreateWidget))
        .AddOperationType(typeof(EditWidget))
        .AddOperationType(typeof(CreateWidgetPlain))
        // The runtime L10N001 gate requires a key for every field label + operation title; the framework
        // packages self-supply theirs, so only the Widget operations' keys are added here.
        .LocaleDefaults("en", new Dictionary<string, string>
        {
            ["labels.widget-id"] = "Widget",
            ["operations.widgets.create.title"] = "Create widget",
            ["operations.widgets.create-plain.title"] = "Create widget (plain)",
            ["operations.widgets.edit.title"] = "Edit widget",
        })
        .Build();
}
