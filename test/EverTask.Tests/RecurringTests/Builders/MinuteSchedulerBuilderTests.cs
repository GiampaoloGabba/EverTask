using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Builders;

public class MinuteSchedulerBuilderTests
{
    private RecurringTask _task;
    private MinuteSchedulerBuilder _builder;

    public MinuteSchedulerBuilderTests()
    {
        _task    = new RecurringTask();
        _builder = new MinuteSchedulerBuilder(_task);
    }

    [Fact]
    public void MinuteSchedulerBuilder_AtSecond_SetsOnSecondForMinuteInterval()
    {
        _task.MinuteInterval = new MinuteInterval(1);
        int second = 15;

        _builder.AtSecond(second);

        Assert.Equal(second, _task.MinuteInterval.OnSecond);
    }

    [Fact]
    public void MinuteSchedulerBuilder_AtSecond_SetsOnSecondForHourInterval()
    {
        _task.HourInterval = new HourInterval(1);
        int second = 45;

        _builder.AtSecond(second);

        Assert.Equal(second, _task.HourInterval.OnSecond);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void MinuteSchedulerBuilder_AtSecond_ThrowsExceptionForInvalidSecond(int second)
    {
        _task.MinuteInterval = new MinuteInterval(1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _builder.AtSecond(second));
        Assert.Equal("second", exception.ParamName);
    }
}
