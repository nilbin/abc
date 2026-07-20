using Erp;
using Erp.Features;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// A required tenant extension field must be satisfied when a row is CREATED — even when the client
/// sends no effective extension patch (Sol re-review round 11, F2). Under the complete-state contract an
/// untouched extension field arrives as Original == Value and reduces to an empty effective patch, and a
/// caller can also omit the extensions channel entirely; neither may bypass the required check. These
/// tests drive the whole pipeline (the extension channel included), not ExtensionApplier in isolation.
/// </summary>
public sealed class RequiredExtensionTests : IAsyncLifetime
{
    private TamTestHost<ErpDbContext> host = null!;
    private Guid customerId;

    public async Task InitializeAsync()
    {
        host = await TamTestHost<ErpDbContext>.CreateSqliteAsync(ErpModel.Build());
        var admin = host.Actor("demo", "extensions.manage");
        await host.SeedAsync("demo", db =>
        {
            var customer = Customer.Create("demo", new("Acme"), new("Road 1"), null, null);
            db.Customers.Add(customer);
            customerId = customer.Id.Value;
            return Task.CompletedTask;
        });
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "order",
            key = "warrantyRef",
            type = "text",
            labels = new Dictionary<string, string> { ["sv"] = "Garantiref", ["en"] = "Warranty ref" },
            required = true,
        })).ShouldSucceed();
        (await admin.ExecuteAsync("extensions.define-field", new
        {
            entity = "order",
            key = "note",
            type = "text",
            labels = new Dictionary<string, string> { ["sv"] = "Notering", ["en"] = "Note" },
        })).ShouldSucceed();
    }

    private Task<ExtensionData> ExtensionsOf(Guid id) =>
        host.QueryDbAsync("demo", db => db.Orders
            .Where(o => o.Id == new OrderId(id)).Select(o => o.Extensions).SingleAsync());

    public async Task DisposeAsync() => await host.DisposeAsync();

    private object CreateBody(object? extensions = null) => extensions is null
        ? new { customerId, orderType = "service", workAddress = "Verkstadsgatan 1", description = "D" }
        : new { customerId, orderType = "service", workAddress = "Verkstadsgatan 1", description = "D", extensions };

    [Fact]
    public async Task Create_omitting_the_extensions_channel_still_enforces_a_required_field()
    {
        // The client sent no extensions object at all — before the fix the whole channel was skipped and
        // the required field went unchecked, letting an invalid row commit.
        var actor = host.Actor("demo", "orders.create");
        (await actor.ExecuteAsync("orders.create", CreateBody()))
            .ShouldFailWith("validation.required", "extensions.warrantyRef");
    }

    [Fact]
    public async Task Create_with_an_empty_change_for_the_required_field_is_rejected()
    {
        // {original:null, value:null} is an initialized-but-untouched change: it reduces to an empty
        // effective patch, yet a create must still be told the required field is missing.
        var actor = host.Actor("demo", "orders.create");
        var body = CreateBody(new { warrantyRef = new { original = (string?)null, value = (string?)null } });
        (await actor.ExecuteAsync("orders.create", body))
            .ShouldFailWith("validation.required", "extensions.warrantyRef");
    }

    [Fact]
    public async Task Create_supplying_the_required_field_succeeds()
    {
        var actor = host.Actor("demo", "orders.create");
        var body = CreateBody(new { warrantyRef = new { original = (string?)null, value = "W-123" } });
        (await actor.ExecuteAsync("orders.create", body)).ShouldSucceed();
    }

    [Fact]
    public async Task Create_persists_a_prefilled_required_field_sent_as_original_equals_value()
    {
        // A prefilled create form freezes the same value as baseline AND current, so a required field can
        // arrive as {original: W-9, value: W-9} (Sol re-review round 12, F2). The edit-style effective
        // patch would drop it as a no-op, failing required or losing the value; a CREATE must apply it.
        var actor = host.Actor("demo", "orders.create");
        var created = await actor.ExecuteAsync("orders.create",
            CreateBody(new { warrantyRef = new { original = "W-9", value = "W-9" } }));
        created.ShouldSucceed();
        Assert.Equal("W-9", (await ExtensionsOf(created.Output<CreateOrder.Output>().OrderId.Value)).Get<string>("warrantyRef"));
    }

    [Fact]
    public async Task Create_persists_a_prefilled_optional_field_sent_as_original_equals_value()
    {
        // The optional twin: {original: N-1, value: N-1} on an OPTIONAL field has no required-active spec
        // to force processing, so before the fix the prefilled value was silently discarded.
        var actor = host.Actor("demo", "orders.create");
        var created = await actor.ExecuteAsync("orders.create", CreateBody(new
        {
            warrantyRef = new { original = (string?)null, value = "W-1" },
            note = new { original = "N-1", value = "N-1" },
        }));
        created.ShouldSucceed();
        Assert.Equal("N-1", (await ExtensionsOf(created.Output<CreateOrder.Output>().OrderId.Value)).Get<string>("note"));
    }

    [Fact]
    public async Task Editing_an_existing_row_with_the_field_unchanged_neither_re_requires_nor_conflicts()
    {
        // Required enforcement is a CREATE concern (round 11, F2): an edit that carries the extension
        // unchanged (Original == Value) reduces to an empty effective patch, so the pipeline does no
        // target selection and no required re-check — the edit of the main field just succeeds.
        var actor = host.Actor("demo", "orders.create", "orders.edit", "orders.edit-all");
        var created = await actor.ExecuteAsync("orders.create",
            CreateBody(new { warrantyRef = new { original = (string?)null, value = "W-1" } }));
        created.ShouldSucceed();
        var orderId = created.Output<CreateOrder.Output>().OrderId.Value;

        var edit = await actor.ExecuteAsync("orders.edit-details", new
        {
            orderId,
            description = new { original = "D", value = "D2" },
            extensions = new { warrantyRef = new { original = "W-1", value = "W-1" } },
        });
        edit.ShouldSucceed();
    }
}
