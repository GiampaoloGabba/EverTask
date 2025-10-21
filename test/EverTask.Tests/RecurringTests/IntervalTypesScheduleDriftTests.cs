using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Tests verifying that all interval types correctly maintain schedule
/// and prevent drift when calculating next run times.
/// Related to schedule drift fix - see docs/test-plan-schedule-drift-fix.md
/// </summary>
public class IntervalTypesScheduleDriftTests
{
    #region SecondInterval Tests

    [Fact]
    public void SecondInterval_Should_Maintain_Seconds_With_Delays()
    {
        // Arrange: Task runs every 30 seconds
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(30)
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run from scheduled time (not current time)
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be exactly 30 seconds later
        var expectedNextRun = scheduledTime.AddSeconds(30);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void SecondInterval_Should_Skip_Past_Occurrences()
    {
        // Arrange: Task runs every 10 seconds
        var task = new RecurringTask
        {
            SecondInterval = new SecondInterval(10)
        };

        // Scheduled 2 minutes ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddMinutes(-2);

        // Act: Calculate next valid run
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip past occurrences (approximately 12 skips: 120 seconds / 10)
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
        Assert.True(result.SkippedCount >= 10);
        Assert.True(result.SkippedCount <= 14); // Allow tolerance
    }

    #endregion

    #region MinuteInterval Tests

    [Fact]
    public void MinuteInterval_Should_Maintain_Minutes_With_Delays()
    {
        // Arrange: Task runs every 15 minutes
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(15)
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be exactly 15 minutes later
        var expectedNextRun = scheduledTime.AddMinutes(15);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void MinuteInterval_Should_Respect_OnSecond()
    {
        // Arrange: Task runs every 10 minutes at :30 seconds
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10) { OnSecond = 30 }
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 30, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should maintain :30 seconds
        Assert.NotNull(nextRun);
        Assert.Equal(30, nextRun.Value.Second);
    }

    [Fact]
    public void MinuteInterval_Should_Skip_Past_Occurrences()
    {
        // Arrange: Task runs every 5 minutes
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(5)
        };

        // Scheduled 30 minutes ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip approximately 6 occurrences (30 / 5 = 6)
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
        Assert.True(result.SkippedCount >= 5);
        Assert.True(result.SkippedCount <= 7);
    }

    #endregion

    #region HourInterval Tests

    [Fact]
    public void HourInterval_Should_Maintain_Hours_With_Delays()
    {
        // Arrange: Task runs every 2 hours
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(2)
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be exactly 2 hours later
        var expectedNextRun = scheduledTime.AddHours(2);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void HourInterval_Should_Respect_OnMinute_And_OnSecond()
    {
        // Arrange: Task runs every hour at :15:30 (15 minutes, 30 seconds)
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1) { OnMinute = 15, OnSecond = 30 }
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 15, 30, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should maintain :15:30
        Assert.NotNull(nextRun);
        Assert.Equal(15, nextRun.Value.Minute);
        Assert.Equal(30, nextRun.Value.Second);
    }

    [Fact]
    public void HourInterval_Should_Respect_OnHours_Array()
    {
        // Arrange: Task runs at specific hours (9 AM, 12 PM, 3 PM, 6 PM)
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1) { OnHours = new[] { 9, 12, 15, 18 } }
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero);

        // Act: Calculate next runs
        var nextRun1 = task.CalculateNextRun(scheduledTime, 1);
        var nextRun2 = task.CalculateNextRun(nextRun1!.Value, 2);
        var nextRun3 = task.CalculateNextRun(nextRun2!.Value, 3);

        // Assert: Should hit specific hours
        Assert.NotNull(nextRun1);
        Assert.Equal(12, nextRun1.Value.Hour);

        Assert.NotNull(nextRun2);
        Assert.Equal(15, nextRun2.Value.Hour);

        Assert.NotNull(nextRun3);
        Assert.Equal(18, nextRun3.Value.Hour);
    }

    [Fact]
    public void HourInterval_Should_Skip_Past_Occurrences()
    {
        // Arrange: Task runs every hour
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1)
        };

        // Scheduled 5 hours ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddHours(-5);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip approximately 5 occurrences
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
        Assert.True(result.SkippedCount >= 4);
        Assert.True(result.SkippedCount <= 6);
    }

    #endregion

    #region DayInterval Tests

    [Fact]
    public void DayInterval_Should_Maintain_Day_With_OnTimes()
    {
        // Arrange: Task runs daily at 9:00 AM
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = new[] { new TimeOnly(9, 0, 0) } }
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be next day at 9:00 AM
        var expectedNextRun = new DateTimeOffset(2024, 1, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNextRun, nextRun);
    }

    [Fact]
    public void DayInterval_Should_Respect_OnDays()
    {
        // Arrange: Task runs on weekdays (Monday-Friday) at 10:00 AM
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1)
            {
                OnDays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                OnTimes = new[] { new TimeOnly(10, 0, 0) }
            }
        };

        // Start on Friday
        var friday = new DateTimeOffset(2024, 1, 5, 10, 0, 0, TimeSpan.Zero); // Jan 5, 2024 is Friday

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(friday, 1);

        // Assert: Should skip weekend and land on Monday
        Assert.NotNull(nextRun);
        Assert.Equal(DayOfWeek.Monday, nextRun.Value.DayOfWeek);
        Assert.Equal(10, nextRun.Value.Hour);
    }

    [Fact]
    public void DayInterval_Should_Skip_Past_Occurrences()
    {
        // Arrange: Task runs daily at 10:00 AM
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = new[] { new TimeOnly(10, 0, 0) } }
        };

        // Scheduled 7 days ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddDays(-7);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip approximately 7 occurrences
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
        Assert.True(result.SkippedCount >= 6);
        Assert.True(result.SkippedCount <= 8);
    }

    #endregion

    #region MonthInterval Tests

    [Fact]
    public void MonthInterval_Should_Respect_OnDay()
    {
        // Arrange: Task runs monthly on the 15th
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 15 }
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 15, 9, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be Feb 15
        Assert.NotNull(nextRun);
        Assert.Equal(2, nextRun.Value.Month);
        Assert.Equal(15, nextRun.Value.Day);
    }

    [Fact]
    public void MonthInterval_Should_Respect_OnFirst()
    {
        // Arrange: Task runs on first Monday of each month
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1)
            {
                OnFirst = DayOfWeek.Monday
            }
        };

        // Start on first Monday of January 2024 (Jan 1, 2024 is Monday)
        var firstMondayJan = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(firstMondayJan, 1);

        // Assert: Should be first Monday of February (Feb 5, 2024)
        Assert.NotNull(nextRun);
        Assert.Equal(2, nextRun.Value.Month);
        Assert.Equal(DayOfWeek.Monday, nextRun.Value.DayOfWeek);
        Assert.True(nextRun.Value.Day <= 7); // Should be in first week
    }

    [Fact]
    public void MonthInterval_Should_Handle_Different_Month_Lengths()
    {
        // Arrange: Task runs monthly on the 31st
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 31 }
        };

        // Start on Jan 31
        var jan31 = new DateTimeOffset(2024, 1, 31, 9, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(jan31, 1);

        // Assert: February has only 29 days (2024 is leap year), so it clamps to Feb 29
        // This is the expected behavior - "monthly on 31st" means "last day of month" for months with <31 days
        Assert.NotNull(nextRun);
        Assert.Equal(2, nextRun.Value.Month); // February
        Assert.Equal(29, nextRun.Value.Day); // Last day of Feb in leap year
    }

    [Fact]
    public void MonthInterval_Should_Handle_LeapYear()
    {
        // Arrange: Task runs monthly on the 29th
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 29 }
        };

        // Start on Jan 29, 2024 (leap year)
        var jan29 = new DateTimeOffset(2024, 1, 29, 9, 0, 0, TimeSpan.Zero);

        // Act: Calculate next run
        var nextRun = task.CalculateNextRun(jan29, 1);

        // Assert: Feb 2024 has 29 days (leap year)
        Assert.NotNull(nextRun);
        Assert.Equal(2, nextRun.Value.Month);
        Assert.Equal(29, nextRun.Value.Day);
    }

    [Fact]
    public void MonthInterval_Should_Skip_Past_Occurrences()
    {
        // Arrange: Task runs monthly on the 1st
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 1 }
        };

        // Scheduled 6 months ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddMonths(-6);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip approximately 6 occurrences
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
        Assert.True(result.SkippedCount >= 5);
        Assert.True(result.SkippedCount <= 7);
    }

    #endregion

    #region CronInterval Tests

    [Fact]
    public void CronInterval_Standard_5Field_Should_Work()
    {
        // Arrange: Cron expression "0 * * * *" (every hour at :00)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 * * * *")
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 0, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be 15:00 (next hour)
        Assert.NotNull(nextRun);
        Assert.Equal(15, nextRun.Value.Hour);
        Assert.Equal(0, nextRun.Value.Minute);
    }

    [Fact]
    public void CronInterval_With_Seconds_6Field_Should_Work()
    {
        // Arrange: Cron expression "30 * * * * *" (every minute at :30 seconds)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("30 * * * * *")
        };

        var scheduledTime = new DateTimeOffset(2024, 1, 1, 14, 0, 30, TimeSpan.Zero);

        // Act
        var nextRun = task.CalculateNextRun(scheduledTime, 1);

        // Assert: Should be 14:01:30 (next minute)
        Assert.NotNull(nextRun);
        Assert.Equal(14, nextRun.Value.Hour);
        Assert.Equal(1, nextRun.Value.Minute);
        Assert.Equal(30, nextRun.Value.Second);
    }

    [Fact]
    public void CronInterval_Should_Skip_Past_Occurrences()
    {
        // Arrange: Cron expression "*/10 * * * * *" (every 10 seconds)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("*/10 * * * * *")
        };

        // Scheduled 2 minutes ago
        var scheduledInPast = DateTimeOffset.UtcNow.AddMinutes(-2);

        // Act
        var result = task.CalculateNextValidRun(scheduledInPast, 1);

        // Assert: Should skip approximately 12 occurrences (120 seconds / 10)
        Assert.NotNull(result.NextRun);
        Assert.True(result.NextRun.Value >= DateTimeOffset.UtcNow);
        Assert.True(result.SkippedCount >= 10);
        Assert.True(result.SkippedCount <= 14);
    }

    [Fact]
    public void CronInterval_Complex_Expression_Should_Calculate_Correctly()
    {
        // Arrange: Cron "0 0 9-17 * * MON-FRI" (every hour from 9 AM to 5 PM, weekdays)
        var task = new RecurringTask
        {
            CronInterval = new CronInterval("0 0 9-17 * * MON-FRI")
        };

        // Start on Monday at 9:00 AM
        var monday9am = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero); // Jan 1, 2024 is Monday

        // Act
        var nextRun = task.CalculateNextRun(monday9am, 1);

        // Assert: Should be 10:00 AM same day
        Assert.NotNull(nextRun);
        Assert.Equal(10, nextRun.Value.Hour);
        Assert.Equal(DayOfWeek.Monday, nextRun.Value.DayOfWeek);
    }

    #endregion
}
