using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class BuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public BuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_EveryMinute_AtSpecificSecond()
    {
        _builder.Schedule().EveryMinute().AtSecond(30);

        Assert.NotNull(_builder.RecurringTask.MinuteInterval);
        Assert.Equal(30, _builder.RecurringTask.MinuteInterval.OnSecond);
    }

    [Fact]
    public void Should_Set_EveryHour_AtSpecificMinute_And_MaxRuns()
    {
        _builder.Schedule().EveryHour().AtMinute(45).MaxRuns(10);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(45, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Equal(10, _builder.RecurringTask.MaxRuns);
        Assert.Null(_builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryHour_AtSpecificMinute_And_RunUntil_UTC()
    {
        var runUntil = DateTimeOffset.Now.AddMinutes(2);
        _builder.Schedule().EveryHour().AtMinute(45).RunUntil(runUntil);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(45, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Null(_builder.RecurringTask.MaxRuns);
        Assert.Equal(runUntil.ToUniversalTime(), _builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryDay_AtMultipleTimes()
    {
        var times = new[] { new TimeOnly(9, 0), new TimeOnly(15, 0), new TimeOnly(21, 0) };
        _builder.Schedule().EveryDay().AtTimes(times);

        Assert.NotNull(_builder.RecurringTask.DayInterval);
        Assert.Equal(times.Select(t => t.ToUniversalTime()), _builder.RecurringTask.DayInterval.OnTimes);
    }

    [Fact]
    public void Should_Set_RunNow_EveryMonth_OnSpecificDay()
    {
        _builder.RunNow().Then().EveryMonth().OnDay(15);

        Assert.True(_builder.RecurringTask.RunNow);
        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(15, _builder.RecurringTask.MonthInterval.OnDay);
    }
}
