using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class IntervalAndMonthlyBuilderChainTests
{
    private RecurringTaskBuilder _builder;

    public IntervalAndMonthlyBuilderChainTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void Should_Set_UseCron_And_MaxRuns()
    {
        var cronExpression = "0 12 * * *"; // Every day at noon

        _builder.Schedule().UseCron(cronExpression).MaxRuns(5);

        Assert.Equal(cronExpression, _builder.RecurringTask.CronInterval!.CronExpression);
        Assert.Equal(5, _builder.RecurringTask.MaxRuns);
        Assert.Null(_builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_UseCron_And_RunUntil()
    {
        var cronExpression = "0 13 0 * *"; // Every day at 1pm

        var runUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        _builder.Schedule().UseCron(cronExpression).RunUntil(runUntil);

        Assert.Equal(cronExpression, _builder.RecurringTask.CronInterval!.CronExpression);
        Assert.Null(_builder.RecurringTask.MaxRuns);
        Assert.Equal(runUntil, _builder.RecurringTask.RunUntil);
    }

    [Fact]
    public void Should_Set_RunDelayed_EveryMonth_OnFirstDayOfWeek()
    {
        var delay     = TimeSpan.FromDays(1);
        var dayOfWeek = DayOfWeek.Monday;

        _builder.RunDelayed(delay).Then().EveryMonth().OnFirst(dayOfWeek);

        Assert.Equal(delay, _builder.RecurringTask.InitialDelay);
        Assert.NotNull(_builder.RecurringTask.MonthInterval);
        Assert.Equal(dayOfWeek, _builder.RecurringTask.MonthInterval.OnFirst);
    }
}
