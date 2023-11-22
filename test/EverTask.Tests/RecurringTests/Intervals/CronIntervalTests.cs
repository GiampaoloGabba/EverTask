using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests.Intervals;

public class CronIntervalTests
{
    [Fact]
    public void Cron_Constructor()
    {
        var expectedExpression = "*/5 * * * *";
        var interval           = new CronInterval(expectedExpression);

        Assert.Equal(expectedExpression, interval.CronExpression);
    }

    [Theory]
    [InlineData("*/5 * * * *", 2023, 11, 22, 12, 30, 0)] // Ogni 5 minuti
    [InlineData("0 0 * * *", 2023, 11, 22, 23, 59, 0)]   // Ogni giorno a mezzanotte
    public void Cron_GetNextOccurrence(string cronExpression, int year, int month, int day, int hour, int minute, int second)
    {
        var interval = new CronInterval(cronExpression);
        var current  = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);

        var cron     = interval.ParseCronExpression();
        var expected = cron.GetNextOccurrence(current, TimeZoneInfo.Utc)?.ToUniversalTime();

        var next = interval.GetNextOccurrence(current);

        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData("invalid-cron-expression")]
    public void Cron_ParseCronExpression_ThrowsArgumentException(string cronExpression)
    {
        var interval = new CronInterval(cronExpression);

        var exception = Record.Exception(() => interval.ParseCronExpression());

        Assert.IsType<ArgumentException>(exception);
    }
}
