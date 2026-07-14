namespace Inspect;

/// <summary>
/// The plugin's own aggregate: stored in the host's database (the host opts in via
/// <c>AddInspect()</c> on its ModelBuilder), audited and stamped by the same pipeline.
/// </summary>
public sealed class Checklist
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid? OrderId { get; set; }
    public string Title { get; set; } = "";
    public bool Passed { get; set; }

    public static Checklist Create(string tenantId, string title, Guid? orderId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Title = title,
        OrderId = orderId,
    };
}
