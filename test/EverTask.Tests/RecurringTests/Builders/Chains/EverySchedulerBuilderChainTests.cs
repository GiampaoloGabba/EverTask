using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class EverySchedulerBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public EverySchedulerBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_Every_Month_OnSpecificDays()
    {
        var days = new[] { 1, 15, 30 };

        _builder.Schedule().EveryMonth().OnDays(days);

        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(days, _builder.RecurringTask.MonthInterval.OnDays);
    }
}
