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
/// gate (targets are ApprovalRule rows, not compile time), gate.Park (the envelope survives the
/// rollback), and EnvelopeReplay (the release runs as the original initiator, dual-attributed).
/// </summary>
[TamPlugin("approvals")]
public sealed class ApprovalsPlugin : ITamPlugin
{
    public void Configure(PluginBuilder plugin)
    {
        plugin.Model.AddDiscovered();

        foreach (var culture in new[] { "sv", "en" })
        {
            using var stream = typeof(ApprovalsPlugin).Assembly
                .GetManifestResourceStream($"Approvals.locales.{culture}.json");
            if (stream is null) continue;
            plugin.LocaleDefaults(
                culture, JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []);
        }

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

        // ---- Seam 1: ONE wildcard gate. Which operations it blocks is ApprovalRule data. ----
        plugin.Gate(GateDefinition.Wildcard, async (gate, ct) =>
        {
            // The sanction (seam 3): a replay the plugin itself released passes. Workflow is
            // only ever set by compiled code — never by a wire caller — so this is not forgeable.
            if (gate.Context.Source == InvocationSource.Workflow) return Result.Success();
            // Never gate our own surface: approving an approval must not need an approval.
            if (gate.OperationId.StartsWith("approvals.", StringComparison.Ordinal))
                return Result.Success();

            var db = ((ITamDb)gate.Services.GetService(typeof(ITamDb))!).Db;
            var tenant = gate.Context.TenantId.Value;
            var rules = await db.Set<ApprovalRule>()
                .Where(r => r.OperationId == gate.OperationId && !r.Retired)
                .ToListAsync(ct);
            var rule = rules.FirstOrDefault(r => Applies(r, gate.Input));
            if (rule is null) return Result.Success();

            // Seam 2: keep the envelope, lose the attempt. The parked work runs AFTER the
            // domain transaction rolls back, in a fresh scope pinned to this tenant.
            var envelope = new ApprovalRequest
            {
                Id = Guid.NewGuid(),
                TenantId = tenant,
                RuleId = rule.Id,
                OperationId = gate.OperationId,
                BodyJson = gate.Input.GetRawText(),
                PayloadHash = gate.PayloadHash,
                InitiatorActorId = gate.Context.Actor.Id,
                Culture = gate.Context.Culture,
                CreatedAtIso = IsoTime.Now(),
            };
            gate.Park(async (services, c) =>
            {
                var fresh = ((ITamDb)services.GetService(typeof(ITamDb))!).Db;
                // A retried submit re-blocks but never double-parks: the pipeline's payload
                // hash is the envelope's natural dedupe key.
                if (await fresh.Set<ApprovalRequest>().AnyAsync(
                        r => r.PayloadHash == envelope.PayloadHash
                             && r.Status == ApprovalRequest.Pending, c))
                    return;
                fresh.Add(envelope);
                // The notification event rides the outbox from the SAME commit as the envelope:
                // the approver is mailed if and only if there is a request to act on.
                fresh.Add(new OutboxRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant,
                    OperationId = gate.OperationId,
                    EventType = "approvals.requested",
                    PayloadJson = JsonSerializer.Serialize(new { requestId = envelope.Id }),
                    CreatedAtIso = IsoTime.Now(),
                });
                await fresh.SaveChangesAsync(c);
            });
            return ApprovalsFindings.Pending.With(("operation", gate.OperationId));
        });

        // ---- Notify the approvers (post-commit, via the outbox + the email seam). ----
        plugin.OnEffect("approvals.requested", async (effect, services, ct) =>
        {
            var db = ((ITamDb)services.GetService(typeof(ITamDb))!).Db;
            var requestId = effect.Payload.GetProperty("requestId").GetGuid();
            var request = await db.Set<ApprovalRequest>()
                .SingleOrDefaultAsync(r => r.Id == requestId, ct);
            var rule = request is null ? null : await db.Set<ApprovalRule>()
                .SingleOrDefaultAsync(r => r.Id == request.RuleId, ct);
            if (request is null || rule is null) return;

            var model = (TamModel)services.GetService(typeof(TamModel))!;
            var email = (ITamEmail)services.GetService(typeof(ITamEmail))!;
            string Localize(string key, IReadOnlyDictionary<string, object?> args) =>
                LocaleCatalogs.Format(
                    model.Locales.Lookup(key, request.Culture) ?? key, args, request.Culture);

            var approvers = await ApprovalGroups.EffectiveApproversAsync(db, rule.GroupId, ct);
            var ids = approvers.Select(Guid.Parse).ToList();
            var addresses = await db.Set<AccountEntity>()
                .Where(a => ids.Contains(a.Id) && a.Active)
                .Select(a => a.Email).ToListAsync(ct);
            var args = new Dictionary<string, object?> { ["operation"] = request.OperationId };
            foreach (var address in addresses)
                await email.SendAsync(address,
                    Localize("approvals.email.requested-subject", args),
                    Localize("approvals.email.requested-body", args), ct);
        });

        // ---- Seam 3: the release. Replay the envelope as its ORIGINAL initiator. ----
        plugin.OnEffect("approvals.approved", async (effect, services, ct) =>
        {
            var db = ((ITamDb)services.GetService(typeof(ITamDb))!).Db;
            var requestId = effect.Payload.GetProperty("requestId").GetGuid();
            var request = await db.Set<ApprovalRequest>()
                .SingleOrDefaultAsync(r => r.Id == requestId, ct);
            // Only an approved request replays; executed/failed means a redelivered effect
            // whose work is already done (delivery is at-least-once).
            if (request is null || request.Status != ApprovalRequest.Approved) return;

            using var body = JsonDocument.Parse(request.BodyJson);
            var replay = (EnvelopeReplay)services.GetService(typeof(EnvelopeReplay))!;
            // Grants re-resolve as of NOW; Workflow marks the sanction; the request id is the
            // audit correlation AND the initiator-scoped idempotency key — so even if THIS
            // status write is lost and the effect redelivers, the operation runs once.
            var response = await replay.ReplayAsync(
                request.OperationId, body.RootElement, Guid.Parse(request.InitiatorActorId),
                request.TenantId, request.Id.ToString("N"), request.Culture, ct);

            var failed = response.Findings.Any(f => f.Severity == FindingSeverity.Error);
            request.Status = failed ? ApprovalRequest.Failed : ApprovalRequest.Executed;
            request.Outcome = failed
                ? response.Findings.First(f => f.Severity == FindingSeverity.Error).Code
                : response.AuditReference;
            await db.SaveChangesAsync(ct);
        });
    }

    /// <summary>No threshold → the rule always applies; with one, the named wire field must be
    /// a number at or above the limit (a missing or non-numeric field does not trigger).</summary>
    private static bool Applies(ApprovalRule rule, JsonElement input) =>
        rule.ThresholdField is not { Length: > 0 } field || rule.Threshold is not { } limit
            || (input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetDecimal() >= limit);

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
