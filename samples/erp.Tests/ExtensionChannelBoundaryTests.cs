using System.Text.Json;
using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// The tenant extension channel is parsed + validated at the REQUEST BOUNDARY (Sol re-review round 13,
/// F3), the same place compiled input is — not read raw after the handler has run inside the transaction.
/// One coherent contract: extensions on a non-extensible operation, a non-object channel, an entry that
/// is not a Change object, or a null entry all answer a structured pipeline.invalid-input, and the
/// handler never runs.
/// </summary>
public sealed class ExtensionChannelBoundaryTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Acme"), new("Road 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            return Task.CompletedTask;
        });
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object OrderBody(object extensions) => new
    {
        customerId, orderType = "service", workAddress = "L", description = "D", extensions,
    };

    private Task<int> OrderCount() => host.QueryDbAsync("demo", db => db.Orders.CountAsync());

    // A raw-JSON body: the typed test serializer drops CLR nulls, but a real wire client sends an
    // explicit JSON null. Parse to a JsonElement so `extensions: null` / `{ "note": null }` survive.
    private JsonElement RawOrderBody(string extensionsJson) => JsonSerializer.Deserialize<JsonElement>(
        $$"""{"customerId":"{{customerId}}","orderType":"service","workAddress":"L","description":"D","extensions":{{extensionsJson}}}""");

    [Fact]
    public async Task Extensions_on_a_non_extensible_operation_are_rejected()
    {
        // projects.create declares no extensible entity — a caller believing an extension was accepted
        // must be told otherwise, not silently succeed.
        var actor = host.Actor("demo", "projects.create");
        (await actor.ExecuteAsync("projects.create", new
        {
            customerId, number = "P-1", name = "N",
            extensions = new { note = new { original = (string?)null, value = "x" } },
        })).ShouldFailWith("pipeline.invalid-input");
    }

    [Fact]
    public async Task A_null_extension_channel_is_invalid_input()
    {
        var actor = host.Actor("demo", "orders.create");
        (await actor.ExecuteAsync("orders.create", RawOrderBody("null")))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await OrderCount());   // the handler never ran
    }

    [Fact]
    public async Task A_non_object_extension_channel_is_invalid_input()
    {
        var actor = host.Actor("demo", "orders.create");
        (await actor.ExecuteAsync("orders.create", OrderBody(extensions: new object[0])))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await OrderCount());
    }

    [Fact]
    public async Task A_scalar_extension_entry_is_invalid_input()
    {
        var actor = host.Actor("demo", "orders.create");
        (await actor.ExecuteAsync("orders.create", OrderBody(extensions: new { note = "not-a-change-object" })))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await OrderCount());
    }

    [Fact]
    public async Task A_null_extension_entry_is_invalid_input()
    {
        var actor = host.Actor("demo", "orders.create");
        (await actor.ExecuteAsync("orders.create", RawOrderBody("""{"note":null}""")))
            .ShouldFailWith("pipeline.invalid-input");
        Assert.Equal(0, await OrderCount());
    }
}
