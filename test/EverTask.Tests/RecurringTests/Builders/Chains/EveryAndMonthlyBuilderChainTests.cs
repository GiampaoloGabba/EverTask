using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class EveryAndMonthlyBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public EveryAndMonthlyBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_EverySecond_And_MaxRuns()
    {
        _builder.Schedule().EverySecond().MaxRuns(20);

        Assert.NotNull(_builder.RecurringTask.SecondInterval);
        Assert.Equal(1, _builder.RecurringTask.SecondInterval.Interval);
        Assert.Equal(20, _builder.RecurringTask.MaxRuns);
    }

    [Fact]
    public void Should_Set_EveryMonth_OnMultipleDays()
    {
        var days = new[] { 5, 15, 25 };
        _builder.Schedule().EveryMonth().OnDays(days);

        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(days, _builder.RecurringTask.MonthInterval.OnDays);
    }

    [Fact]
    public void Should_Set_RunAt_EveryDay_AtSpecificTime()
    {
        var dateTimeOffset = DateTimeOffset.UtcNow.AddDays(1);
        var time           = new TimeOnly(10, 0);

        _builder.RunAt(dateTimeOffset).Then().EveryDay().AtTime(time);

        Assert.Equal(dateTimeOffset, _builder.RecurringTask.SpecificRunTime);
        Assert.NotNull(_builder.RecurringTask.DayInterval);
        Assert.Contains(time.ToUniversalTime(), _builder.RecurringTask.DayInterval.OnTimes);
    }
}

