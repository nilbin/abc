using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Approvals;


// Plain row by design (TAM008 opt-out): a rule is tenant CONFIG the wildcard gate reads —
// every field is an independent setting; there is no transition one field guards another
// against (Retired is the standard single-flag retire).
#pragma warning disable TAM008

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

#pragma warning restore TAM008
