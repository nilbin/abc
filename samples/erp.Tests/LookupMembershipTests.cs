using Erp;
using Erp.Features;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Lookup membership is authoritative (docs/40, Sol plan §5–7): the create-order derivation binds
/// ProjectId to the projects.lookup View scoped to the picked customer. On submit, the selected
/// project must EXIST in that candidate universe — checked by an Exists against the base filter, not
/// by whatever the client last rendered. A project from another customer is rejected even though it
/// is a perfectly real, open project the caller could name.
/// </summary>
public sealed class LookupMembershipTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerA, projectA, projectB;
    private TestActor<ErpDbContext> clerk = null!;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        await host.SeedAsync("demo", db =>
        {
            var custA = Customer.Create("demo", new("Kund A"), new("A-gatan 1"), null, null);
            var custB = Customer.Create("demo", new("Kund B"), new("B-gatan 2"), null, null);
            db.Customers.AddRange(custA, custB);
            var projA = Project.Create("demo", new("P-A-1"), custA.Id, "Projekt A");
            var projB = Project.Create("demo", new("P-B-1"), custB.Id, "Projekt B");
            db.Projects.AddRange(projA, projB);
            customerA = custA.Id.Value;
            projectA = projA.Id.Value;
            projectB = projB.Id.Value;
            return Task.CompletedTask;
        });
        // The clerk can read the lookup View (as the picker requires) — membership reuses that
        // permission, failing closed if the caller cannot see the candidate universe.
        clerk = host.Actor("demo", "orders.create", "projects.read");
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object ProjectOrder(Guid projectId) => new
    {
        customerId = customerA,
        orderType = "project",
        projectId,
        workAddress = "Verkstadsgatan 1",
        description = "membership probe",
    };

    [Fact]
    public async Task A_project_from_another_customer_is_rejected()
    {
        // projectB is real and open — but it belongs to customer B, so it is outside customer A's
        // candidate universe. Membership rejects it (never mind that a stale client might offer it).
        (await clerk.ExecuteAsync("orders.create", ProjectOrder(projectB)))
            .ShouldFailWith("orders.project-not-available", onField: "projectId");
    }

    [Fact]
    public async Task A_project_of_the_customer_is_accepted()
    {
        (await clerk.ExecuteAsync("orders.create", ProjectOrder(projectA)))
            .ShouldSucceed();
    }
}
