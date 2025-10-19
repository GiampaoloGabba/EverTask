using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Intervals;

public class MonthIntervalTests
{
    [Fact]
    public void Month_Constructor()
    {
        int expectedInterval = 6;
        var interval         = new MonthInterval(expectedInterval);

        Assert.Equal(expectedInterval, interval.Interval);
    }

    [Fact]
    public void Month_Constructor_OnMonths()
    {
        int expectedInterval = 3;
        var onMonths         = new [] { 1, 3, 5 };
        var interval         = new MonthInterval(expectedInterval, onMonths);

        Assert.Equal(expectedInterval, interval.Interval);
        Assert.Equal(onMonths.Length, interval.OnMonths.Length);
    }

    [Theory]
    [InlineData(2, 2023, 1, 15)] // Aggiunge 2 mesi
    [InlineData(4, 2023, 1, 15)] // Aggiunge 4 mesi
    public void Month_GetNextOccurrence(int months, int year, int month, int day)
    {
        var interval = new MonthInterval(months);
        var current  = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var expected = current.AddMonths(months);

        var next = interval.GetNextOccurrence(current);

        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData(new[] { 3, 5, 7 }, 2023, 1, 15)] // Testa i mesi successivi validi
    public void Month_GetNextOccurrence_OnMonths(int[] onMonths, int year, int month, int day)
    {
        var interval = new MonthInterval(1, onMonths);
        var current  = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var next     = interval.GetNextOccurrence(current);

        Assert.Contains(next!.Value.Month, onMonths);
    }

    [Fact]
    public void Month_Validate_ThrowsArgumentException()
    {
        var interval = new MonthInterval(0, Array.Empty<int>());

        var exception = Record.Exception(() => interval.Validate());

        Assert.IsType<ArgumentException>(exception);
    }

    // Priority 2: Explicit tests for OnFirst/OnDay scenarios (bug fix verification)

    [Fact]
    public void Month_GetNextOccurrence_WithOnFirst()
    {
        var interval = new MonthInterval(1) { OnFirst = DayOfWeek.Monday };
        var current  = new DateTimeOffset(2023, 11, 15, 0, 0, 0, TimeSpan.Zero);

        var next = interval.GetNextOccurrence(current);

        // December 2023, first Monday = December 4th
        Assert.Equal(12, next!.Value.Month);
        Assert.Equal(4, next.Value.Day);
        Assert.Equal(DayOfWeek.Monday, next.Value.DayOfWeek);
    }

    [Fact]
    public void Month_GetNextOccurrence_WithOnDay()
    {
        var interval = new MonthInterval(1) { OnDay = 15 };
        var current  = new DateTimeOffset(2023, 11, 10, 0, 0, 0, TimeSpan.Zero);

        var next = interval.GetNextOccurrence(current);

        // December 2023, day 15
        Assert.Equal(12, next!.Value.Month);
        Assert.Equal(15, next.Value.Day);
    }

    [Fact]
    public void Month_GetNextOccurrence_WithOnDay_InvalidDayInMonth()
    {
        var interval = new MonthInterval(1) { OnDay = 31 };
        var current  = new DateTimeOffset(2023, 1, 15, 0, 0, 0, TimeSpan.Zero);

        var next = interval.GetNextOccurrence(current);

        // February 2023 has only 28 days, should adjust to 28
        Assert.Equal(2, next!.Value.Month);
        Assert.Equal(28, next.Value.Day);
    }

    [Fact]
    public void Month_GetNextOccurrence_WithOnDay_LeapYear()
    {
        var interval = new MonthInterval(1) { OnDay = 30 };
        var current  = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

        var next = interval.GetNextOccurrence(current);

        // February 2024 (leap year) has 29 days, should adjust to 29
        Assert.Equal(2, next!.Value.Month);
        Assert.Equal(29, next.Value.Day);
    }

    [Fact]
    public void Month_GetNextOccurrence_WithOnFirst_DifferentWeekdays()
    {
        var current = new DateTimeOffset(2023, 12, 1, 0, 0, 0, TimeSpan.Zero);

        // Test all days of week (January 2024: 1st is Monday)
        var testCases = new[]
        {
            (DayOfWeek.Sunday, 7),    // First Sunday in Jan 2024
            (DayOfWeek.Monday, 1),    // First Monday in Jan 2024
            (DayOfWeek.Tuesday, 2),   // First Tuesday in Jan 2024
            (DayOfWeek.Wednesday, 3), // First Wednesday in Jan 2024
            (DayOfWeek.Thursday, 4),  // First Thursday in Jan 2024
            (DayOfWeek.Friday, 5),    // First Friday in Jan 2024
            (DayOfWeek.Saturday, 6)   // First Saturday in Jan 2024
        };

        foreach (var (dayOfWeek, expectedDay) in testCases)
        {
            var interval = new MonthInterval(1) { OnFirst = dayOfWeek };
            var next     = interval.GetNextOccurrence(current);

            Assert.Equal(2024, next!.Value.Year);
            Assert.Equal(1, next.Value.Month);
            Assert.Equal(expectedDay, next.Value.Day);
            Assert.Equal(dayOfWeek, next.Value.DayOfWeek);
        }
    }

    // Priority 2: OnTimes auto-sorting verification (same as DayInterval)

    [Fact]
    public void OnTimes_AutomaticallySortsUnsortedArray()
    {
        var interval = new MonthInterval(1);
        var unsortedTimes = new[]
        {
            TimeOnly.Parse("17:00"),
            TimeOnly.Parse("09:00"),
            TimeOnly.Parse("12:00")
        };

        interval.OnTimes = unsortedTimes;

        // Verify sorted
        Assert.Equal(TimeOnly.Parse("09:00"), interval.OnTimes[0]);
        Assert.Equal(TimeOnly.Parse("12:00"), interval.OnTimes[1]);
        Assert.Equal(TimeOnly.Parse("17:00"), interval.OnTimes[2]);
    }

    [Fact]
    public void OnTimes_GetNextOccurrence_UsesPreSortedArray()
    {
        var interval = new MonthInterval(1) { OnDay = 15 };
        var unsortedTimes = new[]
        {
            TimeOnly.Parse("17:00"),
            TimeOnly.Parse("09:00"),
            TimeOnly.Parse("14:00")
        };

        interval.OnTimes = unsortedTimes; // Will be auto-sorted to 09:00, 14:00, 17:00

        var current = new DateTimeOffset(2023, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var next = interval.GetNextOccurrence(current);

        // Should be December 15th at 14:00 (first time > current time 10:00)
        // GetNextRequestedTime compares against current time, finding first time > 10:00
        Assert.Equal(12, next!.Value.Month);
        Assert.Equal(15, next.Value.Day);
        Assert.Equal(14, next.Value.Hour);
        Assert.Equal(0, next.Value.Minute);
    }
}
