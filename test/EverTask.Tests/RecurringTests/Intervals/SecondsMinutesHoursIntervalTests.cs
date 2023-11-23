using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Intervals;

public class SecondsMinutesHoursIntervalTests
{
    [Fact]
    public void Seconds_Constructor()
    {
        // Arrange
        int expectedInterval = 10;

        // Act
        var interval = new SecondInterval(expectedInterval);

        // Assert
        Assert.Equal(expectedInterval, interval.Interval);
    }

    [Theory]
    [InlineData(10, 2023, 11, 22, 12, 30, 0)] // Aggiunge 10 secondi
    [InlineData(60, 2023, 11, 22, 12, 30, 0)] // Aggiunge 60 secondi (1 minuto)
    public void Seconds_GetNextOccurrence(int seconds, int year, int month, int day, int hour, int minute, int second)
    {
        // Arrange
        var interval = new SecondInterval(seconds);
        var current  = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        var expected = current.AddSeconds(seconds);

        // Act
        var next = interval.GetNextOccurrence(current);

        // Assert
        Assert.Equal(expected, next);
    }

    [Fact]
    public void Minute_Constructor()
    {
        int expectedInterval = 15;
        var interval         = new MinuteInterval(expectedInterval);

        Assert.Equal(expectedInterval, interval.Interval);
    }

    [Theory]
    [InlineData(15, 0, 2023, 11, 22, 12, 30, 0)]  // Aggiunge 15 minuti, mantiene secondi a 0
    [InlineData(30, 10, 2023, 11, 22, 12, 30, 0)] // Aggiunge 30 minuti, imposta secondi a 10
    public void Minute_GetNextOccurrence(int minutes, int onSecond, int year, int month, int day, int hour, int minute, int second)
    {
        var interval = new MinuteInterval(minutes) { OnSecond = onSecond };
        var current  = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        var expected = current.AddMinutes(minutes).Adjust(second: onSecond);

        var next = interval.GetNextOccurrence(current);

        Assert.Equal(expected, next);
    }

    [Fact]
    public void Hour_Constructor()
    {
        int expectedInterval = 2;
        var interval         = new HourInterval(expectedInterval);

        Assert.Equal(expectedInterval, interval.Interval);
    }

    [Fact]
    public void Hour_Constructor_SetsIntervalAndOnHours()
    {
        int expectedInterval = 2;
        var expectedOnHours  = new[] { 10, 12, 14 };
        var interval         = new HourInterval(expectedInterval, expectedOnHours);

        Assert.Equal(expectedInterval, interval.Interval);
        Assert.Equal(expectedOnHours, interval.OnHours);
    }

    [Theory]
    [InlineData(1, 0, 0, 2023, 11, 22, 12, 30, 0)]       // Aggiunge 1 ora
    [InlineData(1, null, null, 2023, 11, 22, 12, 30, 0)] // Aggiunge 1 ora
    [InlineData(3, 15, 10, 2023, 11, 22, 12, 30, 0)]     // Aggiunge 3 ore, imposta minuti a 15 e secondi a 10
    public void Hour_GetNextOccurrence(int hours, int? onMinute, int? onSecond, int year, int month, int day, int hour, int minute, int second)
    {
        var interval = new HourInterval(hours) { OnMinute = onMinute, OnSecond = onSecond };
        var current  = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        var expected = current.AddHours(hours).Adjust(minute: onMinute, second: onSecond);

        var next = interval.GetNextOccurrence(current);

        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData(0, new[] { 14, 15, 16 }, null, null, 2023, 11, 22, 12, 30, 0, 14, 30, 0)] // Aggiunge 1 ora, verifica l'ora successiva valida in OnHours
    [InlineData(0, new[] { 10, 20 }, 45, 0, 2023, 11, 22, 19, 30, 0, 20, 45, 0)]     // Aggiunge 2 ore, verifica OnHours e OnMinute
    public void Hour_GetNextOccurrence_WithOnHours(int hours, int[] onHours, int? onMinute, int? onSecond, int year, int month, int day, int hour, int minute, int second, int expectedHour, int expectedMinute, int expectedSecond)
    {
        var interval = new HourInterval(hours) { OnHours = onHours, OnMinute = onMinute, OnSecond = onSecond };
        var current = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        var expected = new DateTimeOffset(year, month, day, expectedHour, expectedMinute, expectedSecond, TimeSpan.Zero);

        var next = interval.GetNextOccurrence(current);

        Assert.Equal(expected, next);
    }
}
