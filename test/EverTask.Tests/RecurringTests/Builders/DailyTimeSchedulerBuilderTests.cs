using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Builders;

public class DailyTimeSchedulerBuilderTests
{
    private RecurringTask _task;
    private DailyTimeSchedulerBuilder _builder;

    public DailyTimeSchedulerBuilderTests()
    {
        _task    = new RecurringTask();
        _builder = new DailyTimeSchedulerBuilder(_task);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTime_SetsOnTimesForDayInterval()
    {
        _task.DayInterval = new DayInterval(1);
        var time = new TimeOnly(12, 30);

        _builder.AtTime(time);

        Assert.Contains(time.ToUniversalTime(), _task.DayInterval.OnTimes);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTime_SetsOnTimesForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        var time = new TimeOnly(12, 30);

        _builder.AtTime(time);

        Assert.Contains(time.ToUniversalTime(), _task.MonthInterval.OnTimes);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTimes_SetsMultipleOnTimesForDayInterval()
    {
        _task.DayInterval = new DayInterval(1);
        var times = new[] { new TimeOnly(8, 0), new TimeOnly(16, 0) };

        _builder.AtTimes(times);

        Assert.Equal(times.Select(t => t.ToUniversalTime()), _task.DayInterval.OnTimes);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTimes_SetsMultipleOnTimesForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        var times = new[] { new TimeOnly(8, 0), new TimeOnly(16, 0) };

        _builder.AtTimes(times);

        Assert.Equal(times.Select(t => t.ToUniversalTime()), _task.MonthInterval.OnTimes);
    }
}
