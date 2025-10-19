using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Intervals;

public class DayIntervalTests
{
    [Fact]
    public void Day_Constructor()
    {
        int expectedInterval = 10;
        var interval         = new DayInterval(expectedInterval);

        Assert.Equal(expectedInterval, interval.Interval);
    }

    [Fact]
    public void Day_Constructor_OnDays()
    {
        int expectedInterval = 5;
        var onDays           = new [] { DayOfWeek.Monday, DayOfWeek.Wednesday };
        var interval         = new DayInterval(expectedInterval, onDays);

        Assert.Equal(expectedInterval, interval.Interval);
        Assert.Equal(onDays.Length, interval.OnDays.Length);
    }

    [Theory]
    [InlineData(1, 2023, 11, 22, 12, 30, 0, new[] { "13:00" }, 2023, 11, 23, 13, 0, 0)] // Aggiunge 1 giorno e imposta l'ora a 13:00
    [InlineData(2, 2023, 11, 22, 12, 30, 0, new string[] {}, 2023, 11, 24, 12, 30, 0)]   // Aggiunge 2 giorni, nessun orario specificato, imposta mezzanotte
    public void Day_GetNextOccurrence(int days, int year, int month, int day, int hour, int minute, int second, string[] onTimes, int expectedYear, int expectedMonth, int expectedDay, int expectedHour, int expectedMinute, int expectedSecond)
    {
        var interval = new DayInterval(days) { OnTimes = onTimes.Select(TimeOnly.Parse).ToArray() };
        var current = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        var expected = new DateTimeOffset(expectedYear, expectedMonth, expectedDay, expectedHour, expectedMinute, expectedSecond, TimeSpan.Zero);

        var next = interval.GetNextOccurrence(current);

        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }, 2023, 11, 22)] // Testa un fine settimana successivo
    public void Day_GetNextOccurrence_OnDays(DayOfWeek[] onDays, int year, int month, int day)
    {
        var interval = new DayInterval(1, onDays);
        var current  = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        var next     = interval.GetNextOccurrence(current);

        Assert.Contains(next!.Value.DayOfWeek, onDays);
    }

    [Fact]
    public void Day_Validate_ThrowsArgumentException()
    {
        var interval = new DayInterval(0, Array.Empty<DayOfWeek>());

        var exception = Record.Exception(() => interval.Validate());

        Assert.IsType<ArgumentException>(exception);
    }

    // Priority 2: OnTimes auto-sorting verification

    [Fact]
    public void OnTimes_AutomaticallySortsUnsortedArray()
    {
        var interval = new DayInterval(1);
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
    public void OnTimes_HandlesAlreadySortedArray()
    {
        var interval = new DayInterval(1);
        var sortedTimes = new[]
        {
            TimeOnly.Parse("08:00"),
            TimeOnly.Parse("12:00"),
            TimeOnly.Parse("18:00")
        };

        interval.OnTimes = sortedTimes;

        // Should remain sorted
        Assert.Equal(TimeOnly.Parse("08:00"), interval.OnTimes[0]);
        Assert.Equal(TimeOnly.Parse("12:00"), interval.OnTimes[1]);
        Assert.Equal(TimeOnly.Parse("18:00"), interval.OnTimes[2]);
    }

    [Fact]
    public void OnTimes_SortingWorksWithDirectAssignment()
    {
        // Simulate direct property assignment (not via builder)
        var interval = new DayInterval(1)
        {
            OnTimes = new[]
            {
                TimeOnly.Parse("23:59"),
                TimeOnly.Parse("00:01"),
                TimeOnly.Parse("12:00")
            }
        };

        // Should be auto-sorted
        Assert.Equal(TimeOnly.Parse("00:01"), interval.OnTimes[0]);
        Assert.Equal(TimeOnly.Parse("12:00"), interval.OnTimes[1]);
        Assert.Equal(TimeOnly.Parse("23:59"), interval.OnTimes[2]);
    }

    [Fact]
    public void OnTimes_GetNextOccurrence_UsesPreSortedArray()
    {
        var interval = new DayInterval(1);
        var unsortedTimes = new[]
        {
            TimeOnly.Parse("17:00"),
            TimeOnly.Parse("09:00"),
            TimeOnly.Parse("14:00")
        };

        interval.OnTimes = unsortedTimes; // Will be auto-sorted to 09:00, 14:00, 17:00

        var current = new DateTimeOffset(2023, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var next = interval.GetNextOccurrence(current);

        // Should find 14:00 (next time after 10:00), not 17:00
        Assert.Equal(14, next!.Value.Hour);
        Assert.Equal(0, next.Value.Minute);
    }
}
