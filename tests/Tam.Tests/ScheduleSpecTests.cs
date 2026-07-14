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
