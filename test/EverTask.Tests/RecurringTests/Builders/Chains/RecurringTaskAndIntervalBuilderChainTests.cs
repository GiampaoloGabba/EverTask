using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class RecurringTaskAndIntervalBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public RecurringTaskAndIntervalBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_RunNow_Then_UseCron()
    {
        var cronExpression = "*/30 * * * *"; // Every 30 minutes
        _builder.RunNow().Then().UseCron(cronExpression);

        Assert.True(_builder.RecurringTask.RunNow);
        Assert.Equal(cronExpression, _builder.RecurringTask.CronInterval!.CronExpression);
    }

    [Fact]
    public void Should_Set_RunDelayed_Then_EveryHour()
    {
        var delay = TimeSpan.FromHours(2);

        _builder.RunDelayed(delay).Then().EveryHour();

        Assert.Equal(delay, _builder.RecurringTask.InitialDelay);
        Assert.NotNull(_builder.RecurringTask.HourInterval);
        Assert.Equal(1, _builder.RecurringTask.HourInterval.Interval);
    }
}

