using Erp;
using Tam.Testing;

namespace Erp.Tests;

public sealed class CapabilitySweepTests
{
    /// <summary>
    /// The whole declared surface, verified in one test: every view's default sort, every
    /// sortable field in both directions, every filterable field with a typed probe — executed
    /// through the real pipeline against a real database. A view that compiles but cannot
    /// translate fails HERE, named, instead of 500ing on its first sorted request in
    /// production. New aggregates are covered the moment they are declared — no test to write.
    /// </summary>
    [Fact]
    public async Task Every_declared_capability_executes()
    {
        await using var host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        var report = await CapabilitySweep.RunAsync(host, "demo");
        report.ThrowIfFailed();
        Assert.True(report.ViewsExecuted > 20, $"only {report.ViewsExecuted} views executed");
    }
}
