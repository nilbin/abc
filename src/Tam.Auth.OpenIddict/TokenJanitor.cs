using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Tam.AspNetCore;

namespace Tam.Auth;

/// <summary>
/// Prunes expired/invalid tokens and authorizations from the OpenIddict store. Rotation makes
/// every refresh mint a new row, so without pruning the token table grows with every session —
/// the auth twin of the app-side RetentionJanitor. The threshold sits past the longest token
/// lifetime (14-day refresh), so nothing prunable is still redeemable.
/// </summary>
internal sealed class TokenJanitor(IServiceScopeFactory scopes) : TamBackgroundLoop(TimeSpan.FromHours(6))
{
    private static readonly TimeSpan Age = TimeSpan.FromDays(21);

    protected override async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var threshold = DateTimeOffset.UtcNow - Age;
        await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
            .PruneAsync(threshold, ct);
        await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
            .PruneAsync(threshold, ct);
    }
}
