namespace Tam.AspNetCore;

/// <summary>
/// Fans committed effects to SSE subscribers across ALL app instances (docs/12, review-round-3).
/// The in-process default reaches only this node's subscribers; the Postgres adapter puts each
/// payload on a LISTEN/NOTIFY channel so a grid open on instance B refreshes from a commit on
/// instance A. The event *bus* (subscribers, outbound integrations) is a separate, durable concern
/// owned by the outbox — this seam is only the live-refresh edge.
/// </summary>
public interface IEffectBackplane
{
    /// <summary>Publish a committed operation's effects for delivery on every instance (this one
    /// included). Best-effort and fire-and-forget: a refresh hint must never fail the operation.</summary>
    void Send(string tenantId, string operationId, IReadOnlyList<object> effects);
}

/// <summary>Single-instance default: deliver straight to this node's subscribers, no transport.</summary>
public sealed class LocalEffectBackplane(EffectBroadcaster broadcaster) : IEffectBackplane
{
    public void Send(string tenantId, string operationId, IReadOnlyList<object> effects)
    {
        if (effects.Count == 0) return;
        broadcaster.Deliver(tenantId, EffectBroadcaster.Payload(tenantId, operationId, effects));
    }
}
