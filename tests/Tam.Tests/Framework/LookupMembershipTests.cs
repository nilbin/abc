using Tam.Testing;

namespace Tam.Tests.Framework;

/// <summary>
/// Lookup membership is authoritative FRAMEWORK behavior (docs/40): the create derivation binds BinId to
/// the bins.lookup View scoped to the picked Group. On submit the selected Bin must EXIST in that
/// candidate universe — checked by an Exists against the base filter, not by whatever the client last
/// rendered. A Bin from another Group is rejected even though it is a perfectly real, open Bin.
/// </summary>
public sealed class LookupMembershipTests : IAsyncLifetime
{
    private TamTestHost<WidgetDbContext> host = null!;
    private Guid groupA;
    private Guid binInGroupA, binInGroupB;
    private TestActor<WidgetDbContext> clerk = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<WidgetDbContext>.CreateSqliteAsync(WidgetModel.Build());
        groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        binInGroupA = Guid.NewGuid();
        binInGroupB = Guid.NewGuid();
        await host.SeedAsync("demo", db =>
        {
            db.Bins.Add(new Bin { Id = new BinId(binInGroupA), TenantId = "demo", GroupId = groupA, Name = "Bin A", Status = BinStatus.Open });
            db.Bins.Add(new Bin { Id = new BinId(binInGroupB), TenantId = "demo", GroupId = groupB, Name = "Bin B", Status = BinStatus.Open });
            return Task.CompletedTask;
        });
        // The clerk can read the lookup View (as the picker requires) — membership reuses that permission,
        // failing closed if the caller cannot see the candidate universe.
        clerk = host.Actor("demo", "widgets.create", "bins.read");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object SpecialWidget(Guid binId) => new
    {
        name = "W", category = "special", groupId = groupA, binId, description = "membership probe",
    };

    [Fact]
    public async Task A_bin_from_another_group_is_rejected()
    {
        // binInGroupB is real and open — but it belongs to Group B, so it is outside Group A's candidate
        // universe. Membership rejects it (never mind that a stale client might offer it).
        (await clerk.ExecuteAsync("widgets.create", SpecialWidget(binInGroupB)))
            .ShouldFailWith("widgets.bin-not-available", onField: "binId");
    }

    [Fact]
    public async Task A_bin_of_the_group_is_accepted()
    {
        (await clerk.ExecuteAsync("widgets.create", SpecialWidget(binInGroupA))).ShouldSucceed();
    }
}
