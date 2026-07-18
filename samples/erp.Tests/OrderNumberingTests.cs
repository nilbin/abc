using Erp;
using Erp.Features;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Durable order numbering (Sol review, Finding 5). The old COUNT(*)+base handed two concurrent
/// creates the same number (caught only by the unique index as a spurious failure) and recycled
/// numbers after a delete. The counter row makes numbers sequential, unique and monotonic — this
/// test pins those properties, including that the counter survives across independent operations
/// rather than being recomputed from a live row count.
/// </summary>
public sealed class OrderNumberingTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;
    private TestActor<ErpDbContext> clerk = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Testkund AB"), new("Testgatan 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            return Task.CompletedTask;
        });
        clerk = host.Actor("demo", "orders.create");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<string> CreateAsync() =>
        (await clerk.ExecuteAsync("orders.create", new
            { customerId, orderType = "service", workAddress = "L", description = "D" }))
        .ShouldSucceed().Output<CreateOrder.Output>().Number.Value;

    [Fact]
    public async Task Numbers_are_sequential_unique_and_survive_a_deleted_row()
    {
        // A tenant with no counter yet claims the historical base, then advances by one each time.
        var first = await CreateAsync();
        var second = await CreateAsync();
        var third = await CreateAsync();

        Assert.Equal("2026-01412", first);
        Assert.Equal("2026-01413", second);
        Assert.Equal("2026-01414", third);
        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());

        // Delete the middle order. COUNT(*)+base would now hand the NEXT create 2026-01414 again —
        // a duplicate. The durable counter never looks back, so the sequence keeps climbing.
        await host.SeedAsync("demo", db =>
        {
            var victim = db.Orders.Single(o => o.Number == new OrderNumber(second));
            db.Orders.Remove(victim);
            return Task.CompletedTask;
        });

        var fourth = await CreateAsync();
        Assert.Equal("2026-01415", fourth);
    }
}
