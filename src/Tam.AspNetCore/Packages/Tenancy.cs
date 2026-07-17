using Microsoft.EntityFrameworkCore;
using Tam.EntityFrameworkCore;

namespace Tam.AspNetCore;

/// <summary>Tenant hierarchy admin (docs/26). The tree itself (paths, cascade, act-as) is core;
/// these are the structural operations, which enforce subtree/cycle invariants — a
/// security-sensitive package by review policy.</summary>
[TamPackage("tam.tenancy", "tenants", "web.tenants")]
public sealed class TamTenancyPackage : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        // Nav CONTENT + suggestion (docs/30 D-N2) — the host owns placement.
        plugin.Nav(nav => nav.Page("tenants", grid: "web.tenants", suggest: "administration", order: 80));
        plugin
            .AddOperationType(typeof(CreateTenant))
            .AddOperationType(typeof(MoveTenant))
            .AddOperationType(typeof(RenameTenant))
            .AddViewType(typeof(TenantList))
            .Form<CreateTenant.Input>("web.tenants.create", "tenants.create", form =>
            {
                form.Field(x => x.Id);
                form.Field(x => x.DisplayName);
            })
            .Form<MoveTenant.Input>("web.tenants.move", "tenants.move", form =>
            {
                form.Field(x => x.TenantId);
                form.Field(x => x.NewParentId);
            })
            .Form<RenameTenant.Input>("web.tenants.rename", "tenants.rename", form =>
            {
                form.Field(x => x.TenantId);
                form.Field(x => x.DisplayName);
            })
            .Grid<TenantList.Result>("web.tenants", "tenants.list", grid =>
            {
                grid.Column(x => x.Id);
                grid.Column(x => x.DisplayName);
                grid.Column(x => x.Path);
                grid.ToolbarAction("tenants.create");
                grid.ToolbarAction("tenants.move");
                grid.ToolbarAction("tenants.rename");
            });
    }
}

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
        string DisplayName);

    public sealed record Output(string Id, string Path);

    public static async Task<Result<Output>> Execute(
        Input input, OperationContext context, ITamDb tam, CancellationToken ct)
    {
        // The id is a path segment: lowercase, no dots (the separator), same shape as role names.
        // The active id is checked explicitly: on a pre-hierarchy tenant (no registry row yet) the
        // registry probe would miss it and the self-heal below would add the same key twice.
        var activeId = context.TenantId.Value;
        if (!Naming.IsSlug(input.Id))
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

        // Crossing an anchor boundary (docs/24 hierarchy) changes effective entitlements and
        // seat pools. The move is structural and always succeeds — the mover typically CANNOT
        // fix billing anyway (subscriptions.manage is reserved) — but it WARNS about every
        // active plugin the new anchor doesn't entitle and about a seat pool now over its
        // ceiling. Enforcement follows the existing downgrade semantics: no new activations,
        // no new seats, reconciliation deactivates later. Undo the move and nothing happened.
        var findings = await AnchorCrossingWarningsAsync(tam, nodes, moved, ct);
        return new Result<Output> { Output = new Output(moved.Id, newPath), Findings = findings };
    }

    private static async Task<List<Finding>> AnchorCrossingWarningsAsync(
        ITamDb tam, List<TenantEntity> nodes, TenantEntity moved, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var anchors = await tam.Db.Set<SubscriptionEntity>().AcrossTenants().ToListAsync(ct);
        var byId = nodes.ToDictionary(t => t.Id);

        // Post-move covering anchors, computed over the ALREADY-REWRITTEN in-memory paths.
        CoveringSubscription CoveringOf(string id) =>
            Subscriptions.Pick(Subscriptions.Chain(byId.GetValueOrDefault(id), id), anchors);

        // Active plugins anywhere in the moved subtree that the NEW covering anchor no longer
        // entitles. Cross-tenant read by nature — the subtree spans nodes.
        var subtreeIds = nodes
            .Where(t => TenantEntity.IsSelfOrDescendant(moved.Path, t.Path))
            .Select(t => t.Id).ToList();
        var activations = await tam.Db.Set<PluginActivationEntity>().AcrossTenants()
            .Where(a => subtreeIds.Contains(a.TenantId)).ToListAsync(ct);
        foreach (var activation in activations)
        {
            if (!CoveringOf(activation.TenantId).Subscription.Entitles(activation.PluginId))
                findings.Add(SubscriptionFindings.EntitlementLost
                    .With(("plugin", activation.PluginId), ("tenant", activation.TenantId)));
        }

        // The moved node's new pool may now be over its ceiling. New consumption is blocked by
        // the seat lease; existing members are never deactivated (docs/24).
        var covering = CoveringOf(moved.Id);
        var anchorIds = anchors.Select(a => a.TenantId).ToHashSet();
        var covered = nodes
            .Where(t => byId.TryGetValue(covering.AnchorTenantId, out var anchor)
                && TenantEntity.IsSelfOrDescendant(anchor.Path, t.Path)
                && !Shadowed(t, covering.AnchorTenantId, anchorIds))
            .Select(t => t.Id).ToList();
        var used = await tam.Db.Set<TenantMembershipEntity>().AcrossTenants()
            .CountAsync(m => m.Active && covered.Contains(m.TenantId), ct);
        if (used > covering.Subscription.Seats)
            findings.Add(SubscriptionFindings.SeatOverflow
                .With(("used", used), ("seats", covering.Subscription.Seats)));

        return findings;
    }

    private static bool Shadowed(TenantEntity tenant, string anchorId, HashSet<string> anchorIds)
    {
        var chain = tenant.AncestorIds();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i] == anchorId) return false;
            if (anchorIds.Contains(chain[i])) return true;
        }
        return false;
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
        string DisplayName);

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
        public string DisplayName { get; init; } = "";
        [LabelKey("labels.parent")]
        public string? ParentId { get; init; }
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
