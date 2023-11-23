using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Builders;

public class HourSchedulerBuilderTests
{
    private RecurringTask _task;
    private HourSchedulerBuilder _builder;

    public HourSchedulerBuilderTests()
    {
        _task    = new RecurringTask();
        _builder = new HourSchedulerBuilder(_task);
    }

    [Fact]
    public void HourSchedulerBuilder_AtMinute_SetsOnMinuteForHourInterval()
    {
        _task.HourInterval = new HourInterval(1);
        int minute = 30;

        _builder.AtMinute(minute);

        Assert.Equal(minute, _task.HourInterval.OnMinute);
    }

    [Fact]
    public void HourSchedulerBuilder_AtMinute_ThrowsExceptionForNullHourInterval()
    {
        _task.HourInterval = null;
        int minute = 30;

        var exception = Assert.Throws<ArgumentNullException>(() => _builder.AtMinute(minute));
        Assert.Equal("task.HourInterval", exception.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    public void HourSchedulerBuilder_AtMinute_ThrowsExceptionForInvalidMinute(int minute)
    {
        _task.HourInterval = new HourInterval(1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _builder.AtMinute(minute));
        Assert.Equal("minute", exception.ParamName);
    }
}
