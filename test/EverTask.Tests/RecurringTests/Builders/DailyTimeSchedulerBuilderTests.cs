using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Builders;

public class DailyTimeSchedulerBuilderTests
{
    private RecurringTask _task;
    private DailyTimeSchedulerBuilder _builder;

    public DailyTimeSchedulerBuilderTests()
    {
        _task    = new RecurringTask();
        _builder = new DailyTimeSchedulerBuilder(_task);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTime_SetsOnTimesForDayInterval()
    {
        _task.DayInterval = new DayInterval(1);
        var time = new TimeOnly(12, 30);

        _builder.AtTime(time);

        Assert.Contains(time, _task.DayInterval.OnTimes);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTime_SetsOnTimesForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        var time = new TimeOnly(12, 30);

        _builder.AtTime(time);

        Assert.Contains(time, _task.MonthInterval.OnTimes);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTimes_SetsMultipleOnTimesForDayInterval()
    {
        _task.DayInterval = new DayInterval(1);
        var times = new[] { new TimeOnly(8, 0), new TimeOnly(16, 0) };

        _builder.AtTimes(times);

        Assert.Equal(times, _task.DayInterval.OnTimes);
    }

    [Fact]
    public void DailyTimeSchedulerBuilder_AtTimes_SetsMultipleOnTimesForMonthInterval()
    {
        _task.MonthInterval = new MonthInterval(1);
        var times = new[] { new TimeOnly(8, 0), new TimeOnly(16, 0) };

        _builder.AtTimes(times);

        Assert.Equal(times, _task.MonthInterval.OnTimes);
    }

    [Fact]
    public void Should_keep_the_whole_TimeOnly_when_AtTime_stores_it()
    {
        // T12: the builder stores the value VERBATIM. It used to pass every time through
        // TimeOnly.ToUniversalTime(), which converted nothing — it rebuilt the value from today's UTC date,
        // whose offset is zero — and rounded it down to the second on the way through. Asserting against that
        // same helper, as these tests once did, could never see the difference.
        _task.DayInterval = new DayInterval(1);
        var time = new TimeOnly(12, 30, 15, 250);

        _builder.AtTime(time);

        _task.DayInterval.OnTimes.ShouldHaveSingleItem().ShouldBe(time);
        _task.DayInterval.OnTimes[0].Millisecond.ShouldBe(250);
    }

    [Fact]
    public void Should_keep_two_sub_second_times_apart_when_AtTimes_stores_them()
    {
        // The same rounding used to make these two the SAME time, and Distinct() then dropped one of them:
        // a schedule asking for two slots quietly became a schedule with one.
        _task.DayInterval = new DayInterval(1);
        var times = new[] { new TimeOnly(6, 0, 0, 100), new TimeOnly(6, 0, 0, 900) };

        _builder.AtTimes(times);

        _task.DayInterval.OnTimes.ShouldBe(times);
    }

    [Fact]
    public void Should_land_on_the_sub_second_slot_the_builder_stored()
    {
        // Storing the precision is only half of it: the grid has to be able to reach the slot, or the verbatim
        // value would be a promise the schedule does not keep.
        _task.DayInterval = new DayInterval(1);
        _builder.AtTime(new TimeOnly(6, 0, 0, 250));

        var next = _task.CalculateNextRun(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero), 1)
                        .ShouldNotBeNull();

        next.ShouldBe(new DateTimeOffset(2026, 5, 11, 6, 0, 0, 250, TimeSpan.Zero));
    }

}
