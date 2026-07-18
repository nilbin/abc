using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Approvals;


/// <summary>
/// Which HOST operations need sign-off — tenant data over host WIRE ids, exactly the config the
/// wildcard gate reads. An optional numeric threshold restricts the rule to inputs whose named
/// wire field reaches the limit ("orders.create above 100 000 kr").
/// </summary>
public sealed class ApprovalRule : ITenantScoped
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public Guid GroupId { get; set; }
    public string? ThresholdField { get; set; }     // wire name, e.g. "estimatedTotal"
    public decimal? Threshold { get; set; }
    public bool Retired { get; set; }
}
