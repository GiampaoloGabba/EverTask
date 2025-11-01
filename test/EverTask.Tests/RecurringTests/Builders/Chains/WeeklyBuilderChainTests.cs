using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.Builders.Chains;

public class WeeklyBuilderChainTests
{
    [Fact]
    public void Should_chain_everyweek_with_runat()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var runAt = new DateTimeOffset(2030, 1, 13, 10, 0, 0, TimeSpan.Zero); // Monday

        // Act
        builder.Schedule().EveryWeek().RunUntil(runAt);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.RunUntil.ShouldBe(runAt.ToUniversalTime());
    }

    [Fact]
    public void Should_chain_everyweek_with_maxruns()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().EveryWeek().MaxRuns(5);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.MaxRuns.ShouldBe(5);
    }

    [Fact]
    public void Should_chain_everyweek_ondays_with_rununtil()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var runUntil = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero);

        // Act
        builder.Schedule().EveryWeek()
               .OnDays(DayOfWeek.Monday, DayOfWeek.Friday)
               .RunUntil(runUntil);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.RunUntil.ShouldBe(runUntil.ToUniversalTime());
    }

    [Fact]
    public void Should_chain_every_n_weeks_ondays()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.Schedule().Every(2).Weeks()
            .OnDays(DayOfWeek.Wednesday);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.Interval.ShouldBe(2);
        task.WeekInterval.OnDays.ShouldBe([DayOfWeek.Wednesday]);
    }

    [Fact]
    public void Should_chain_runnow_then_everyweek()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();

        // Act
        builder.RunNow().Then().EveryWeek().MaxRuns(10);

        // Assert
        var task = builder.RecurringTask;
        task.RunNow.ShouldBeTrue();
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.Interval.ShouldBe(1);
        task.MaxRuns.ShouldBe(10);
    }

    [Fact]
    public void Should_chain_runat_then_everyweek()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var runAt = new DateTimeOffset(2025, 1, 13, 14, 0, 0, TimeSpan.Zero);

        // Act
        builder.RunAt(runAt).Then().EveryWeek().OnDay(DayOfWeek.Monday).MaxRuns(5);

        // Assert
        var task = builder.RecurringTask;
        task.SpecificRunTime.ShouldBe(runAt);
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.OnDays.ShouldBe([DayOfWeek.Monday]);
        task.MaxRuns.ShouldBe(5);
    }

    [Fact]
    public void Should_chain_every_n_weeks_with_multiple_constraints()
    {
        // Arrange
        var builder = new RecurringTaskBuilder();
        var runUntil = new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero);
        // Act
        builder.Schedule()
            .Every(2).Weeks()
            .OnDays(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday)
            .RunUntil(runUntil);

        // Assert
        var task = builder.RecurringTask;
        task.WeekInterval.ShouldNotBeNull();
        task.WeekInterval.Interval.ShouldBe(2);
        task.WeekInterval.OnDays.Length.ShouldBe(3);
        task.RunUntil.ShouldBe(runUntil.ToUniversalTime());
    }
}
