using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;

namespace Erp.Features;

/// <summary>
/// Audit as a read model (decision D3): entity history is an ordinary indexed query over the
/// same-transaction audit tables — no separate store, no special API.
/// </summary>
[View("audit.entries")]
[Authorize("audit.read")]
public static class AuditLog
{
    public sealed record Query(string? Entity = null, string? EntityId = null);

    public sealed record Result
    {
        public Guid Id { get; init; }
        [LabelKey("labels.timestamp")]
        public string Timestamp { get; init; } = "";
        [LabelKey("labels.operation")]
        public string OperationId { get; init; } = "";
        [LabelKey("labels.actor")]
        public string ActorName { get; init; } = "";
        public string Entity { get; init; } = "";
        [LabelKey("labels.entity-id")]
        public string EntityId { get; init; } = "";
        public string Field { get; init; } = "";
        [LabelKey("labels.old-value")]
        public string? OldValue { get; init; }
        [LabelKey("labels.new-value")]
        public string? NewValue { get; init; }
    }

    public static IQueryable<Result> Execute(Query query, ErpDbContext db)
    {
        var changes = db.Set<AuditChange>().AsQueryable();
        if (query.Entity is { Length: > 0 }) changes = changes.Where(x => x.Entity == query.Entity);
        if (query.EntityId is { Length: > 0 }) changes = changes.Where(x => x.EntityId == query.EntityId);

        return changes
            .Join(db.Set<AuditEntry>(), c => c.EntryId, e => e.Id, (c, e) => new Result
            {
                Id = c.Id,
                // SQLite cannot order/format DateTimeOffset server-side; sortable ISO string instead.
                Timestamp = e.TimestampIso.Substring(0, 16).Replace("T", " "),
                OperationId = e.OperationId,
                ActorName = e.ActorName,
                Entity = c.Entity,
                EntityId = c.EntityId,
                Field = c.Field.Length > 0 ? c.Field : c.Kind,
                OldValue = c.OldValue,
                NewValue = c.NewValue,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) => caps
        .Sortable(nameof(Result.Timestamp), nameof(Result.OperationId))
        .DefaultSort(nameof(Result.Timestamp), descending: true);
}
