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
}
