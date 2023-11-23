using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders;

public class RecurringTaskBuilderTests
{
    private RecurringTaskBuilder _builder;

    public RecurringTaskBuilderTests()
    {
        _builder = new RecurringTaskBuilder();
    }

    [Fact]
    public void RecurringTaskBuilder_RunNow_SetsRunNow()
    {
        _builder.RunNow();
        Assert.True(_builder.RecurringTask.RunNow);
    }

    [Fact]
    public void Should_set_RunNow_to_true_and_SpecificRunTime_to_current_time()
    {
        _builder.RunNow();

        Assert.True(_builder.RecurringTask.RunNow);

        var currentTime     = DateTimeOffset.UtcNow;
        var specificRunTime = _builder.RecurringTask.SpecificRunTime!.Value;

        var roundedCurrentTime     = currentTime.AddTicks(-currentTime.Ticks);
        var roundedSpecificRunTime = specificRunTime.AddTicks(-specificRunTime.Ticks);

        roundedSpecificRunTime.ShouldBe(roundedCurrentTime);
    }

    [Fact]
    public void RecurringTaskBuilder_RunDelayed_SetsInitialDelay()
    {
        var delay = TimeSpan.FromMinutes(10);
        _builder.RunDelayed(delay);
        Assert.Equal(delay, _builder.RecurringTask.InitialDelay);
    }


    [Fact]
    public void RecurringTaskBuilder_RunAt_SetsSpecificRunTime()
    {
        var runTime = DateTimeOffset.Now.AddDays(1);
        _builder.RunAt(runTime);
        Assert.Equal(runTime, _builder.RecurringTask.SpecificRunTime);
    }

    [Fact]
    public void RecurringTaskBuilder_Schedule_ReturnsIntervalSchedulerBuilder()
    {
        var returnedBuilder = _builder.Schedule();
        Assert.IsType<IntervalSchedulerBuilder>(returnedBuilder);
    }
}
