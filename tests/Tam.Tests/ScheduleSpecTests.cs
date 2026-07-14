using Tam.AspNetCore;

namespace Tam.Tests;

public class ScheduleSpecTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("every:15m")]
    [InlineData("every:2h")]
    [InlineData("every:1d")]
    [InlineData("daily:02:00")]
    [InlineData("daily:23:59")]
    public void Valid_specs_parse(string spec) => Assert.True(ScheduleSpec.IsValid(spec));

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("every:")]
    [InlineData("every:0m")]
    [InlineData("every:-5m")]
    [InlineData("every:10x")]
    [InlineData("daily:25:00")]
    [InlineData("weekly:mon")]
    public void Invalid_specs_are_rejected(string spec) => Assert.False(ScheduleSpec.IsValid(spec));

    [Fact]
    public void Interval_adds_to_the_reference_time()
    {
        Assert.True(ScheduleSpec.TryNext("every:15m", Base, out var next));
        Assert.Equal(Base.AddMinutes(15), next);
    }

    [Fact]
    public void Daily_rolls_to_tomorrow_when_the_time_already_passed_today()
    {
        // 02:00 is before 10:30 → next is tomorrow at 02:00.
        Assert.True(ScheduleSpec.TryNext("daily:02:00", Base, out var next));
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 2, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Daily_stays_today_when_the_time_is_still_ahead()
    {
        Assert.True(ScheduleSpec.TryNext("daily:22:00", Base, out var next));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("every:2000000000d")]   // days that overflow DateTimeOffset
    [InlineData("every:9999999999m")]   // minutes past int range
    public void Overflowing_intervals_fail_closed_instead_of_throwing(string spec)
    {
        // A malformed/absurd interval must return false, never throw into the scheduler tick.
        Assert.False(ScheduleSpec.TryNext(spec, Base, out _));
    }
}

public class RetryPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly RetryPolicy Policy =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), maxAttempts: 3);

    [Fact]
    public void First_backoff_is_exactly_the_base_delay()
    {
        // attempts == 1 (the first try just failed) → base delay, no doubling yet.
        Assert.Equal(T0.AddSeconds(10), Policy.NextAttempt(1, T0));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    public void Backoff_doubles_each_attempt(int attempts, int expectedSeconds) =>
        Assert.Equal(T0.AddSeconds(expectedSeconds), Policy.NextAttempt(attempts, T0));

    [Fact]
    public void Backoff_is_capped_at_the_maximum()
    {
        // 10s·2^19 would be ~60 days; the 5-minute cap holds.
        Assert.Equal(T0.AddMinutes(5), Policy.NextAttempt(20, T0));
    }

    [Fact]
    public void Absurd_attempt_counts_do_not_overflow()
    {
        var next = Policy.NextAttempt(100000, T0);   // must not throw
        Assert.Equal(T0.AddMinutes(5), next);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void Exhaustion_is_at_the_attempt_cap(int attempts, bool exhausted) =>
        Assert.Equal(exhausted, Policy.IsExhausted(attempts));
}
