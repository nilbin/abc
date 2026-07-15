using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Tam.AspNetCore;

/// <summary>
/// Sanctioned re-execution of a parked envelope (docs/28 approvals seam 3): the stored wire body
/// runs through the FULL pipeline again — authorization, validation, rules, gates, audit — as its
/// ORIGINAL initiator, in a fresh scope pinned to the envelope's tenant.
///
/// The attribution and safety properties, by construction:
/// - the actor is re-resolved AS OF NOW (a revoked grant or deactivated account fails the replay
///   — approval releases a block, it never escalates the initiator);
/// - <see cref="InvocationSource.Workflow"/> marks the run as sanctioned — the parking gate lets
///   it pass, while every OTHER gate still runs (approval is not a bypass of other invariants);
/// - the envelope id rides <see cref="OperationContext.CorrelationId"/> into the audit entry, so
///   the operation's audit (actor = initiator) links to the envelope whose own trail records who
///   released it — dual attribution without a second actor field;
/// - the envelope id is also the idempotency key, scoped to the initiator, so a re-delivered
///   approval effect replays the stored outcome instead of executing twice.
/// </summary>
public sealed class EnvelopeReplay(
    TamModel model,
    IServiceProvider services,
    Func<IServiceProvider, DbContext> dbResolver)
{
    /// <summary>The idempotency-key prefix marking a replayed envelope.</summary>
    public const string KeyPrefix = "replay:";

    public Task<OperationResponse> ReplayAsync(
        string operationId, JsonElement body, Guid initiatorAccountId,
        string tenantId, string envelopeId, string culture, CancellationToken ct)
        => PinnedScope.RunAsync(services, tenantId, async (sp, c) =>
        {
            var db = dbResolver(sp);
            var actor = ClaimsActorProvider.Resolve(db, model, initiatorAccountId, tenantId);
            if (actor is null)
            {
                var finding = model.Locales.Resolve(
                    PipelineFindings.ReplayActorUnavailable.Create(), culture);
                return new OperationResponse(null, [finding], [], null, null);
            }

            var context = new OperationContext
            {
                Actor = actor,
                TenantId = new TenantId(tenantId),
                Source = InvocationSource.Workflow,
                Culture = culture,
                IdempotencyKey = KeyPrefix + envelopeId,
                CorrelationId = envelopeId,
                Services = sp,
            };
            return await sp.GetRequiredService<OperationExecutor>()
                .ExecuteAsync(operationId, body, context, c);
        }, ct);
}
