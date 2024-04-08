using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class IntervalSchedulerBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public IntervalSchedulerBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_EveryDay_AtSpecificTime_And_MaxRuns()
    {
        var time = new TimeOnly(8, 30);

        _builder.Schedule().EveryDay().AtTime(time).MaxRuns(5);

        Assert.NotNull(_builder.RecurringTask.DayInterval);
        Assert.Contains(time.ToUniversalTime(), _builder.RecurringTask.DayInterval.OnTimes);
        Assert.Equal(5, _builder.RecurringTask.MaxRuns);
        Assert.Null(_builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryDay_AtSpecificTime_And_RunUntil()
    {
        var runUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        var time     = new TimeOnly(8, 30);

        _builder.Schedule().EveryDay().AtTime(time).RunUntil(runUntil);

        Assert.NotNull(_builder.RecurringTask.DayInterval);
        Assert.Contains(time.ToUniversalTime(), _builder.RecurringTask.DayInterval.OnTimes);
        Assert.Null(_builder.RecurringTask.MaxRuns);
        Assert.Equal(runUntil, _builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryHour_OnSpecificMinute()
    {
        _builder.Schedule().EveryHour().AtMinute(15);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(15, _builder.RecurringTask.HourInterval.OnMinute);
    }
}

