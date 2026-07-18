using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;
using Tam.Generated;

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
        // The generated facade (docs/31): compile-time names over the SAME declared read.
        var order = OrdersDetailRow.From(WireValues.Row(orders.Rows[0]));
        var number = order.Number ?? "";
        var amount = order.EstimatedTotal ?? 0m;

        var invoice = Invoice.Create(context.TenantId.Value, input.OrderId, number, amount);
        tam.Db.Add(invoice);

        // The order wears its invoice status (D-X2): the plugin's OWN declared field, set
        // through the structurally-scoped writer — column/filter on the host grid update live.
        await fields.SetAsync("order", input.OrderId, "invoiceStatus", "draft", ct);

        return new Result<Output> { Output = new Output(invoice.Id) }
            .Effect(new EventPublished(new InvoiceCreated(invoice.Id, input.OrderId)));
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
            .Effect(new EventPublished(new InvoiceFinalized(invoice.Id, invoice.OrderId)));
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
            .Effect(new EventPublished(new InvoicePaid(invoice.Id, invoice.OrderId)));
    }
}

/// <summary>The record surface behind the plugin's DECLARED page (docs/32 + review round 4):
/// one invoice, fields named like the list so labels are shared.</summary>
[View("invoicing.invoices.detail")]
[Authorize("invoicing.read")]
public static class InvoiceDetail
{
    public sealed record Query(Guid InvoiceId);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("invoicing.labels.order-number")]
        public string OrderNumber { get; init; } = "";
        [LabelKey("labels.status")]
        public string Status { get; init; } = "";
        [LabelKey("invoicing.labels.amount")]
        public Money Amount { get; init; }
        [LabelKey("invoicing.labels.created")]
        public string Created { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam) =>
        tam.Db.Set<Invoice>().Where(x => x.Id == query.InvoiceId)
            .Select(x => new Result
            {
                Id = x.Id, OrderNumber = x.OrderNumber, Status = x.Status,
                Amount = x.Amount, Created = x.CreatedAtIso,
            });
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
        public Money Amount { get; init; }
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

/// <summary>Blocks orders.complete while a draft invoice exists for the order — the gate
/// reads the plugin's OWN table off the wire input, never host types (docs/22 P2).</summary>
[Gate("orders.complete")]
internal sealed class DraftPendingGate(ITamDb tam) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        if (gate.Guid("orderId") is not { } orderId) return Result.Success();
        var pending = await tam.Db.Set<Invoice>()
            .AnyAsync(x => x.OrderId == orderId && x.Status == "draft", ct);
        return pending ? InvoicingFindings.DraftPending.Create() : Result.Success();
    }
}

/// <summary>
/// Step 17 beat 4: on order completion, draft the invoice. Idempotent (at-least-once
/// delivery); order number from the payload; the amount backfilled through a SERVICE-MODE
/// declared read (docs/31 D-X3) — no actor exists here, so the readable surface is exactly
/// the RequiresView list. The status lands on the order via the writer (D-X2).
/// </summary>
[OnEffect("order-completed")]
internal sealed class DraftOnCompletion(
    ITamDb tam, IHostViewReader host, IPackagedFieldWriter fields) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (OrderCompletedEvent.From(effect) is not { OrderId: { } orderId } payload) return;
        if (await tam.Db.Set<Invoice>().AnyAsync(x => x.OrderId == orderId, ct))
            return;   // redelivery or already invoiced by hand — idempotent

        var number = payload.Number ?? "";
        var detail = await host.RowsAsync("orders.detail",
            new Dictionary<string, string?> { ["orderId"] = orderId.ToString() }, ct);
        var amount = (detail.FirstRow() is { } row ? OrdersDetailRow.From(row) : null)?.EstimatedTotal ?? 0m;

        tam.Db.Add(Invoice.Create(effect.TenantId, orderId, number, amount));
        await tam.Db.SaveChangesAsync(ct);
        await fields.SetAsync("order", orderId, "invoiceStatus", "draft", ct);
    }
}

/// <summary>
/// docs/34 M4: on WORK ORDER completion, draft the invoice from what the work actually cost —
/// APPROVED time entries plus every material line, read through the same service-mode declared
/// reads as the order path (docs/31 D-X3), filtered by the number the payload carries. The
/// aggregates postdate this plugin's original design; only the CONTRACT grew.
/// </summary>
[OnEffect("work-order-completed")]
internal sealed class DraftOnWorkOrderCompletion(
    ITamDb tam, IHostViewReader host) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (WorkOrderCompletedEvent.From(effect) is not { WorkOrderId: { } workOrderId } payload) return;
        if (await tam.Db.Set<Invoice>().AnyAsync(x => x.WorkOrderId == workOrderId, ct))
            return;   // at-least-once delivery — idempotent

        var number = payload.Number ?? "";
        if (number.Length == 0) return;

        var amount =
            await SumAsync("time.list", new Dictionary<string, string?>
                { ["workOrderNumber"] = number, ["status"] = "approved", ["pageSize"] = "200" }, ct)
            + await SumAsync("materials.list", new Dictionary<string, string?>
                { ["workOrderNumber"] = number, ["pageSize"] = "200" }, ct);

        tam.Db.Add(Invoice.CreateForWorkOrder(effect.TenantId, workOrderId, number, amount));
        await tam.Db.SaveChangesAsync(ct);
    }

    private async Task<decimal> SumAsync(
        string viewId, IReadOnlyDictionary<string, string?> query, CancellationToken ct)
    {
        // Both declared reads share the amount column; either facade names it — TimeListRow
        // here, exercising the generated accessor over the wire row.
        var result = await host.RowsAsync(viewId, query, ct);
        return result.WireRows().Sum(row => TimeListRow.From(row).Amount ?? 0m);
    }
}
