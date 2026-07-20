using Erp;
using Erp.Features;
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
    }

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
