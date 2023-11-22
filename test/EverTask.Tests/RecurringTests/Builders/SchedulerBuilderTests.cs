using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders;

public class SchedulerBuilderTests
{
    private RecurringTask _task;
    private EverySchedulerBuilder _builder;

    public SchedulerBuilderTests()
    {
        _task    = new RecurringTask();
        _builder = new EverySchedulerBuilder(_task, 10);
    }

    [Fact]
    public void EverySchedulerBuilder_SetsSecondInterval()
    {
        _builder.Seconds();
        Assert.NotNull(_task.SecondInterval);
        Assert.Equal(10, _task.SecondInterval.Interval);
    }

    [Fact]
    public void EverySchedulerBuilder_SetsMinuteInterval()
    {
        _builder.Minutes();
        Assert.NotNull(_task.MinuteInterval);
        Assert.Equal(10, _task.MinuteInterval.Interval);
    }

    [Fact]
    public void EverySchedulerBuilder_SetsHourInterval()
    {
        _builder.Hours();
        Assert.NotNull(_task.HourInterval);
        Assert.Equal(10, _task.HourInterval.Interval);
    }

    [Fact]
    public void EverySchedulerBuilder_SetsDayInterval()
    {
        _builder.Days();
        Assert.NotNull(_task.DayInterval);
        Assert.Equal(10, _task.DayInterval.Interval);
    }

    [Fact]
    public void EverySchedulerBuilder_SetsMonthInterval()
    {
        _builder.Months();
        Assert.NotNull(_task.MonthInterval);
        Assert.Equal(10, _task.MonthInterval.Interval);
    }
}
