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
    public decimal Amount { get; set; }
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
