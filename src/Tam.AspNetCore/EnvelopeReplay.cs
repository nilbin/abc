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

    /// <summary>
    /// Everything a replay needs, as ONE named shape — a parked envelope row maps to it field by
    /// field, and there is no adjacent-string parameter list to transpose. The initiator is the
    /// actor-id string exactly as <see cref="Actor.Id"/> gave it at park time.
    /// </summary>
    public sealed record Envelope(
        string OperationId,
        JsonElement Body,
        string InitiatorActorId,
        string TenantId,
        string EnvelopeId,
        string Culture);

    public Task<OperationResponse> ReplayAsync(Envelope envelope, CancellationToken ct)
        => PinnedScope.RunAsync(services, envelope.TenantId, async (sp, c) =>
        {
            // Fail closed on anything that does not resolve to a live account — including an
            // actor id that was never an account id at all.
            var db = dbResolver(sp);
            var actor = Guid.TryParse(envelope.InitiatorActorId, out var accountId)
                ? ClaimsActorProvider.Resolve(db, model, accountId, envelope.TenantId)
                : null;
            if (actor is null)
            {
                var finding = model.Locales.Resolve(
                    PipelineFindings.ReplayActorUnavailable.Create(), envelope.Culture);
                return new OperationResponse(null, [finding], [], null, null);
            }

            var context = new OperationContext
            {
                Actor = actor,
                TenantId = new TenantId(envelope.TenantId),
                Source = InvocationSource.Workflow,
                Culture = envelope.Culture,
                IdempotencyKey = KeyPrefix + envelope.EnvelopeId,
                CorrelationId = envelope.EnvelopeId,
                Services = sp,
            };
            return await sp.GetRequiredService<OperationExecutor>()
                .ExecuteAsync(envelope.OperationId, envelope.Body, context, c);
        }, ct);
}
