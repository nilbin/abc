namespace Tam.AspNetCore;

/// <summary>
/// One retry policy for all integration processing (docs/10 + docs/25). The inbound inbox and the
/// outbound task queue dead-letter after the same number of attempts and space retries by the same
/// exponential backoff, so "retry" means one thing across the framework rather than two hand-tuned
/// behaviours. Deterministic (no jitter) so it is testable; jitter is a natural later addition for
/// thundering-herd avoidance and is noted as a ceiling.
/// </summary>
public sealed class RetryPolicy(TimeSpan baseDelay, TimeSpan maxDelay, int maxAttempts = 3)
{
    /// <summary>Framework default: 30s base, doubling, capped at 1h, dead-lettered after 3 tries.</summary>
    public static readonly RetryPolicy Default =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), maxAttempts: 3);

    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>
    /// The instant of the next attempt after <paramref name="attempts"/> failures: exponential
    /// (base·2^(attempts-1)) capped at <c>maxDelay</c>. <paramref name="attempts"/> is the count
    /// AFTER the try that just failed (so the first backoff, attempts==1, is exactly baseDelay).
    /// </summary>
    public DateTimeOffset NextAttempt(int attempts, DateTimeOffset from)
    {
        var n = Math.Max(1, attempts);
        // 2^(n-1) without overflowing double at absurd attempt counts.
        var factor = n >= 32 ? double.MaxValue : Math.Pow(2, n - 1);
        var seconds = Math.Min(maxDelay.TotalSeconds, baseDelay.TotalSeconds * factor);
        return from + TimeSpan.FromSeconds(seconds);
    }

    /// <summary>True once a unit has failed enough times to be dead-lettered.</summary>
    public bool IsExhausted(int attempts) => attempts >= MaxAttempts;

    /// <summary>Resolve the configured policy, falling back to the default outside DI.</summary>
    public static RetryPolicy Resolve(IServiceProvider? services) =>
        services?.GetService(typeof(RetryPolicy)) as RetryPolicy ?? Default;
}
