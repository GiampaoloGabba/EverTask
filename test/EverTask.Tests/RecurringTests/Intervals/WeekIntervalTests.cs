using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Intervals;

public class WeekIntervalTests
{
    [Fact]
    public void Should_calculate_next_occurrence_for_single_week()
    {
        // Arrange
        var interval = new WeekInterval(1);
        var current = new DateTimeOffset(2025, 1, 6, 10, 0, 0, TimeSpan.Zero); // Monday

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 13, 10, 0, 0, TimeSpan.Zero)); // Next Monday
    }

    [Fact]
    public void Should_calculate_next_occurrence_for_multiple_weeks()
    {
        // Arrange
        var interval = new WeekInterval(2);
        var current = new DateTimeOffset(2025, 1, 6, 14, 30, 0, TimeSpan.Zero); // Monday 14:30

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 20, 14, 30, 0, TimeSpan.Zero)); // 2 weeks later, same time
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_specific_day()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Friday]);
        var current = new DateTimeOffset(2025, 1, 6, 10, 0, 0, TimeSpan.Zero); // Monday

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Friday);
        next.Value.Hour.ShouldBe(10); // Mantiene l'ora
        next.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_multiple_days()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        var current = new DateTimeOffset(2025, 11, 5, 15, 0, 0, TimeSpan.Zero); // Monday 15:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Wednesday); // Next valid day
        next.Value.Hour.ShouldBe(15);
    }

    [Fact]
    public void Should_throw_when_interval_is_zero_and_no_days_specified()
    {
        // Arrange
        var interval = new WeekInterval(0);
        var current = DateTimeOffset.UtcNow;

        // Act & Assert
        Should.Throw<ArgumentException>(() => interval.GetNextOccurrence(current))
            .Message.ShouldContain("Invalid Week Interval");
    }

    [Fact]
    public void Should_preserve_time_components()
    {
        // Arrange
        var interval = new WeekInterval(1);
        var current = new DateTimeOffset(2025, 1, 6, 14, 25, 37, TimeSpan.Zero); // Monday 14:25:37

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.Hour.ShouldBe(14);
        next.Value.Minute.ShouldBe(25);
        next.Value.Second.ShouldBe(37);
    }

    [Fact]
    public void Should_remove_duplicate_days()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Monday, DayOfWeek.Monday, DayOfWeek.Friday]);

        // Assert
        interval.OnDays.Length.ShouldBe(2);
        interval.OnDays.ShouldContain(DayOfWeek.Monday);
        interval.OnDays.ShouldContain(DayOfWeek.Friday);
    }

    [Fact]
    public void Should_maintain_day_of_week_across_weeks()
    {
        // Arrange
        var interval = new WeekInterval(3);
        var current = new DateTimeOffset(2025, 1, 8, 9, 30, 0, TimeSpan.Zero); // Wednesday

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Wednesday);
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 29, 9, 30, 0, TimeSpan.Zero)); // 3 weeks later
    }
}
