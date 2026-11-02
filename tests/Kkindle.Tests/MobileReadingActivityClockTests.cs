using Kkindle.Mobile;

namespace Kkindle.Tests;

public class MobileReadingActivityClockTests
{
    [Fact]
    public void BackgroundTimeIsExcludedEvenWhenThePauseIsShort()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var clock = new ReadingActivityClock(start);

        Assert.Equal(20, clock.Pause(start.AddSeconds(20)));
        Assert.Equal(0, clock.CountUntil(start.AddSeconds(50)));
        clock.Resume(start.AddSeconds(50));
        Assert.Equal(10, clock.CountUntil(start.AddSeconds(60)));
    }

    [Fact]
    public void LongIdleIntervalsAreNotCountedAndRepeatedResumeDoesNotResetTheClock()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var clock = new ReadingActivityClock(start);

        Assert.Equal(0, clock.CountUntil(start.AddMinutes(3)));
        clock.Resume(start.AddMinutes(3).AddSeconds(5));
        Assert.Equal(10, clock.CountUntil(start.AddMinutes(3).AddSeconds(10)));
    }
}
