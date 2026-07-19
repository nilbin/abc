using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Rollback isolation (Sol re-review, Finding 1): a blocked operation must leave NO tracked residue
/// on its DbContext, so a later save on a shared scope can never flush its discarded writes.
/// orders.create writes the Order in its handler FIRST, then the extension channel rejects an
/// unknown custom-field key — a write-then-block that exercises the rollback + change-tracker clear.
/// Without the clear, the Added Order would linger and a stray SaveChanges would commit it.
/// </summary>
public sealed class RollbackIsolationTests : IAsyncLifetime
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

    [Fact]
    public async Task Blocked_operation_leaves_no_tracked_residue()
    {
        await host.ExecuteThenInspectAsync(clerk, "orders.create", new
        {
            customerId,
            orderType = "service",
            workAddress = "L",
            description = "D",
            // The handler creates the Order first; THEN this unknown custom field is rejected,
            // so the attempt rolls back after a tracked write — the exact shape of the old leak.
            extensions = new { noSuchField = new { original = (string?)null, value = "x" } },
        }, async (response, db) =>
        {
            Assert.Contains(response.Findings, f => f.Severity == FindingSeverity.Error);
            // The rolled-back Order is gone from the shared change tracker (Finding 1): no Added or
            // Modified entry lingers to be flushed by a later SaveChanges on this same context.
            Assert.DoesNotContain(db.ChangeTracker.Entries(),
                e => e.State is EntityState.Added or EntityState.Modified);
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.Orders.CountAsync());
            return true;
        });
    }
}
