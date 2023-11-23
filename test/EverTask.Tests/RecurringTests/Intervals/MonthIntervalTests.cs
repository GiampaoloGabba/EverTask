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
}
