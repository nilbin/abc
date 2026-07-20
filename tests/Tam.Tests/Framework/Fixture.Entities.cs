using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests.Framework;

// The framework-owned pipeline fixture (the user's directive: framework behavior must not be asserted in
// the sample-app test suite). A minimal but complete model — two extensible entities, a lookup view,
// derivations, forms, an event, rule-target fields — stood up through the REAL pipeline via TamTestHost,
// so derivation/merge/lookup/form/rules/extension behavior is verified independently of the ERP sample.
// Nothing here is discovered by the source generator; everything registers through the public
// TamModelBuilder API the generator would otherwise call.

// ---- Value objects (semantic wrappers) + enums ----

public readonly record struct WidgetId(Guid Value);

public readonly record struct BinId(Guid Value);

/// <summary>A semantic wrapper used ONLY as an INPUT field for a CLOSED-OPTION target — its Option values
/// are wrappers the membership check must unwrap to their scalar to compare against the submitted string
/// (round-10 F3). The entity stores the plain scalar.</summary>
public readonly record struct WidgetLocation(string Value);

public enum WidgetCategory { Standard, Special }

public enum WidgetPriority { Normal, Urgent }

public enum BinStatus { Open, Closed }

// ---- Entities ----

/// <summary>The primary extensible aggregate (the "order" analog). A plain tenant-scoped POCO: the
/// framework needs only its key, its tenant stamp, and its extension channel.</summary>
public sealed class Widget : IExtensible, ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Location { get; set; } = "";
    public WidgetCategory Category { get; set; }
    public WidgetPriority Priority { get; set; }
    public BinId? BinId { get; set; }
    public ExtensionData Extensions { get; set; } = new();
}

/// <summary>The membership-universe + rule-target aggregate (the "project" analog): scoped to a Group,
/// carries a Status enum + Budget money (the compiled fields rules.schema offers) and an extension bag
/// (which rules.schema must NOT offer).</summary>
public sealed class Bin : IExtensible, ITenantScoped
{
    public BinId Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid GroupId { get; set; }
    public string Name { get; set; } = "";
    public WidgetCategory Category { get; set; }
    public BinStatus Status { get; set; }
    public decimal? Budget { get; set; }
    public ExtensionData Extensions { get; set; } = new();
}

// ---- Domain events (the "order-created" analog). BinCreated references a value-object-keyed row, so a
// rule's set-field action can resolve and write the payload's Bin. ----

[DomainEvent("widget-created")]
public sealed record WidgetCreated(Guid WidgetId, string Name, WidgetCategory Category);

[DomainEvent("bin-created")]
public sealed record BinCreated(BinId BinId, string Name, WidgetCategory Category);

// ---- DbContext ----

public sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options, TenantScope tenantScope)
    : DbContext(options), ITenantScopeContext
{
    public string? CurrentTenantId => tenantScope.Current;
    public IReadOnlyList<string> TenantReadSet => tenantScope.ReadSet;
    public bool DerivationReadOnly => tenantScope.DerivationReadOnly;

    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Bin> Bins => Set<Bin>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<Bin>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
        });
        modelBuilder.UseTam(Database.ProviderName);   // framework tables: extension registry, audit, outbox…
        modelBuilder.ApplyTenantFilter(this);
    }
}
