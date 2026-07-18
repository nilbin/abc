using Tam;
using Tam.EntityFrameworkCore;

namespace Invoicing;

/// <summary>
/// The vendor's own aggregate (docs/31 D-X6, tutorial Step 17): stored in the host database via
/// the host's one-line opt-in, tenant-scoped by the ambient filter. The order reference is a
/// WIRE-KEY Guid — no navigation property, no FK, no host CLR type — and the order number is
/// DENORMALIZED at event/read time (docs/31 D-X3: no cross-boundary joins, ever).
/// </summary>
public sealed class Invoice : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = "";
    public string Status { get; set; } = "draft";      // draft | invoiced | paid
    public Money Amount { get; set; }
    public string CreatedAtIso { get; set; } = "";
    public string? FinalizedAtIso { get; set; }

    public static Invoice Create(string tenantId, Guid orderId, string orderNumber, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        OrderId = orderId,
        OrderNumber = orderNumber,
        Amount = amount,
        CreatedAtIso = IsoTime.Now(),
    };
}


// ---- The aggregate's published language (docs/31 "events are records"): the record IS the
// contract — fields and kinds derive from its members, discovery registers it, and the
// publish site is compile-checked (TAM009 refuses anonymous payloads). ----

[DomainEvent("invoicing.invoice-created")]
public sealed record InvoiceCreated(Guid InvoiceId, Guid OrderId);

[DomainEvent("invoicing.invoice-finalized")]
public sealed record InvoiceFinalized(Guid InvoiceId, Guid OrderId);

[DomainEvent("invoicing.invoice-paid")]
public sealed record InvoicePaid(Guid InvoiceId, Guid OrderId);
