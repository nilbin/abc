using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// Rollback isolation (Sol re-review, Finding 1) is FRAMEWORK behavior: a blocked operation must leave NO
/// tracked residue on its DbContext, so a later save on a shared scope can never flush its discarded
/// writes. widgets.create writes the Widget in its handler FIRST, then the extension channel rejects an
/// unknown custom-field key — a write-then-block that exercises the rollback + change-tracker clear.
/// Without the clear, the Added Widget would linger and a stray SaveChanges would commit it.
/// </summary>
public sealed class RollbackIsolationTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;

    public async Task InitializeAsync() =>
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Blocked_operation_leaves_no_tracked_residue()
    {
        var clerk = host.Actor("demo", "widgets.create");
        await host.ExecuteThenInspectAsync(clerk, "widgets.create", new
        {
            name = "W",
            // The handler creates the Widget first; THEN this unknown custom field is rejected, so the
            // attempt rolls back after a tracked write — the exact shape of the old leak.
            extensions = new { noSuchField = new { original = (string?)null, value = "x" } },
        }, async (response, db) =>
        {
            Assert.Contains(response.Findings, f => f.Severity == FindingSeverity.Error);
            // The rolled-back Widget is gone from the shared change tracker (Finding 1): no Added or
            // Modified entry lingers to be flushed by a later SaveChanges on this same context.
            Assert.DoesNotContain(db.ChangeTracker.Entries(),
                e => e.State is EntityState.Added or EntityState.Modified);
            await db.SaveChangesAsync();
            Assert.Equal(0, await db.Widgets.CountAsync());
            return true;
        });
    }
}
