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
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 13, 0, 0, 0, TimeSpan.Zero)); // Next Monday at midnight (default)
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
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 20, 0, 0, 0, TimeSpan.Zero)); // 2 weeks later at midnight (default)
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
        next.Value.Hour.ShouldBe(0); // Midnight (default)
        next.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_multiple_days()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        var current = new DateTimeOffset(2025, 11, 5, 15, 0, 0, TimeSpan.Zero); // Wednesday 15:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Wednesday); // Next week Wednesday (first valid day in next week's cycle)
        next.Value.Hour.ShouldBe(0); // Midnight (default)
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
    public void Should_use_default_midnight_when_no_time_specified()
    {
        // Arrange
        var interval = new WeekInterval(1);
        var current = new DateTimeOffset(2025, 1, 6, 14, 25, 37, TimeSpan.Zero); // Monday 14:25:37

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.Hour.ShouldBe(0);
        next.Value.Minute.ShouldBe(0);
        next.Value.Second.ShouldBe(0);
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
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 29, 0, 0, 0, TimeSpan.Zero)); // 3 weeks later at midnight (default)
    }

    [Fact]
    public void Should_use_default_midnight_time_when_OnTimes_not_set()
    {
        // Arrange
        var interval = new WeekInterval(1);
        var current = new DateTimeOffset(2025, 1, 6, 14, 30, 0, TimeSpan.Zero); // Monday 14:30

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.Hour.ShouldBe(0);
        next.Value.Minute.ShouldBe(0);
        next.Value.Second.ShouldBe(0);
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_single_time()
    {
        // Arrange
        var interval = new WeekInterval(1);
        interval.OnTimes = [new TimeOnly(9, 30)];
        var current = new DateTimeOffset(2025, 1, 6, 8, 0, 0, TimeSpan.Zero); // Monday 8:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        next.ShouldNotBeNull();
        next.Value.Hour.ShouldBe(9);
        next.Value.Minute.ShouldBe(30);
        next.Value.Second.ShouldBe(0);
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_multiple_times()
    {
        // Arrange
        var interval = new WeekInterval(1);
        interval.OnTimes = [new TimeOnly(9, 0), new TimeOnly(15, 0)];
        var current = new DateTimeOffset(2025, 1, 6, 10, 0, 0, TimeSpan.Zero); // Monday 10:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert - Should return Monday at 15:00 (next available time)
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        next.Value.Hour.ShouldBe(15);
        next.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void Should_move_to_next_week_when_all_times_passed()
    {
        // Arrange
        var interval = new WeekInterval(1);
        interval.OnTimes = [new TimeOnly(9, 0), new TimeOnly(15, 0)];
        var current = new DateTimeOffset(2025, 1, 6, 16, 0, 0, TimeSpan.Zero); // Monday 16:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert - Should return next Monday at 9:00
        next.ShouldNotBeNull();
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 13, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_specific_day_and_time()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Friday]);
        interval.OnTimes = [new TimeOnly(14, 30)];
        var current = new DateTimeOffset(2025, 1, 6, 10, 0, 0, TimeSpan.Zero); // Monday 10:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert - Should return Friday at 14:30
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Friday);
        next.Value.Hour.ShouldBe(14);
        next.Value.Minute.ShouldBe(30);
    }

    [Fact]
    public void Should_calculate_next_occurrence_with_multiple_days_and_multiple_times()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        interval.OnTimes = [new TimeOnly(9, 0), new TimeOnly(15, 0)];
        var current = new DateTimeOffset(2025, 1, 6, 10, 0, 0, TimeSpan.Zero); // Monday 10:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert - Should return Monday at 15:00 (next available time on same day)
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        next.Value.Hour.ShouldBe(15);
        next.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void Should_move_to_next_week_when_all_times_passed_on_current_day()
    {
        // Arrange
        var interval = new WeekInterval(1, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]);
        interval.OnTimes = [new TimeOnly(9, 0), new TimeOnly(15, 0)];
        var current = new DateTimeOffset(2025, 1, 6, 16, 0, 0, TimeSpan.Zero); // Monday 16:00

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert - Should return next Monday at 9:00 (every week schedule)
        next.ShouldNotBeNull();
        next.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        next.Value.ShouldBe(new DateTimeOffset(2025, 1, 13, 9, 0, 0, TimeSpan.Zero)); // Next Monday 9:00
    }

    [Fact]
    public void OnTimes_should_sort_automatically()
    {
        // Arrange
        var interval = new WeekInterval(1);
        var time1 = new TimeOnly(15, 0);
        var time2 = new TimeOnly(9, 0);
        var time3 = new TimeOnly(12, 0);

        // Act
        interval.OnTimes = [time1, time2, time3];

        // Assert
        interval.OnTimes.Length.ShouldBe(3);
        interval.OnTimes[0].ShouldBe(time2); // 9:00
        interval.OnTimes[1].ShouldBe(time3); // 12:00
        interval.OnTimes[2].ShouldBe(time1); // 15:00
    }
}
