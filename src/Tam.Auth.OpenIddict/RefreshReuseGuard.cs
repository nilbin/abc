using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Tam.Auth;

/// <summary>
/// Reuse detection with FAMILY REVOCATION (no-BFF hardening, docs/26): OpenIddict rejects a
/// redeemed refresh token replayed after the reuse leeway, but out of the box the already-rotated
/// siblings stay usable. This handler runs just before the built-in entry validation; when the
/// presented refresh token (or authorization code) turns out to be redeemed beyond the leeway —
/// a replay, most plausibly a stolen token — it revokes the shared authorization and every token
/// descended from it, ending the session for thief and victim alike. The victim signs in again;
/// the thief holds nothing. The built-in handler that follows still produces the rejection.
/// </summary>
public sealed class RefreshReuseGuard(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations,
    IOptionsMonitor<OpenIddictServerOptions> options) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
            .UseScopedHandler<RefreshReuseGuard>()
            .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        if (context.Principal?.GetTokenId() is not { } tokenId) return;
        if (await tokens.FindByIdAsync(tokenId) is not { } entry) return;

        var type = await tokens.GetTypeAsync(entry);
        if (type is not (TokenTypeHints.RefreshToken or TokenTypeHints.AuthorizationCode)) return;
        if (await tokens.GetStatusAsync(entry) != Statuses.Redeemed) return;

        // A redeemed refresh token seen again WITHIN the leeway is the client's own concurrent
        // retry — the built-in handler accepts it; only a replay beyond the window is theft.
        // Authorization codes have no such window: any second redemption is a replay.
        if (type == TokenTypeHints.RefreshToken
            && options.CurrentValue.RefreshTokenReuseLeeway is { } leeway
            && await tokens.GetRedemptionDateAsync(entry) is { } redeemed
            && DateTimeOffset.UtcNow < redeemed + leeway)
            return;

        if (await tokens.GetAuthorizationIdAsync(entry) is not { } authorizationId) return;
        if (await authorizations.FindByIdAsync(authorizationId) is { } authorization)
            await authorizations.TryRevokeAsync(authorization);
        await foreach (var sibling in tokens.FindByAuthorizationIdAsync(authorizationId))
            await tokens.TryRevokeAsync(sibling);
    }
}
