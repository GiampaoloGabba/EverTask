using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class HourAndMinuteBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public HourAndMinuteBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_EveryHour_AtSpecificMinute_And_MaxRuns()
    {
        _builder.Schedule().EveryHour().AtMinute(30).MaxRuns(10);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(30, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Equal(10, _builder.RecurringTask.MaxRuns);
        Assert.Null(_builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryHour_AtSpecificMinute_And_RunUntil()
    {
        var runUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        _builder.Schedule().EveryHour().AtMinute(30).RunUntil(runUntil);

        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(30, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Null(_builder.RecurringTask.MaxRuns);
        Assert.Equal(runUntil, _builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_EveryMinute_AtSpecificSecond()
    {
        _builder.Schedule().EveryMinute().AtSecond(15);

        Assert.NotNull(_builder.RecurringTask.MinuteInterval);
        Assert.Equal(15, _builder.RecurringTask.MinuteInterval.OnSecond);
    }

    [Fact]
    public void Should_Set_RunNow_EveryHour_AtMinute_And_AtSecond()
    {
        _builder.RunNow().Then().EveryHour().AtMinute(45).AtSecond(30);

        Assert.True(_builder.RecurringTask.RunNow);
        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(45, _builder.RecurringTask.HourInterval.OnMinute);
        Assert.Equal(30, _builder.RecurringTask.HourInterval.OnSecond);
    }
}
