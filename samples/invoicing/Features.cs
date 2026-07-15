using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Invoicing;

public static class InvoicingFindings
{
    public static readonly FindingFactory OrderNotFound = Finding.Error("invoicing.order-not-found");
    public static readonly FindingFactory AlreadyInvoiced = Finding.Error("invoicing.already-invoiced");
    public static readonly FindingFactory NotDraft = Finding.Error("invoicing.not-draft");
    public static readonly FindingFactory NotInvoiced = Finding.Error("invoicing.not-invoiced");
    public static readonly FindingFactory DraftPending = Finding.Error("invoicing.draft-pending");
}

/// <summary>
/// Step 17 beat 3+7: the operation the GRID ACTION binds (docs/31 D-X1). The order id arrives
/// as wire input; the order itself is read through the ACTOR-mode host view reader (D-X3) —
/// permission-checked exactly like the wire, so a user who may not see the order cannot
/// invoice it. The order number and amount are denormalized from the declared read.
/// </summary>
[Operation("invoicing.create-from-order")]
[Authorize("invoicing.manage")]
public static class CreateFromOrder
{
    public sealed record Input(
        [property: LabelKey("invoicing.labels.order-id")] Guid OrderId);

    public sealed record Output(Guid InvoiceId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, IHostViewReader host,
        IPackagedFieldWriter fields, CancellationToken ct)
    {
        if (await tam.Db.Set<Invoice>().AnyAsync(x => x.OrderId == input.OrderId, ct))
            return InvoicingFindings.AlreadyInvoiced.At(nameof(Input.OrderId));

        var orders = await host.RowsAsync("orders.detail",
            new Dictionary<string, string?> { ["orderId"] = input.OrderId.ToString() }, context, ct);
        if (orders.Rows.Count == 0)
            return InvoicingFindings.OrderNotFound.At(nameof(Input.OrderId));
        var order = (IDictionary<string, object?>?)null;
        var row = System.Text.Json.JsonSerializer.SerializeToElement(orders.Rows[0], TamJson.Options);
        var number = row.TryGetProperty("number", out var n) ? n.GetString() ?? "" : "";
        var amount = row.TryGetProperty("estimatedTotal", out var a) && a.ValueKind
            is System.Text.Json.JsonValueKind.Number ? a.GetDecimal() : 0m;
        _ = order;

        var invoice = Invoice.Create(context.TenantId.Value, input.OrderId, number, amount);
        tam.Db.Add(invoice);

        // The order wears its invoice status (D-X2): the plugin's OWN declared field, set
        // through the structurally-scoped writer — column/filter on the host grid update live.
        await fields.SetAsync("order", input.OrderId, "invoiceStatus", "draft", ct);

        return new Result<Output> { Output = new Output(invoice.Id) }
            .Effect(new EventPublished("invoicing.invoice-created",
                new { invoiceId = invoice.Id, orderId = input.OrderId }));
    }
}

[Operation("invoicing.finalize")]
[Authorize("invoicing.manage")]
public static class FinalizeInvoice
{
    public sealed record Input(
        [property: LabelKey("invoicing.labels.invoice-id")] Guid InvoiceId);

    public sealed record Output(Guid InvoiceId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, IPackagedFieldWriter fields,
        CancellationToken ct)
    {
        var invoice = await tam.Db.Set<Invoice>()
            .SingleOrDefaultAsync(x => x.Id == input.InvoiceId, ct);
        if (invoice is null) return PipelineFindings.NotFound.Create();
        if (invoice.Status != "draft") return InvoicingFindings.NotDraft.Create();

        invoice.Status = "invoiced";
        invoice.FinalizedAtIso = IsoTime.Now();
        await fields.SetAsync("order", invoice.OrderId, "invoiceStatus", "invoiced", ct);

        return new Result<Output> { Output = new Output(invoice.Id) }
            .Effect(new EventPublished("invoicing.invoice-finalized",
                new { invoiceId = invoice.Id, orderId = invoice.OrderId }));
    }
}

[Operation("invoicing.mark-paid")]
[Authorize("invoicing.manage")]
public static class MarkPaid
{
    public sealed record Input(
        [property: LabelKey("invoicing.labels.invoice-id")] Guid InvoiceId);

    public sealed record Output(Guid InvoiceId);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, IPackagedFieldWriter fields,
        CancellationToken ct)
    {
        var invoice = await tam.Db.Set<Invoice>()
            .SingleOrDefaultAsync(x => x.Id == input.InvoiceId, ct);
        if (invoice is null) return PipelineFindings.NotFound.Create();
        if (invoice.Status != "invoiced") return InvoicingFindings.NotInvoiced.Create();

        invoice.Status = "paid";
        await fields.SetAsync("order", invoice.OrderId, "invoiceStatus", "paid", ct);

        return new Result<Output> { Output = new Output(invoice.Id) }
            .Effect(new EventPublished("invoicing.invoice-paid",
                new { invoiceId = invoice.Id, orderId = invoice.OrderId }));
    }
}

[View("invoicing.invoices.list")]
[Authorize("invoicing.read")]
public static class InvoiceList
{
    public sealed record Query(Guid? OrderId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("invoicing.labels.order-number")]
        public string OrderNumber { get; init; } = "";
        [LabelKey("labels.status")]
        public string Status { get; init; } = "";
        [LabelKey("invoicing.labels.amount")]
        public decimal Amount { get; init; }
        [LabelKey("invoicing.labels.created")]
        public string Created { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam)
    {
        var invoices = tam.Db.Set<Invoice>().AsQueryable();
        if (query.OrderId is { } orderId) invoices = invoices.Where(x => x.OrderId == orderId);
        return invoices.Select(x => new Result
        {
            Id = x.Id,
            OrderNumber = x.OrderNumber,
            Status = x.Status,
            Amount = x.Amount,
            Created = x.CreatedAtIso.Substring(0, 10),
        });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.OrderNumber), nameof(Result.Created))
        .Filterable(nameof(Result.Status))
        .DefaultSort(nameof(Result.Created), descending: true);
}
