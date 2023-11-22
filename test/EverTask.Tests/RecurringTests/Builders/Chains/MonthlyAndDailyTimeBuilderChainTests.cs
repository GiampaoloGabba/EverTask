using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class MonthlyAndDailyTimeBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public MonthlyAndDailyTimeBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_EveryMonth_OnSpecificDay_AtTime()
    {
        var time = new TimeOnly(20, 0);

        _builder.Schedule().EveryMonth().OnDay(10).AtTime(time);

        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(10, _builder.RecurringTask.MonthInterval.OnDay);
        Assert.Contains(time.ToUniversalTime(), _builder.RecurringTask.MonthInterval.OnTimes);
    }

    [Fact]
    public void Should_Set_EveryMonth_OnFirstDayOfWeek_AtMultipleTimes()
    {
        var times     = new[] { new TimeOnly(9, 0), new TimeOnly(15, 0) };
        var dayOfWeek = DayOfWeek.Monday;

        _builder.Schedule().EveryMonth().OnFirst(dayOfWeek).AtTimes(times);

        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(dayOfWeek, _builder.RecurringTask.MonthInterval.OnFirst);
        Assert.Equal(times.Select(t => t.ToUniversalTime()), _builder.RecurringTask.MonthInterval.OnTimes);
    }
}

