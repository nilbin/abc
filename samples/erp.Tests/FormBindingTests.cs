using Erp;
using Erp.Features;
using Tam.Testing;

namespace Erp.Tests;

/// <summary>
/// Forms are projections of an operation contract (docs/40): a form's RequiredWhen tightening
/// applies ONLY when the submission is made through that named form — never as a union scanned
/// across every form bound to the operation (Sol re-review, Finding 2). A direct operation call
/// (the door MCP and integrations use) is governed by the operation's own contract alone.
/// </summary>
public sealed class FormBindingTests : IAsyncLifetime
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

    private object ProjectOrderMissingProject() => new
    {
        customerId,
        orderType = "project",
        workAddress = "Verkstadsgatan 1",
        description = "project order, no project chosen",
    };

    [Fact]
    public async Task Direct_operation_call_is_bound_by_the_operation_contract_only()
    {
        // No form binding: the create form's RequiredWhen(OrderType == Project) does NOT run. The
        // operation's OWN domain rule catches the missing project, with its precise finding — not a
        // generic validation.required scanned off some form.
        (await clerk.ExecuteAsync("orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("orders.project-required");
    }

    [Fact]
    public async Task Submitting_through_the_form_applies_that_forms_tightening()
    {
        // Through web.orders.create, the form's RequiredWhen(OrderType == Project) fires first as
        // validation.required on projectId — the form-specific tightening, applied because THIS
        // form was named.
        (await clerk.ExecuteThroughFormAsync("web.orders.create", "orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("validation.required", onField: "projectId");
    }

    [Fact]
    public async Task Another_forms_tightening_is_not_applied()
    {
        // web.orders.schedule binds to a DIFFERENT operation, so naming it here is inert: the
        // orders.create submission is bound by orders.create's own contract, and only its domain
        // rule fires. No unrelated form's rules leak in.
        (await clerk.ExecuteThroughFormAsync("web.orders.schedule", "orders.create", ProjectOrderMissingProject()))
            .ShouldFailWith("orders.project-required");
    }
}
