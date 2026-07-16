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
        plugin.Model.AddDiscovered();
        plugin.LocaleDefaults();   // embedded locales/*.json by convention

        plugin.Model.Form<DefineGroup.Input>(
            "approvals.web.groups.define", "approvals.groups.define", form =>
        {
            form.Field(x => x.Name);
            form.Field(x => x.ParentGroupId);
        });

        plugin.Model.Form<AssignMember.Input>(
            "approvals.web.groups.assign", "approvals.groups.assign", form =>
        {
            form.Field(x => x.GroupId);
            form.Field(x => x.Email);
        });

        plugin.Model.Form<DefineRule.Input>(
            "approvals.web.rules.define", "approvals.rules.define", form =>
        {
            form.Field(x => x.OperationId);
            form.Field(x => x.GroupId);
            form.Field(x => x.ThresholdField);
            form.Field(x => x.Threshold).Renderer("money");
        });

        plugin.Model.Grid<RequestList.Result>(
            "approvals.web.requests", "approvals.requests.list", grid =>
        {
            grid.Column(x => x.OperationId);
            grid.Column(x => x.Initiator);
            grid.Column(x => x.Status);
            grid.Column(x => x.CreatedAtIso);
            grid.RowAction("approvals.approve");
            grid.RowAction("approvals.reject");
        });

        // Seam 1: ONE wildcard gate — which operations it blocks is ApprovalRule data. The
        // gate and both subscribers register from their own attributes ([GateAll]/[OnEffect]
        // below, via AddDiscovered); only the event CONTRACTS are declared here.
        plugin
            .PublishesEvent("approvals.requested", "requestId")
            .PublishesEvent("approvals.approved", "requestId");
    }

    /// <summary>
    /// Seam 1 + 2. Consults the tenant's rules for the operation at hand; a match parks the
    /// envelope (kept across the rollback) and blocks with approvals.pending.
    /// </summary>

    /// <summary>Persists the parked envelope + its notification event in one commit.</summary>

    /// <summary>Mails the rule's effective approver set through the framework seams.</summary>

    /// <summary>Seam 3: replay the approved envelope as its ORIGINAL initiator and record the
    /// outcome. Redelivery-safe twice over: only an APPROVED request replays, and the replay is
    /// idempotent by envelope id.</summary>

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
        var rule = rules.FirstOrDefault(r => Applies(r, gate.Input));
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
    private static bool Applies(ApprovalRule rule, JsonElement input) =>
        rule.ThresholdField is not { Length: > 0 } field || rule.Threshold is not { } limit
            || (input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetDecimal() >= limit);
}

[OnEffect("approvals.requested")]
internal sealed class NotifyApprovers(
    ITamDb tam, TamModel model, ITamEmail email, ITamDirectory directory) : IEffectHandler
{
    public async Task HandleAsync(EffectEvent effect, CancellationToken ct)
    {
        var requestId = effect.Payload.GetProperty("requestId").GetGuid();
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
        var requestId = effect.Payload.GetProperty("requestId").GetGuid();
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
