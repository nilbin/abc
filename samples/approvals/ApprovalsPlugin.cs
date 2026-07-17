using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tam;
using Tam.AspNetCore;
using Tam.EntityFrameworkCore;
using Tam.Generated;

namespace Approvals;

/// <summary>
/// The tutorial's Step 16 package (docs/28 D-AG4): purchase approvals from a workflow vendor —
/// nested approver groups, tenant-configured rules over host operation ids, parked envelopes,
/// sanctioned replay. The point is what it does NOT require: no change to any domain operation,
/// no approval engine in the framework. It is built ENTIRELY on the three seams: the wildcard
/// gate (targets are ApprovalRule rows, not compile time), Park (the envelope survives the
/// rollback), and EnvelopeReplay (the release runs as the original initiator, dual-attributed).
/// Every handler is a class constructed with ctor injection — no service locators.
/// </summary>
[TamPlugin("approvals")]
public sealed class ApprovalsPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.AddDiscovered();   // operations, views, [GateAll]/[OnEffect] behaviors

        // Seam 1: ONE wildcard gate — which operations it blocks is ApprovalRule data. The
        // gate and both subscribers register from their own attributes (below, via
        // AddDiscovered); only the event CONTRACTS are declared here.
        plugin
            .PublishesEvent("approvals.requested", "requestId")
            .PublishesEvent("approvals.approved", "requestId");

        plugin.AddPart<AdminSurface>();    // groups + rules configuration
        plugin.AddPart<ReviewSurface>();   // the approver's request queue
    }

    /// <summary>Host opt-in for the plugin's storage, like inspect's: one line in OnModelCreating.</summary>
    public static ModelBuilder AddApprovals(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApprovalGroup>(b =>
        {
            b.ToTable("approvals_groups");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Name });
        });
        modelBuilder.Entity<ApprovalGroupMember>(b =>
        {
            b.ToTable("approvals_group_members");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.GroupId });
        });
        modelBuilder.Entity<ApprovalRule>(b =>
        {
            b.ToTable("approvals_rules");
            b.HasKey(x => x.Id);
            // The gate's hot read: rules for (tenant, operation) — one indexed seek per request.
            b.HasIndex(x => new { x.TenantId, x.OperationId });
        });
        modelBuilder.Entity<ApprovalRequest>(b =>
        {
            b.ToTable("approvals_requests");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.PayloadHash });
        });
        return modelBuilder;
    }
}

/// <summary>The tenant admin's configuration surface: approver groups (define, assign
/// members) and approval rules (define, retire) — one declared page, suggested into the
/// host's administration area. Assign opens PREFILLED from the group row (the docs/32
/// RowForm contract: the list view deliberately carries the form's field names).</summary>
internal sealed class AdminSurface : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Form<DefineGroup.Input>(
            "approvals.web.groups.define", "approvals.groups.define");

        plugin.Form<AssignMember.Input>(
            "approvals.web.groups.assign", "approvals.groups.assign", form =>
        {
            form.Field(x => x.GroupId).Renderer("hidden");
            form.Field(x => x.Email);
        });

        plugin.Form<DefineRule.Input>(
            "approvals.web.rules.define", "approvals.rules.define", form =>
        {
            form.Field(x => x.OperationId);
            form.Field(x => x.GroupId);
            form.Field(x => x.ThresholdField);
            form.Field(x => x.Threshold).Renderer("money");
        });

        plugin.Grid<GroupList.Result>(
            "approvals.web.groups", "approvals.groups.list", grid =>
        {
            grid.Column(x => x.Name);
            grid.Column(x => x.ParentGroupId);
            grid.RowForm("approvals.groups.assign");
            grid.ToolbarAction("approvals.groups.define");
        });

        plugin.Grid<RuleList.Result>(
            "approvals.web.rules", "approvals.rules.list", grid =>
        {
            grid.RowAction("approvals.rules.retire");
            grid.ToolbarAction("approvals.rules.define");
        });

        plugin.Page("approvals.admin", page => page
            .Grid("approvals.web.groups", heading: "approvals.headings.groups")
            .Grid("approvals.web.rules", heading: "approvals.headings.rules"));

        plugin.Nav(nav => nav.Page("approvals.admin",
            page: "approvals.admin", suggest: "administration", order: 70));
    }
}

/// <summary>The approver's queue: pending requests with approve/reject row actions.</summary>
internal sealed class ReviewSurface : IPluginPart
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Grid<RequestList.Result>(
            "approvals.web.requests", "approvals.requests.list", grid =>
        {
            grid.Column(x => x.OperationId);
            grid.Column(x => x.Initiator);
            grid.Column(x => x.Status);
            grid.Column(x => x.CreatedAtIso);
            grid.RowAction("approvals.approve");
            grid.RowAction("approvals.reject");
        });

        plugin.Page("approvals.requests", page => page
            .Grid("approvals.web.requests"));

        plugin.Nav(nav => nav.Page("approvals.requests",
            page: "approvals.requests", suggest: "administration", order: 75));
    }
}

[GateAll]
internal sealed class ApprovalsGate(ITamDb tam) : IOperationGate
{
    public async Task<Result> CheckAsync(GateContext gate, CancellationToken ct)
    {
        // The sanction (seam 3): a replay the plugin itself released passes. Workflow is
        // only ever set by compiled code — never by a wire caller — so this is not forgeable.
        if (gate.Context.Source == InvocationSource.Workflow) return Result.Success();
        // Never gate our own surface: approving an approval must not need an approval.
        if (gate.OperationId.StartsWith("approvals.", StringComparison.Ordinal))
            return Result.Success();

        var rules = await tam.Db.Set<ApprovalRule>()
            .Where(r => r.OperationId == gate.OperationId && !r.Retired)
            .ToListAsync(ct);
        var rule = rules.FirstOrDefault(r => Applies(r, gate));
        if (rule is null) return Result.Success();

        // Seam 2: keep the envelope, lose the attempt. ParkEnvelope is constructed in the
        // FRESH scope after the domain transaction rolled back — its ITamDb cannot be this
        // gate's rolled-back one, by construction.
        gate.Park<ParkEnvelope, ApprovalRequest>(new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            TenantId = gate.Context.TenantId.Value,
            RuleId = rule.Id,
            OperationId = gate.OperationId,
            BodyJson = gate.Input.GetRawText(),
            PayloadHash = gate.PayloadHash,
            InitiatorActorId = gate.Context.Actor.Id,
            Culture = gate.Context.Culture,
            CreatedAtIso = IsoTime.Now(),
        });
        return ApprovalsFindings.Pending.With(("operation", gate.OperationId));
    }

    /// <summary>No threshold → the rule always applies; with one, the named wire field must
    /// be a number at or above the limit (a missing or non-numeric field does not trigger).</summary>
    private static bool Applies(ApprovalRule rule, GateContext gate) =>
        rule.ThresholdField is not { Length: > 0 } field || rule.Threshold is not { } limit
            || gate.Decimal(field) >= limit;
}

[OnEffect("approvals.requested")]
internal sealed class NotifyApprovers(
    ITamDb tam, TamModel model, ITamEmail email, ITamDirectory directory) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (effect.Guid("requestId") is not { } requestId) return;
        var request = await tam.Db.Set<ApprovalRequest>()
            .SingleOrDefaultAsync(r => r.Id == requestId, ct);
        var rule = request is null ? null : await tam.Db.Set<ApprovalRule>()
            .SingleOrDefaultAsync(r => r.Id == request.RuleId, ct);
        if (request is null || rule is null) return;

        var approvers = await ApprovalGroups.EffectiveApproversAsync(tam.Db, rule.GroupId, ct);
        var args = new Dictionary<string, object?> { ["operation"] = request.OperationId };
        foreach (var address in await directory.EmailsAsync(approvers.ToList(), ct))
            await email.SendAsync(address,
                model.Locales.Localize("approvals.email.requested-subject", request.Culture, args),
                model.Locales.Localize("approvals.email.requested-body", request.Culture, args), ct);
    }
}

[OnEffect("approvals.approved")]
internal sealed class ReleaseApproved(ITamDb tam, EnvelopeReplay replay) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        if (effect.Guid("requestId") is not { } requestId) return;
        var request = await tam.Db.Set<ApprovalRequest>()
            .SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null || request.Status != ApprovalRequest.Approved) return;

        using var body = JsonDocument.Parse(request.BodyJson);
        // Grants re-resolve as of NOW; Workflow marks the sanction; the request id is the
        // audit correlation AND the initiator-scoped idempotency key — so even if THIS
        // status write is lost and the effect redelivers, the operation runs once.
        var response = await replay.ReplayAsync(new EnvelopeReplay.Envelope(
            request.OperationId, body.RootElement, request.InitiatorActorId,
            request.TenantId, request.Id.ToString("N"), request.Culture), ct);

        var failed = response.Findings.Any(f => f.Severity == FindingSeverity.Error);
        request.Status = failed ? ApprovalRequest.Failed : ApprovalRequest.Executed;
        request.Outcome = failed
            ? response.Findings.First(f => f.Severity == FindingSeverity.Error).Code
            : response.AuditReference;
        await tam.Db.SaveChangesAsync(ct);
    }
}

internal sealed class ParkEnvelope(ITamDb tam) : IParkedWork<ApprovalRequest>
{
    public async Task RunAsync(ApprovalRequest envelope, CancellationToken ct)
    {
        // A retried submit re-blocks but never double-parks: the pipeline's payload hash
        // is the envelope's natural dedupe key.
        if (await tam.Db.Set<ApprovalRequest>().AnyAsync(
                r => r.PayloadHash == envelope.PayloadHash
                     && r.Status == ApprovalRequest.Pending, ct))
            return;
        tam.Db.Add(envelope);
        // The notification event rides the outbox from the SAME commit as the envelope:
        // the approver is mailed if and only if there is a request to act on.
        tam.Db.Publish("approvals.requested", new { requestId = envelope.Id }, envelope.OperationId);
        await tam.Db.SaveChangesAsync(ct);
    }
}
