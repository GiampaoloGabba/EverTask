using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders;

public class WeeklySchedulerBuilderTests
{
    [Fact]
    public void EveryWeek_should_set_week_interval()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().EveryWeek();

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.Interval.ShouldBe(1);
    }

    [Fact]
    public void Every_n_weeks_should_set_week_interval()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().Every(3).Weeks();

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.Interval.ShouldBe(3);
    }

    [Fact]
    public void OnDay_should_set_single_day()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().EveryWeek().OnDay(DayOfWeek.Friday);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnDays.ShouldBe([DayOfWeek.Friday]);
    }

    [Fact]
    public void OnDays_should_set_multiple_days()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().EveryWeek()
            .OnDays(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday) ;

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnDays.ShouldContain(DayOfWeek.Monday);
        task.WeekInterval.OnDays.ShouldContain(DayOfWeek.Wednesday);
        task.WeekInterval.OnDays.ShouldContain(DayOfWeek.Friday);
    }

    [Fact]
    public void OnDays_should_remove_duplicates()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().EveryWeek()
            .OnDays(DayOfWeek.Monday, DayOfWeek.Monday, DayOfWeek.Friday);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnDays.Length.ShouldBe(2);
    }

    [Fact]
    public void OnDay_should_throw_if_week_interval_not_set()
    {
        // Arrange
        var task = new RecurringTask();
        var builder = new WeeklySchedulerBuilder(task);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.OnDay(DayOfWeek.Monday))
            .Message.ShouldContain("WeekInterval must be set");
    }

    [Fact]
    public void OnDays_should_throw_if_week_interval_not_set()
    {
        // Arrange
        var task = new RecurringTask();
        var builder = new WeeklySchedulerBuilder(task);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.OnDays(DayOfWeek.Monday))
            .Message.ShouldContain("WeekInterval must be set");
    }

    [Fact]
    public void OnDay_should_return_daily_time_scheduler_builder()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        var result = builder.Schedule().EveryWeek().OnDay(DayOfWeek.Monday);

        // Assert
        result.ShouldBeOfType<DailyTimeSchedulerBuilder>();
    }

    [Fact]
    public void OnDays_should_return_daily_time_scheduler_builder()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        var result = builder.Schedule().EveryWeek().OnDays(DayOfWeek.Monday, DayOfWeek.Friday);

        // Assert
        result.ShouldBeOfType<DailyTimeSchedulerBuilder>();
    }

    [Fact]
    public void OnDay_with_AtTime_should_set_single_time()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var time = new TimeOnly(9, 30);

        // Act
        builder.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(time);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnTimes.Length.ShouldBe(1);
        task.WeekInterval.OnTimes[0].ShouldBe(time.ToUniversalTime());
    }

    [Fact]
    public void OnDay_with_AtTimes_should_set_multiple_times()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var time1 = new TimeOnly(9, 0);
        var time2 = new TimeOnly(15, 0);

        // Act
        builder.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTimes(time1, time2);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnTimes.Length.ShouldBe(2);
        task.WeekInterval.OnTimes.ShouldContain(time1.ToUniversalTime());
        task.WeekInterval.OnTimes.ShouldContain(time2.ToUniversalTime());
    }

    [Fact]
    public void OnDays_with_AtTime_should_set_single_time()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var time = new TimeOnly(14, 30);

        // Act
        builder.Schedule().EveryWeek()
            .OnDays(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday)
            .AtTime(time);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnDays.Length.ShouldBe(3);
        task.WeekInterval.OnTimes.Length.ShouldBe(1);
        task.WeekInterval.OnTimes[0].ShouldBe(time.ToUniversalTime());
    }

    [Fact]
    public void OnDays_with_AtTimes_should_set_multiple_times()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var time1 = new TimeOnly(8, 0);
        var time2 = new TimeOnly(12, 0);
        var time3 = new TimeOnly(16, 0);

        // Act
        builder.Schedule().EveryWeek()
            .OnDays(DayOfWeek.Tuesday, DayOfWeek.Thursday)
            .AtTimes(time1, time2, time3);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnDays.Length.ShouldBe(2);
        task.WeekInterval.OnTimes.Length.ShouldBe(3);
        task.WeekInterval.OnTimes.ShouldContain(time1.ToUniversalTime());
        task.WeekInterval.OnTimes.ShouldContain(time2.ToUniversalTime());
        task.WeekInterval.OnTimes.ShouldContain(time3.ToUniversalTime());
    }

    [Fact]
    public void AtTimes_should_remove_duplicate_times()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var time = new TimeOnly(10, 0);

        // Act
        builder.Schedule().EveryWeek()
            .OnDay(DayOfWeek.Wednesday)
            .AtTimes(time, time, new TimeOnly(14, 0));

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnTimes.Length.ShouldBe(2);
    }

    [Fact]
    public void AtTimes_should_sort_times_automatically()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var time1 = new TimeOnly(15, 0);
        var time2 = new TimeOnly(9, 0);
        var time3 = new TimeOnly(12, 0);

        // Act
        builder.Schedule().EveryWeek()
            .OnDay(DayOfWeek.Friday)
            .AtTimes(time1, time2, time3);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnTimes.Length.ShouldBe(3);
        task.WeekInterval.OnTimes[0].ShouldBe(time2.ToUniversalTime()); // 9:00
        task.WeekInterval.OnTimes[1].ShouldBe(time3.ToUniversalTime()); // 12:00
        task.WeekInterval.OnTimes[2].ShouldBe(time1.ToUniversalTime()); // 15:00
    }
}
