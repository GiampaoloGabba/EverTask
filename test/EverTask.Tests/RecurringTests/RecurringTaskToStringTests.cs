using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

public class RecurringTaskToStringTests
{
    [Fact]
    public void ToString_WithMinuteInterval_ShouldIncludeMinuteDetails()
    {
        var task = new RecurringTask { MinuteInterval = new MinuteInterval(15) { OnSecond = 30 } };
        var str = task.ToString();

        Assert.Contains("every 15 minute(s) at second 30", str);
    }

    [Fact]
    public void ToString_WithHourInterval_ShouldIncludeHourDetails()
    {
        var task = new RecurringTask { HourInterval = new HourInterval(2) { OnMinute = 15 } };
        var str = task.ToString();

        Assert.Contains("every 2 hour(s) at minute 15", str);
    }

    [Fact]
    public void ToString_WithMultipleHourInterval_ShouldIncludeHourDetails()
    {
        var task = new RecurringTask { HourInterval = new HourInterval(0, new[] { 1, 15, 16 }) { OnMinute = 15 } };
        var str  = task.ToString();

        Assert.Contains("at hour(s) 1 - 15 - 16 at minute 15", str);
    }

    [Fact]
    public void ToString_WithDayIntervalAndSpecificDays_ShouldIncludeDayDetails()
    {
        var task = new RecurringTask { DayInterval = new DayInterval(1, new[] { DayOfWeek.Monday, DayOfWeek.Friday }) };
        var str = task.ToString();

        Assert.Contains("every 1 day(s) at 00:00 on Monday - Friday", str);
    }

    [Fact]
    public void ToString_WithMonthIntervalAndSpecificMonths_ShouldIncludeMonthDetails()
    {
        var task = new RecurringTask { MonthInterval = new MonthInterval(3, new[] { 1, 6, 12 }) };
        var str = task.ToString();

        Assert.Contains("every 3 month(s) at 00:00 in 1 - 6 - 12", str);
    }

    [Fact]
    public void ToString_WithRunNowAndInterval_ShouldIncludeRunNowAndInterval()
    {
        var task = new RecurringTask { RunNow = true, SecondInterval = new SecondInterval(10) };
        var str = task.ToString();

        Assert.Contains("Run immediately then every 10 second(s)", str);
    }

    [Fact]
    public void ToString_WithInitialDelayAndInterval_ShouldIncludeDelayAndInterval()
    {
        var delay = TimeSpan.FromMinutes(5);
        var task = new RecurringTask { InitialDelay = delay, MinuteInterval = new MinuteInterval(30) };
        var str = task.ToString();

        Assert.Contains($"Start after a delay of {delay} then every 30 minute(s)", str);
    }


}

