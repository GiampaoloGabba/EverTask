using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Builders;

public class MonthlySchedulerBuilderTests
{
    private RecurringTask _task;
    private MonthlySchedulerBuilder _builder;

    public MonthlySchedulerBuilderTests()
    {
        _task    = new RecurringTask();
        _builder = new MonthlySchedulerBuilder(_task);
    }

    [Fact]
    public void MonthlySchedulerBuilder_OnDay_SetsOnDayForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        int day = 15;

        _builder.OnDay(day);

        Assert.Equal(day, _task.MonthInterval.OnDay);
    }

    [Fact]
    public void MonthlySchedulerBuilder_OnDays_SetsOnDaysForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        var days = new[] { 10, 20, 30 };

        _builder.OnDays(days);

        Assert.Equal(days, _task.MonthInterval.OnDays);
    }

    [Fact]
    public void MonthlySchedulerBuilder_OnFirst_SetsOnFirstForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        var dayOfWeek = DayOfWeek.Monday;

        _builder.OnFirst(dayOfWeek);

        Assert.Equal(dayOfWeek, _task.MonthInterval.OnFirst);
    }
}

