using Erp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Tam.Tests;

/// <summary>
/// Proves the tenant boundary is enforced by the EF global query filter (one place), not by a
/// hand-written Where at each call site. The decisive case is TWO context instances with DIFFERENT
/// ambient tenants sharing one cached model: if EF captured the model-building instance instead of
/// re-reading the current one, the second context would see the first's tenant — so this test
/// fails loudly if the mechanism regresses.
/// </summary>
public class TenantIsolationTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<ErpDbContext> _options;

    public TenantIsolationTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new TenantStampInterceptor())
            .Options;

        // Seed rows for two tenants (Add is never filtered).
        var scope = new TenantScope();
        using var db = new ErpDbContext(_options, scope);
        db.Database.EnsureCreated();
        db.Customers.Add(Customer.Create("a", new("Acme A"), new("Road 1"), null, null));
        db.Customers.Add(Customer.Create("a", new("Acme A2"), new("Road 2"), null, null));
        db.Customers.Add(Customer.Create("b", new("Beta B"), new("Road 3"), null, null));
        db.SaveChanges();
    }

    private ErpDbContext For(string? tenant) => new(_options, new TenantScope { Current = tenant });

    [Fact]
    public void Each_tenant_sees_only_its_own_rows()
    {
        using var a = For("a");
        using var b = For("b");
        Assert.Equal(2, a.Customers.Count());
        Assert.Single(b.Customers.ToList());
        Assert.All(a.Customers.ToList(), c => Assert.Equal("a", c.TenantId));
        Assert.All(b.Customers.ToList(), c => Assert.Equal("b", c.TenantId));
    }

    [Fact]
    public void A_by_id_read_from_the_wrong_tenant_returns_nothing()
    {
        using var a = For("a");
        var oneOfA = a.Customers.First();
        using var b = For("b");
        // Same id, different tenant — the filter makes it invisible, no manual Where needed.
        Assert.Null(b.Customers.SingleOrDefault(c => c.Id == oneOfA.Id));
    }

    [Fact]
    public void An_unset_tenant_sees_nothing_unless_it_opts_out()
    {
        using var none = For(null);
        Assert.Empty(none.Customers.ToList());
        Assert.Equal(3, none.Customers.IgnoreQueryFilters().Count());   // background/janitor path
    }

    [Fact]
    public void TenantId_is_auto_stamped_on_insert_from_the_ambient_scope()
    {
        using (var a = For("a"))
        {
            a.Customers.Add(BlankTenant("Stamp Me AB"));   // no TenantId set on the new row
            a.SaveChanges();
        }
        using var read = For("a");
        var stamped = read.Customers.AsEnumerable().Single(c => c.Name.Value == "Stamp Me AB");
        Assert.Equal("a", stamped.TenantId);   // filled by the interceptor, not the caller
    }

    // A customer built with a blank tenant — the interceptor must fill it at save time.
    private static Customer BlankTenant(string name) =>
        Customer.Create("", new(name), new("Road"), null, null);

    public void Dispose() => _conn.Dispose();
}
