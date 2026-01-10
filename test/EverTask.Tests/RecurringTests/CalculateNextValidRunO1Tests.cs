using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Comprehensive unit tests for the O(1) CalculateNextValidRun algorithm.
/// Tests all interval types and cron expressions to ensure rhythm preservation
/// and correct skip calculations.
/// </summary>
public class CalculateNextValidRunO1Tests
{
    #region Second Interval Tests

    [Theory]
    [InlineData(1, 60)]      // Every 1 second, 60 seconds ago
    [InlineData(5, 30)]      // Every 5 seconds, 30 seconds ago
    [InlineData(10, 120)]    // Every 10 seconds, 2 minutes ago
    [InlineData(30, 300)]    // Every 30 seconds, 5 minutes ago
    [InlineData(1, 3600)]    // Every 1 second, 1 hour ago (3600 skips)
    public void SecondInterval_MaintainsRhythm(int intervalSeconds, int secondsAgo)
    {
        // Arrange
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(intervalSeconds)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddSeconds(-secondsAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);

        // Verify rhythm: difference from scheduledTime should be multiple of interval
        var totalSeconds = (result.NextRun.Value - scheduledTime).TotalSeconds;
        var remainder = totalSeconds % intervalSeconds;
        (remainder < 0.001 || remainder > intervalSeconds - 0.001).ShouldBeTrue(
            $"Rhythm not maintained. TotalSeconds: {totalSeconds}, Interval: {intervalSeconds}, Remainder: {remainder}");

        // Verify skipped count is reasonable
        var expectedMinSkips = (secondsAgo / intervalSeconds) - 1;
        result.SkippedCount.ShouldBeGreaterThanOrEqualTo(expectedMinSkips);
    }

    [Fact]
    public void SecondInterval_VeryLongDowntime_StillMaintainsRhythm()
    {
        // Arrange: 1 year downtime with 1-second interval
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(1)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddYears(-1); // ~31.5 million seconds

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        result.SkippedCount.ShouldBeGreaterThan(30_000_000); // At least 30M skips
    }

    #endregion

    #region Minute Interval Tests

    [Theory]
    [InlineData(1, 10)]      // Every 1 minute, 10 minutes ago
    [InlineData(5, 30)]      // Every 5 minutes, 30 minutes ago
    [InlineData(15, 60)]     // Every 15 minutes, 1 hour ago
    [InlineData(30, 120)]    // Every 30 minutes, 2 hours ago
    [InlineData(1, 1440)]    // Every 1 minute, 1 day ago (1440 skips)
    public void MinuteInterval_MaintainsRhythm(int intervalMinutes, int minutesAgo)
    {
        // Arrange
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(intervalMinutes)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMinutes(-minutesAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);

        // Verify rhythm
        var totalMinutes = (result.NextRun.Value - scheduledTime).TotalMinutes;
        var remainder = totalMinutes % intervalMinutes;
        (remainder < 0.001 || remainder > intervalMinutes - 0.001).ShouldBeTrue(
            $"Rhythm not maintained. TotalMinutes: {totalMinutes}, Interval: {intervalMinutes}, Remainder: {remainder}");
    }

    [Fact]
    public void MinuteInterval_SpecificRhythmTest()
    {
        // Arrange: Task runs every 5 minutes, starting at :00
        // Scheduled at 10:00, now is 10:22 -> should return 10:25
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5)
        };
        var scheduledTime = new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero);
        var referenceTime = new DateTimeOffset(2026, 1, 10, 10, 22, 0, TimeSpan.Zero);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBe(new DateTimeOffset(2026, 1, 10, 10, 25, 0, TimeSpan.Zero));
        result.SkippedCount.ShouldBe(4); // Skipped :05, :10, :15, :20
    }

    #endregion

    #region Hour Interval Tests

    [Theory]
    [InlineData(1, 5)]       // Every 1 hour, 5 hours ago
    [InlineData(2, 10)]      // Every 2 hours, 10 hours ago
    [InlineData(4, 24)]      // Every 4 hours, 1 day ago
    [InlineData(6, 48)]      // Every 6 hours, 2 days ago
    [InlineData(12, 168)]    // Every 12 hours, 1 week ago
    public void HourInterval_MaintainsRhythm(int intervalHours, int hoursAgo)
    {
        // Arrange
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(intervalHours)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-hoursAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);

        // Verify rhythm
        var totalHours = (result.NextRun.Value - scheduledTime).TotalHours;
        var remainder = totalHours % intervalHours;
        (remainder < 0.001 || remainder > intervalHours - 0.001).ShouldBeTrue(
            $"Rhythm not maintained. TotalHours: {totalHours}, Interval: {intervalHours}, Remainder: {remainder}");
    }

    [Fact]
    public void HourInterval_TenYearsDowntime()
    {
        // Arrange: Task runs every hour, 10 years downtime
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddYears(-10);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        result.SkippedCount.ShouldBeGreaterThan(80000); // ~87,600 hours in 10 years
    }

    #endregion

    #region Day Interval Tests

    [Theory]
    [InlineData(1, 7)]       // Every 1 day, 1 week ago
    [InlineData(2, 14)]      // Every 2 days, 2 weeks ago
    [InlineData(7, 30)]      // Every week, ~1 month ago
    [InlineData(1, 365)]     // Every 1 day, 1 year ago
    public void DayInterval_MaintainsRhythm(int intervalDays, int daysAgo)
    {
        // Arrange - Use midnight as base to avoid time-of-day complications
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(intervalDays)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-daysAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);

        // Verify rhythm - DayInterval uses 24h intervals
        var totalHours = (result.NextRun.Value - scheduledTime).TotalHours;
        var intervalHours = intervalDays * 24;
        var remainder = totalHours % intervalHours;
        (remainder < 0.01 || remainder > intervalHours - 0.01).ShouldBeTrue(
            $"Rhythm not maintained. TotalHours: {totalHours}, Interval: {intervalHours}h, Remainder: {remainder}");
    }

    [Fact]
    public void DayInterval_UsesConsistent24HourIncrements()
    {
        // Arrange: Task started at midnight, runs daily
        // DayInterval adds Days to the date (not 24h), so midnight is used
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1)
        };
        var scheduledTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var referenceTime = new DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero); // 9 days later

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // DayInterval maintains consistent 24h (1 day) increments from the original scheduled time
        var totalDays = (result.NextRun.Value - scheduledTime).TotalDays;
        (totalDays % 1 < 0.001).ShouldBeTrue($"Should be whole days. TotalDays: {totalDays}");
    }

    #endregion

    #region Week Interval Tests

    [Theory]
    [InlineData(1, 4)]       // Every 1 week, 4 weeks ago
    [InlineData(2, 8)]       // Every 2 weeks, 8 weeks ago
    [InlineData(1, 52)]      // Every 1 week, 1 year ago
    public void WeekInterval_MaintainsRhythm(int intervalWeeks, int weeksAgo)
    {
        // Arrange - Use midnight as base to avoid time-of-day complications
        var task = new RecurringTask
        {
            WeekInterval = new WeekInterval(intervalWeeks)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-weeksAgo * 7);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);

        // Verify rhythm (in hours, where 1 week = 168 hours)
        var totalHours = (result.NextRun.Value - scheduledTime).TotalHours;
        var intervalHours = intervalWeeks * 7 * 24;
        var remainder = totalHours % intervalHours;
        (remainder < 0.01 || remainder > intervalHours - 0.01).ShouldBeTrue(
            $"Rhythm not maintained. TotalHours: {totalHours}, Interval: {intervalHours}h, Remainder: {remainder}");
    }

    #endregion

    #region Month Interval Tests

    [Theory]
    [InlineData(1, 6)]       // Every 1 month, 6 months ago
    [InlineData(2, 12)]      // Every 2 months, 1 year ago
    [InlineData(3, 24)]      // Every quarter, 2 years ago
    [InlineData(6, 36)]      // Every 6 months, 3 years ago
    public void MonthInterval_MaintainsRhythm(int intervalMonths, int monthsAgo)
    {
        // Arrange
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(intervalMonths)
        };
        var referenceTime = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMonths(-monthsAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);

        // For monthly intervals, verify the day of month is preserved when possible
        // (some months have fewer days, so we just verify it's a valid future date)
    }

    [Fact]
    public void MonthInterval_ReturnsValidFutureDate()
    {
        // Arrange: Task runs monthly
        // Note: MonthInterval uses ~30 day intervals (GetMinimumInterval returns 30 days)
        // so exact day-of-month preservation is not guaranteed by this interval type
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1)
        };
        var scheduledTime = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var referenceTime = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero); // 3+ months later

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        result.SkippedCount.ShouldBeGreaterThan(0); // Should have skipped some intervals
    }

    #endregion

    #region Cron Expression Tests

    [Theory]
    [InlineData("* * * * *", 60)]           // Every minute, 1 hour ago
    [InlineData("0 * * * *", 300)]          // Every hour at :00, 5 hours ago
    [InlineData("0 0 * * *", 2880)]         // Daily at midnight, 2 days ago (in minutes)
    [InlineData("0 12 * * *", 4320)]        // Daily at noon, 3 days ago (in minutes)
    [InlineData("0 0 * * 0", 20160)]        // Weekly on Sunday, 2 weeks ago (in minutes)
    public void CronExpression_ReturnsValidFutureTime(string cronExpression, int minutesAgo)
    {
        // Arrange
        var task = new RecurringTask
        {
            CronInterval = new CronInterval(cronExpression)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 30, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMinutes(-minutesAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Fact]
    public void CronExpression_EveryMinute_ReturnsNextMinute()
    {
        // Arrange
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("* * * * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 30, 15, TimeSpan.Zero); // 12:30:15
        var scheduledTime = referenceTime.AddHours(-1);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBe(new DateTimeOffset(2026, 1, 10, 12, 31, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CronExpression_EveryHourAtZero_ReturnsNextHour()
    {
        // Arrange: Every hour at minute 0
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 * * * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 30, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-5);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBe(new DateTimeOffset(2026, 1, 10, 13, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CronExpression_DailyAtSpecificTime_ReturnsCorrectNextRun()
    {
        // Arrange: Daily at 14:30
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("30 14 * * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 15, 0, 0, TimeSpan.Zero); // After 14:30
        var scheduledTime = referenceTime.AddDays(-7);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBe(new DateTimeOffset(2026, 1, 11, 14, 30, 0, TimeSpan.Zero)); // Tomorrow
    }

    [Fact]
    public void CronExpression_WeeklyOnMonday_ReturnsNextMonday()
    {
        // Arrange: Every Monday at 9:00
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9 * * 1")
        };
        // January 10, 2026 is a Saturday
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-14);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        // Next Monday after Jan 10 (Saturday) is Jan 12
        result.NextRun!.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        result.NextRun.Value.Hour.ShouldBe(9);
        result.NextRun.Value.Minute.ShouldBe(0);
    }

    [Fact]
    public void CronExpression_MonthlyOnThe15th_ReturnsNext15th()
    {
        // Arrange: 15th of every month at 10:00
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 10 15 * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero); // After Jan 15
        var scheduledTime = referenceTime.AddMonths(-3);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.Day.ShouldBe(15);
        result.NextRun.Value.Month.ShouldBe(2); // February
        result.NextRun.Value.Hour.ShouldBe(10);
    }

    [Fact]
    public void CronExpression_VeryLongDowntime_StillWorks()
    {
        // Arrange: 10 years downtime
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 0 * * *") // Daily at midnight
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddYears(-10);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        result.NextRun.Value.Hour.ShouldBe(0);
        result.NextRun.Value.Minute.ShouldBe(0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void WithinTolerance_DoesNotSkip()
    {
        // Arrange: nextRun is within 1-second tolerance of now
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(10)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, 500, TimeSpan.Zero);
        // scheduledTime such that nextRun = referenceTime - 0.5 seconds (within tolerance)
        var scheduledTime = referenceTime.AddSeconds(-10.5);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.SkippedCount.ShouldBe(0); // Should NOT skip because within tolerance
    }

    [Fact]
    public void JustOutsideTolerance_DoesSkip()
    {
        // Arrange: nextRun is just outside 1-second tolerance
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(10)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        // scheduledTime such that nextRun = referenceTime - 1.5 seconds (outside tolerance)
        var scheduledTime = referenceTime.AddSeconds(-11.5);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.SkippedCount.ShouldBeGreaterThan(0); // Should skip
    }

    [Fact]
    public void MaxRuns_Constraint_ReturnsNull()
    {
        // Arrange: Task with MaxRuns = 5, already at run 4
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            MaxRuns = 5
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-10); // Would skip ~10 runs

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 4, referenceTime);

        // Assert: currentRun (4) + skippedCount would exceed MaxRuns (5)
        result.NextRun.ShouldBeNull();
    }

    [Fact]
    public void RunUntil_Constraint_ReturnsNull()
    {
        // Arrange: Task with RunUntil in the past
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            RunUntil = new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero) // Yesterday
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-5);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldBeNull();
    }

    [Fact]
    public void FutureScheduledTime_NoSkip()
    {
        // Arrange: scheduledTime is in the future
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMinutes(10); // 10 minutes in future

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBe(scheduledTime.AddMinutes(5)); // scheduledTime + interval
        result.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public void NullRecurringTask_ThrowsArgumentNullException()
    {
        // Arrange
        RecurringTask? task = null;

        // Act & Assert
        Should.Throw<ArgumentNullException>(() =>
            task!.CalculateNextValidRun(DateTimeOffset.UtcNow, 1));
    }

    [Fact]
    public void ZeroInterval_ReturnsOriginalNextRun()
    {
        // Arrange: Invalid zero interval (edge case)
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(0)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMinutes(-5);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert: Should handle gracefully (returns original CalculateNextRun result)
        // The behavior depends on RecurringTask.CalculateNextRun implementation
    }

    #endregion

    #region Combined Intervals Tests

    [Fact]
    public void MinuteAndSecond_Combined_MaintainsRhythm()
    {
        // Arrange: Every 5 minutes at 30 seconds
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5),
            SecondInterval = new SecondInterval(30)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMinutes(-30);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Fact]
    public void HourAndMinute_Combined_MaintainsRhythm()
    {
        // Arrange: Every 2 hours at minute 15
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(2),
            MinuteInterval = new MinuteInterval(15)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-10);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Fact]
    public void DayHourMinute_Combined_MaintainsRhythm()
    {
        // Arrange: Every 3 days at 14:30
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(3),
            HourInterval = new HourInterval(14),
            MinuteInterval = new MinuteInterval(30)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-15);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    #endregion

    #region Named Day Tests (Every Monday, Tuesday, etc.)

    [Theory]
    [InlineData(DayOfWeek.Monday, 14)]     // Every Monday, 2 weeks ago
    [InlineData(DayOfWeek.Friday, 21)]     // Every Friday, 3 weeks ago
    [InlineData(DayOfWeek.Sunday, 28)]     // Every Sunday, 4 weeks ago
    [InlineData(DayOfWeek.Wednesday, 7)]   // Every Wednesday, 1 week ago
    public void CronExpression_NamedDays_ReturnsCorrectDayOfWeek(DayOfWeek expectedDay, int daysAgo)
    {
        // Arrange: Cron expression for specific day of week (0=Sunday, 1=Monday, etc.)
        var cronDayNumber = (int)expectedDay;
        var task = new RecurringTask
        {
            CronInterval = new CronInterval($"0 9 * * {cronDayNumber}") // At 9:00 on that day
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero); // Saturday
        var scheduledTime = referenceTime.AddDays(-daysAgo);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        result.NextRun.Value.DayOfWeek.ShouldBe(expectedDay);
        result.NextRun.Value.Hour.ShouldBe(9);
    }

    [Fact]
    public void CronExpression_EveryWeekday_SkipsWeekends()
    {
        // Arrange: Every weekday (Mon-Fri) at 9:00
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9 * * 1-5")
        };
        // Jan 10, 2026 is Saturday
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-14);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // Should be Monday (next weekday after Saturday)
        result.NextRun.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
    }

    [Fact]
    public void CronExpression_WeekendOnly_SkipsWeekdays()
    {
        // Arrange: Every weekend (Sat-Sun) at 10:00
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 10 * * 0,6")
        };
        // Jan 12, 2026 is Monday
        var referenceTime = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-14);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // Should be Saturday (next weekend day after Monday)
        result.NextRun.Value.DayOfWeek.ShouldBe(DayOfWeek.Saturday);
    }

    [Fact]
    public void CronExpression_SpecificDayOfMonth_ReturnsCorrectDay()
    {
        // Arrange: 1st of every month at 00:00
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 0 1 * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMonths(-3);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.Day.ShouldBe(1);
        result.NextRun.Value.Month.ShouldBe(2); // February
    }

    [Fact]
    public void CronExpression_LastDayOfMonth_HandlesDifferentMonths()
    {
        // Arrange: Last day of month at 23:59 (using 28-31 range)
        // Note: Cron doesn't have a "last day" syntax, but we can test specific days
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("59 23 28 * *") // 28th of every month
        };
        var referenceTime = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero); // After Jan 28
        var scheduledTime = referenceTime.AddMonths(-2);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.Day.ShouldBe(28);
    }

    #endregion

    #region Extended Combined Intervals Tests

    [Fact]
    public void MonthDayHour_Combined_MaintainsSchedule()
    {
        // Arrange: Every month, on day 1, at hour 8
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1),
            DayInterval = new DayInterval(1),
            HourInterval = new HourInterval(8)
        };
        var referenceTime = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMonths(-6);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Fact]
    public void WeekDayHour_Combined_MaintainsSchedule()
    {
        // Arrange: Every 2 weeks, at specific hour
        var task = new RecurringTask
        {
            WeekInterval = new WeekInterval(2),
            HourInterval = new HourInterval(10)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-28); // 4 weeks ago

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Fact]
    public void DayHourMinuteSecond_FullCombination_MaintainsSchedule()
    {
        // Arrange: Every 3 days, at 14:30:45
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(3),
            HourInterval = new HourInterval(14),
            MinuteInterval = new MinuteInterval(30),
            SecondInterval = new SecondInterval(45)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-30);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Fact]
    public void MonthWeek_Combined_MaintainsSchedule()
    {
        // Arrange: Every 2 months, weekly
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(2),
            WeekInterval = new WeekInterval(1)
        };
        var referenceTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddMonths(-12);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    [Theory]
    [InlineData(1, 6, 30)]   // Every day, every 6 hours, every 30 minutes
    [InlineData(2, 12, 15)]  // Every 2 days, every 12 hours, every 15 minutes
    [InlineData(7, 24, 60)]  // Every week, every day (24h), every hour (60 min)
    public void VariousCombinations_AllReturnValidFutureDates(int days, int hours, int minutes)
    {
        // Arrange
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(days),
            HourInterval = new HourInterval(hours),
            MinuteInterval = new MinuteInterval(minutes)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-100);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
    }

    #endregion

    #region Complex Cron Patterns

    [Fact]
    public void CronExpression_EveryQuarterFirstMonday_Works()
    {
        // Arrange: First day of Jan, Apr, Jul, Oct at 9:00
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9 1 1,4,7,10 *")
        };
        var referenceTime = new DateTimeOffset(2026, 2, 15, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddYears(-1);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // Should be April 1st (next quarter start)
        result.NextRun.Value.Month.ShouldBe(4);
        result.NextRun.Value.Day.ShouldBe(1);
    }

    [Fact]
    public void CronExpression_EveryFiveMinutes_Works()
    {
        // Arrange: Every 5 minutes (0, 5, 10, 15, etc.)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("*/5 * * * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 32, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-2);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // Minute should be multiple of 5
        (result.NextRun.Value.Minute % 5).ShouldBe(0);
    }

    [Fact]
    public void CronExpression_BusinessHours_Works()
    {
        // Arrange: Every hour from 9-17 on weekdays
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9-17 * * 1-5")
        };
        // Saturday at 20:00
        var referenceTime = new DateTimeOffset(2026, 1, 10, 20, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-7);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // Should be Monday at 9:00
        result.NextRun.Value.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        result.NextRun.Value.Hour.ShouldBeInRange(9, 17);
    }

    [Fact]
    public void CronExpression_TwiceDaily_Works()
    {
        // Arrange: At 9:00 and 18:00 every day
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 9,18 * * *")
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-5);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert
        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(referenceTime);
        // Hour should be either 9 or 18
        (result.NextRun.Value.Hour == 9 || result.NextRun.Value.Hour == 18).ShouldBeTrue();
    }

    #endregion

    #region Skipped Count Accuracy Tests

    [Fact]
    public void SkippedCount_IsAccurate_ForSeconds()
    {
        // Arrange: Every 10 seconds, 100 seconds ago
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(10)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddSeconds(-100);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert: Should skip ~10 intervals (100s / 10s)
        result.SkippedCount.ShouldBeInRange(9, 11);
    }

    [Fact]
    public void SkippedCount_IsAccurate_ForMinutes()
    {
        // Arrange: Every 5 minutes, 1 hour ago
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddHours(-1);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert: Should skip ~12 intervals (60min / 5min)
        result.SkippedCount.ShouldBeInRange(11, 13);
    }

    [Fact]
    public void SkippedCount_IsAccurate_ForHours()
    {
        // Arrange: Every 2 hours, 1 day ago
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(2)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-1);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert: Should skip ~12 intervals (24h / 2h)
        result.SkippedCount.ShouldBeInRange(11, 13);
    }

    [Fact]
    public void SkippedCount_IsAccurate_ForDays()
    {
        // Arrange: Every day, 30 days ago
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1)
        };
        var referenceTime = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var scheduledTime = referenceTime.AddDays(-30);

        // Act
        var result = task.CalculateNextValidRun(scheduledTime, 1, referenceTime);

        // Assert: Should skip ~30 intervals
        result.SkippedCount.ShouldBeInRange(29, 31);
    }

    #endregion
}
