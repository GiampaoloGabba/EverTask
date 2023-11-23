using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders;

public class IntervalSchedulerBuilderTests
{
    private RecurringTask _task;
    private IntervalSchedulerBuilder _builder;

    public IntervalSchedulerBuilderTests()
    {
        _task = new RecurringTask();
        _builder = new IntervalSchedulerBuilder(_task);
    }

    [Fact]
    public void IntervalSchedulerBuilder_EverySecond_SetsSecondInterval()
    {
        _builder.EverySecond();
        Assert.NotNull(_task.SecondInterval);
        Assert.Equal(1, _task.SecondInterval.Interval);
    }

    [Fact]
    public void IntervalSchedulerBuilder_EveryMinute_SetsMinuteInterval()
    {
        _builder.EveryMinute();
        Assert.NotNull(_task.MinuteInterval);
        Assert.Equal(1, _task.MinuteInterval.Interval);
    }

    [Fact]
    public void IntervalSchedulerBuilder_EveryHour_SetsHourInterval()
    {
        _builder.EveryHour();
        Assert.NotNull(_task.HourInterval);
        Assert.Equal(1, _task.HourInterval.Interval);
    }

    [Fact]
    public void IntervalSchedulerBuilder_EveryDay_SetsDayInterval()
    {
        _builder.EveryDay();
        Assert.NotNull(_task.DayInterval);
        Assert.Equal(1, _task.DayInterval.Interval);
    }

    [Fact]
    public void IntervalSchedulerBuilder_EveryMonth_SetsMonthInterval()
    {
        _builder.EveryMonth();
        Assert.NotNull(_task.MonthInterval);
        Assert.Equal(1, _task.MonthInterval.Interval);
    }

    [Fact]
    public void IntervalSchedulerBuilder_OnHours_SetsHourInterval()
    {
        _builder.OnHours();
        Assert.NotNull(_task.HourInterval);
        Assert.Equal(1, _task.HourInterval.Interval);
    }

    [Fact]
    public void IntervalSchedulerBuilder_OnDays_SetsDayInterval()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Friday };
        _builder.OnDays(days);
        Assert.NotNull(_task.DayInterval);
        Assert.NotEmpty(_task.DayInterval.OnDays);
    }

    [Fact]
    public void IntervalSchedulerBuilder_OnMonths_SetsMonthInterval()
    {
        var months = new[] { 1, 6, 12 };
        _builder.OnMonths(months);
        Assert.NotNull(_task.MonthInterval);
        Assert.NotEmpty(_task.MonthInterval.OnMonths);
    }
}
