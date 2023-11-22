using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

public class IntervalTests
{
    [Fact]
    public void MinuteIntervalCreate_ShouldSet_IntervalAndOnSecond()
    {
        var interval = 5;
        var onSecond = 10;

        var result = MinuteInterval.Create(interval, onSecond);

        Assert.Equal(interval, result.Interval);
        Assert.Equal(onSecond, result.OnSecond);
    }

    [Fact]
    public void DayIntervalCreate_ShouldSet_IntervalOnTimesAndOnDays()
    {
        var interval = 5;
        var onTimes  = new[] { TimeOnly.Parse("00:00") };
        var onDays   = new [] { Day.Monday };

        var result = DayInterval.Create(interval, onTimes, onDays);

        Assert.Equal(interval, result.Interval);
        Assert.Equal(onTimes, result.OnTimes);
        Assert.Equal(onDays, result.OnDays);
    }

    [Fact]
    public void MonthIntervalCreate_ShouldSet_IntervalOnDayOnFirstOnTimesAndOnMonths()
    {
        var interval = 5;
        var onDay    = 10;
        var onFirst  = Day.Monday;
        var onTimes  = new[] { TimeOnly.Parse("00:00") };
        var onMonths = new [] { Month.January };

        var result = MonthInterval.Create(interval, onDay, onFirst, onTimes, onMonths);

        Assert.Equal(interval, result.Interval);
        Assert.Equal(onDay, result.OnDay);
        Assert.Equal(onFirst, result.OnFirst);
        Assert.Equal(onTimes, result.OnTimes);
        Assert.Equal(onMonths, result.OnMonths);
    }
}
