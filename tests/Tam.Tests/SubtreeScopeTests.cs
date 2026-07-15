using Erp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// Subtree grids (docs/26 D-H1 evolved): the ambient READ scope widens to a validated tenant
/// set for a SubtreeRead view; the write side (stamping) keeps using the single Current node.
/// </summary>
public class SubtreeScopeTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<ErpDbContext> _options;

    public SubtreeScopeTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new TenantStampInterceptor())
            .Options;

        var scope = new TenantScope();
        using var db = new ErpDbContext(_options, scope);
        db.Database.EnsureCreated();
        db.Customers.Add(Customer.Create("group", new("Group HQ"), new("Road 1"), null, null));
        db.Customers.Add(Customer.Create("group.north", new("North AB"), new("Road 2"), null, null));
        db.Customers.Add(Customer.Create("group.south", new("South AB"), new("Road 3"), null, null));
        db.Customers.Add(Customer.Create("other", new("Other Corp"), new("Road 4"), null, null));
        db.SaveChanges();
    }

    private ErpDbContext For(TenantScope scope) => new(_options, scope);

    [Fact]
    public void The_read_set_widens_reads_without_touching_the_current_node()
    {
        var scope = new TenantScope { Current = "group" };
        using var db = For(scope);
        Assert.Single(db.Customers.ToList());   // strict before widening

        scope.WidenRead(["group.north", "group.south"]);
        var widened = db.Customers.ToList();
        Assert.Equal(3, widened.Count);                            // self + subtree
        Assert.DoesNotContain(widened, c => c.TenantId == "other"); // never beyond the set
    }

    [Fact]
    public void An_empty_read_set_is_exactly_the_old_strict_behavior()
    {
        using var db = For(new TenantScope { Current = "group.north" });
        var rows = db.Customers.ToList();
        Assert.Single(rows);
        Assert.Equal("group.north", rows[0].TenantId);
    }

    [Fact]
    public void Writes_stamp_the_current_node_even_while_reads_are_widened()
    {
        var scope = new TenantScope { Current = "group" };
        scope.WidenRead(["group.north"]);
        using (var db = For(scope))
        {
            db.Customers.Add(Customer.Create("", new("New One"), new("Road 9"), null, null));
            db.SaveChanges();
        }
        using var check = For(new TenantScope { Current = "group" });
        Assert.Contains(check.Customers.ToList(), c => c.Name.Value == "New One");
        using var north = For(new TenantScope { Current = "group.north" });
        Assert.DoesNotContain(north.Customers.ToList(), c => c.Name.Value == "New One");
    }

    [Fact]
    public void InScope_composes_the_widened_set_explicitly()
    {
        // The explicit twin of the ambient filter, for queries that compose IgnoreQueryFilters
        // sources (the TAM005 composition rule): same answer under strict AND widened scope.
        var scope = new TenantScope { Current = "group" };
        using var db = For(scope);
        Assert.Single(db.Customers.InScope(db, new TenantId("group")).ToList());

        scope.WidenRead(["group.north", "group.south"]);
        Assert.Equal(3, db.Customers.InScope(db, new TenantId("group")).Count());
    }

    [Fact]
    public void SubtreeRead_capability_lands_in_the_manifest_and_SUB001_guards_the_field()
    {
        var model = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["labels.id"] = "Id", ["labels.name"] = "Name", ["labels.company"] = "Company",
            })
            .AddViewType(typeof(ThingsAcross))
            .Build();

        Assert.Equal("tenantId", model.Views["things.across"].Capabilities.SubtreeTenantField);
        Assert.Contains("tenantId", model.Views["things.across"].Capabilities.Filterable);

        var manifest = ManifestBuilder.Build(
            model, new Dictionary<string, IReadOnlyList<ExtensionFieldSpec>>(), 0, null);
        Assert.Equal("tenantId", manifest.Views["things.across"].Subtree);

        var bad = new TamModelBuilder()
            .LocaleDefaults("en", new Dictionary<string, string>
            {
                ["labels.id"] = "Id", ["labels.name"] = "Name",
            })
            .AddViewType(typeof(BadSubtree));
        var error = Assert.Throws<InvalidOperationException>(() => bad.Build());
        Assert.StartsWith("SUB001", error.Message);
    }

    [View("things.across")]
    [Authorize("things.read")]
    private static class ThingsAcross
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("labels.name")]
            public string Name { get; init; } = "";
            [LabelKey("labels.company")]
            public string TenantId { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();

        public static void Capabilities(ViewCapabilitiesBuilder caps) =>
            caps.SubtreeRead(nameof(Result.TenantId));
    }

    [View("things.broken")]
    [Authorize("things.read")]
    private static class BadSubtree
    {
        public sealed record Query;

        public sealed record Result
        {
            public Guid Id { get; init; }
            [LabelKey("labels.name")]
            public string Name { get; init; } = "";
        }

        public static IQueryable<Result> Execute(Query query) =>
            Array.Empty<Result>().AsQueryable();

        public static void Capabilities(ViewCapabilitiesBuilder caps) =>
            caps.SubtreeRead("TenantId");
    }

    public void Dispose() => _conn.Dispose();
}
