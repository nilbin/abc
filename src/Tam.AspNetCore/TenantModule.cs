using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

public static class TenantFindings
{
    public static readonly FindingFactory InvalidId = Finding.Error("tenants.invalid-id");
    public static readonly FindingFactory DuplicateId = Finding.Error("tenants.duplicate-id");
    public static readonly FindingFactory UnknownNode = Finding.Error("tenants.unknown-node");
    public static readonly FindingFactory NotInSubtree = Finding.Error("tenants.not-in-subtree");
    public static readonly FindingFactory Cycle = Finding.Error("tenants.cycle");
}

/// <summary>
/// Tenant lifecycle (docs/26): a new node is always created as a CHILD OF THE ACTIVE node — writes
/// fan in to one node, so creating a grandchild means acting-as the child first (D-H4), exactly like
/// creating its data. The id becomes a path segment, so it must not contain the '.' separator; ids
/// are globally unique (they key every ITenantScoped row). A cascading membership above the new node
/// reaches it immediately — no membership row is written here (grants fan out, D-H5).
/// </summary>
[Operation("tenants.create")]
[Authorize("tenants.create")]
public static class CreateTenant
{
    public sealed record Input(
        string Id,
        [property: LabelKey("labels.display-name")] string DisplayName);

    public sealed record Output(string Id, string Path);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        // The id is a path segment: lowercase, no dots (the separator), same shape as role names.
        // The active id is checked explicitly: on a pre-hierarchy tenant (no registry row yet) the
        // registry probe would miss it and the self-heal below would add the same key twice.
        var activeId = context.TenantId.Value;
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.Id, "^[a-z][a-z0-9-]*$"))
            return TenantFindings.InvalidId.At(nameof(Input.Id));
        if (input.Id == activeId
            || await tam.Db.Set<TenantEntity>().AnyAsync(t => t.Id == input.Id, ct))
            return TenantFindings.DuplicateId.With(("id", input.Id)).At(nameof(Input.Id));

        // Self-healing root (docs/26): a pre-hierarchy tenant has no TenantEntity row; its first
        // child creation materializes the root so the path machinery has an anchor.
        var parent = await tam.Db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.Id == activeId, ct);
        if (parent is null)
        {
            parent = new TenantEntity { Id = activeId, ParentId = null, Path = activeId, DisplayName = activeId };
            tam.Db.Add(parent);
        }

        // Structural lease: attaching under a node bumps ITS version, so this create conflicts at
        // SaveChanges with any concurrent move/re-parent of the parent (whose stale path we would
        // otherwise bake into the child) instead of silently orphaning the new node.
        parent.Version++;

        tam.Db.Add(new TenantEntity
        {
            Id = input.Id,
            ParentId = parent.Id,
            Path = parent.Path + "." + input.Id,
            DisplayName = input.DisplayName,
        });
        return new Output(input.Id, parent.Path + "." + input.Id);
    }
}

/// <summary>
/// Re-parenting is nearly free by design (docs/27 implementation notes): rows carry only TenantId,
/// so moving a subtree rewrites the moved nodes' Path values in the tenants registry and NOTHING
/// else — no data row is touched. Authority is subtree-contained: standing at the active node you
/// may move a strict descendant under the active node or another of its descendants; you can never
/// move the node you stand on, lift a node out of your subtree, or create a cycle.
/// </summary>
[Operation("tenants.move")]
[Authorize("tenants.move")]
public static class MoveTenant
{
    public sealed record Input(
        [property: LabelKey("labels.tenant")] string TenantId,
        [property: LabelKey("labels.new-parent")] string NewParentId);

    public sealed record Output(string Id, string Path);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var activeId = context.TenantId.Value;
        var nodes = await tam.Db.Set<TenantEntity>().ToListAsync(ct);   // the registry is tiny
        var byId = nodes.ToDictionary(t => t.Id);
        if (!byId.TryGetValue(activeId, out var active))
            return TenantFindings.UnknownNode.With(("id", activeId)).At(nameof(Input.TenantId));
        if (!byId.TryGetValue(input.TenantId, out var moved))
            return TenantFindings.UnknownNode.With(("id", input.TenantId)).At(nameof(Input.TenantId));
        if (!byId.TryGetValue(input.NewParentId, out var newParent))
            return TenantFindings.UnknownNode.With(("id", input.NewParentId)).At(nameof(Input.NewParentId));

        // Both ends stay inside the active subtree; the moved node is a STRICT descendant.
        if (moved.Id == active.Id
            || !TenantEntity.IsSelfOrDescendant(active.Path, moved.Path))
            return TenantFindings.NotInSubtree.With(("id", moved.Id)).At(nameof(Input.TenantId));
        if (!TenantEntity.IsSelfOrDescendant(active.Path, newParent.Path))
            return TenantFindings.NotInSubtree.With(("id", newParent.Id)).At(nameof(Input.NewParentId));

        // A node cannot become its own descendant (this also rejects parent == moved).
        if (TenantEntity.IsSelfOrDescendant(moved.Path, newParent.Path))
            return TenantFindings.Cycle.With(("id", newParent.Id)).At(nameof(Input.NewParentId));

        // Structural lease (see CreateTenant): bump the attachment point so this move conflicts at
        // SaveChanges with any concurrent structural change to the new parent, instead of baking a
        // stale parent path into the moved subtree.
        newParent.Version++;

        var oldPath = moved.Path;
        var newPath = newParent.Path + "." + moved.Id;
        moved.ParentId = newParent.Id;
        foreach (var node in nodes)
        {
            if (node.Path == oldPath) node.Path = newPath;
            else if (node.Path.StartsWith(oldPath + ".", StringComparison.Ordinal))
                node.Path = newPath + node.Path[oldPath.Length..];
        }
        return new Output(moved.Id, newPath);
    }
}

/// <summary>Rename a node in the active subtree (including the active node itself — the only way to
/// give a self-healed root a real display name). Ids are immutable; only the label changes.</summary>
[Operation("tenants.rename")]
[Authorize("tenants.edit")]
public static class RenameTenant
{
    public sealed record Input(
        [property: LabelKey("labels.tenant")] string TenantId,
        [property: LabelKey("labels.display-name")] string DisplayName);

    public sealed record Output(string Id, string DisplayName);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        var activeId = context.TenantId.Value;
        var active = await tam.Db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.Id == activeId, ct);
        var node = input.TenantId == activeId
            ? active
            : await tam.Db.Set<TenantEntity>().FirstOrDefaultAsync(t => t.Id == input.TenantId, ct);
        if (node is null)
            return TenantFindings.UnknownNode.With(("id", input.TenantId)).At(nameof(Input.TenantId));
        if (active is null || !TenantEntity.IsSelfOrDescendant(active.Path, node.Path))
            return TenantFindings.NotInSubtree.With(("id", node.Id)).At(nameof(Input.TenantId));

        node.DisplayName = input.DisplayName;
        return new Output(node.Id, node.DisplayName);
    }
}

/// <summary>The active node's subtree — the nodes this administrator governs from where they stand.</summary>
[View("tenants.list")]
[Authorize("tenants.read")]
public static class TenantList
{
    public sealed record Query();

    public sealed record Result
    {
        public string Id { get; init; } = "";
        [LabelKey("labels.display-name")]
        public string DisplayName { get; init; } = "";
        [LabelKey("labels.parent")]
        public string? ParentId { get; init; }
        [LabelKey("labels.path")]
        public string Path { get; init; } = "";
    }

    public static IQueryable<Result> Execute(Query query, ITamDb tam, OperationContext context)
    {
        var activeId = context.TenantId.Value;
        var activePath = tam.Db.Set<TenantEntity>()
            .Where(t => t.Id == activeId).Select(t => t.Path).FirstOrDefault() ?? activeId;
        var prefix = activePath + ".";
        return tam.Db.Set<TenantEntity>()
            .Where(t => t.Path == activePath || t.Path.StartsWith(prefix))
            .Select(t => new Result
            {
                Id = t.Id, DisplayName = t.DisplayName, ParentId = t.ParentId, Path = t.Path,
            });
    }

    public static void Capabilities(ViewCapabilitiesBuilder caps) =>
        caps.Sortable(nameof(Result.Path)).DefaultSort(nameof(Result.Path));
}
