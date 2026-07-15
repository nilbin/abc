using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore.SystemOps;

/// <summary>The audit READ side (D3). Capture stays in the pipeline transaction — core,
/// unconditionally; this package is only the history surface.</summary>
[TamPackage("tam.audit", "audit", "web.audit")]
public sealed class TamAuditPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.LocaleDefaults();
        plugin.Model
            .AddViewType(typeof(AuditLog))
            .Grid<AuditLog.Result>("web.audit.list", "audit.entries", grid =>
            {
                grid.Column(x => x.Timestamp);
                grid.Column(x => x.OperationId);
                grid.Column(x => x.ActorName);
                grid.Column(x => x.Entity);
                grid.Column(x => x.Field);
                grid.Column(x => x.OldValue);
                grid.Column(x => x.NewValue);
            });
    }
}

/// <summary>Entity history is an ordinary indexed query over the same-transaction audit tables.</summary>
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

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var changes = tam.Db.Set<AuditChange>().AsQueryable();
        if (query.Entity is { Length: > 0 }) changes = changes.Where(x => x.Entity == query.Entity);
        if (query.EntityId is { Length: > 0 }) changes = changes.Where(x => x.EntityId == query.EntityId);

        // Entries carry the tenant; joining through them scopes the change rows.
        return changes
            .Join(tam.Db.Set<AuditEntry>(),
                c => c.EntryId, e => e.Id, (c, e) => new Result
            {
                Id = c.Id,
                // ISO string column: sortable/formattable on every provider (SQLite incl.)
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
